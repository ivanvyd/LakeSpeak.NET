using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using LakeSpeak.Genie.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeSpeak.Genie;

/// <inheritdoc cref="IGenieClient"/>
public sealed partial class GenieClient : IGenieClient
{
    private const string Root = "api/2.0/genie/spaces";

    private readonly GenieClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GenieClient> _logger;
    private readonly GenieTransport _transport;

    public GenieClient(
        HttpClient http,
        IOptions<GenieClientOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<GenieClient>? logger = null)
    {
        _options = options.Value;
        _options.Validate();
        // Injected so polling tests advance a fake clock instead of sleeping. A polling loop
        // tested with real delays is either slow or flaky, usually both.
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GenieClient>.Instance;
        _transport = _options.StrictResults is { } limits
            ? new StrictGenieTransport(http, _options, _time, _logger, limits)
            : new LegacyGenieTransport(http, _options, _time, _logger);
    }

    public async Task<GenieAgentPage> ListAgentsAsync(
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Root}?page_size={_options.PageSize}";
        if (!string.IsNullOrEmpty(pageToken))
        {
            url += $"&page_token={Uri.EscapeDataString(pageToken)}";
        }

        var wire = await _transport.GetAsync<SpaceListWire>(url, cancellationToken).ConfigureAwait(false);

        var agents = (wire.Spaces ?? [])
            .Where(s => !string.IsNullOrEmpty(s.SpaceId))
            .Select(s => new GenieAgent(s.SpaceId!, s.Title ?? s.SpaceId!, s.Description, s.WarehouseId))
            .ToList();

        return new GenieAgentPage(agents, wire.NextPageToken);
    }

    public async IAsyncEnumerable<GenieAgent> ListAllAgentsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? token = null;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);

        do
        {
            var page = await ListAgentsAsync(token, cancellationToken).ConfigureAwait(false);
            foreach (var agent in page.Agents)
            {
                yield return agent;
            }

            token = page.NextPageToken;

            // A server that returns the same token forever would otherwise spin here until the
            // process is killed.
            if (token is not null && !seenTokens.Add(token))
            {
                LogRepeatedPageToken(_logger);
                yield break;
            }
        }
        while (!string.IsNullOrEmpty(token));
    }

    public async Task<GenieAgent> GetAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var wire = await _transport.GetAsync<SpaceWire>($"{Root}/{Esc(agentId)}", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(wire.SpaceId))
        {
            throw new GenieException(GenieFailureKind.MalformedResponse, "Agent response contained no space_id.");
        }

        _transport.ValidateAgent(wire, agentId);
        return new GenieAgent(wire.SpaceId, wire.Title ?? wire.SpaceId, wire.Description, wire.WarehouseId);
    }

    public async Task<(GenieConversationRef Conversation, string MessageId)> StartConversationAsync(
        string agentId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var wire = await _transport.PostAsync<StartConversationRequestWire, StartConversationWire>(
            $"{Root}/{Esc(agentId)}/start-conversation",
            new StartConversationRequestWire { Content = question },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(wire.ConversationId) || string.IsNullOrEmpty(wire.MessageId))
        {
            throw new GenieException(
                GenieFailureKind.MalformedResponse,
                "Start-conversation response was missing conversation_id or message_id.");
        }

        _transport.ValidateStartConversation(wire, agentId, wire.ConversationId, wire.MessageId);
        return (new GenieConversationRef(agentId, wire.ConversationId, wire.Conversation?.Title), wire.MessageId);
    }

    public async Task<string> SendMessageAsync(
        string agentId,
        string conversationId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var wire = await _transport.PostAsync<StartConversationRequestWire, MessageWire>(
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages",
            new StartConversationRequestWire { Content = question },
            cancellationToken).ConfigureAwait(false);

        var messageId = wire.MessageId ?? wire.Id
            ?? throw new GenieException(GenieFailureKind.MalformedResponse, "Message response contained no message_id.");
        _transport.ValidateMessage(wire, agentId, conversationId, messageId);
        return messageId;
    }

    public async Task<GenieResponse> GetMessageAsync(
        string agentId,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var wire = await _transport.GetAsync<MessageWire>(
            MessageUrl(agentId, conversationId, messageId), cancellationToken).ConfigureAwait(false);

        return Normalize(wire, agentId, conversationId, messageId, TimeSpan.Zero, pollCount: 0);
    }

    public async Task<GenieResponse> WaitForResponseAsync(
        string agentId,
        string conversationId,
        string messageId,
        GenieAskOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GenieAskOptions();
        var timeout = options.Timeout ?? _options.PollingTimeout;
        GenieClientOptions.ValidateWaitTimeout(timeout, nameof(options.Timeout));
        var started = _time.GetTimestamp();
        var interval = _options.InitialPollInterval;
        var polls = 0;
        GenieMessageState? lastState = null;
        GenieResponse? last = null;

        using var deadlineCancellation = new CancellationTokenSource(timeout, _time);
        using var pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCancellation.Token);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var elapsed = _time.GetElapsedTime(started);
                if (elapsed >= timeout)
                {
                    throw PollingTimedOut(timeout, lastState, last);
                }

                var wire = await _transport.GetAsync<MessageWire>(
                    MessageUrl(agentId, conversationId, messageId), pollingCancellation.Token).ConfigureAwait(false);

                polls++;
                last = Normalize(wire, agentId, conversationId, messageId, _time.GetElapsedTime(started), polls);

                // A handler can complete after cancellation was requested, so the clock remains
                // authoritative even when the transport returns a terminal response.
                elapsed = _time.GetElapsedTime(started);
                if (elapsed >= timeout)
                {
                    throw PollingTimedOut(timeout, last.State, last);
                }

                if (last.State != lastState)
                {
                    lastState = last.State;
                    options.OnStateChanged?.Invoke(last.State);
                    LogStateChanged(_logger, messageId, last.State);
                }

                if (last.State.IsTerminal())
                {
                    var error = _transport.ReportedError(wire);
                    return last.State switch
                    {
                        GenieMessageState.Failed => throw new GenieException(
                            GenieFailureKind.MessageFailed,
                            error?.Error is { Length: > 0 } e
                                ? $"Genie could not answer: {e}"
                                : "Genie could not answer the question.",
                            errorCode: error?.Type,
                            lastKnownResponse: last),

                        GenieMessageState.Cancelled => throw new GenieException(
                            GenieFailureKind.MessageCancelled,
                            "The Genie message was cancelled.",
                            lastKnownResponse: last),

                        _ => last,
                    };
                }

                var remaining = timeout - elapsed;
                var delay = interval <= remaining ? interval : remaining;
                await Task.Delay(delay, _time, pollingCancellation.Token).ConfigureAwait(false);

                // Back off gently. Genie questions are usually seconds, occasionally minutes; a
                // fixed 1s interval is wasteful for the long tail and a fixed 5s makes the common
                // case feel sluggish.
                interval = TimeSpan.FromMilliseconds(
                    Math.Min(interval.TotalMilliseconds * 1.5, _options.MaxPollInterval.TotalMilliseconds));
            }
        }
        catch (OperationCanceledException) when (
            deadlineCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw PollingTimedOut(timeout, lastState, last);
        }
    }

    private static GenieException PollingTimedOut(
        TimeSpan timeout,
        GenieMessageState? lastState,
        GenieResponse? last) =>
        new(
            GenieFailureKind.PollingTimeout,
            $"Genie did not finish within {timeout.TotalSeconds:F0}s (last state: {lastState?.ToString() ?? "none"}). " +
            "The question may still complete; it can be re-read with its message id.",
            lastKnownResponse: last);

    public Task<GenieQueryResult?> GetQueryResultAsync(
        string agentId, string conversationId, string messageId, string attachmentId,
        CancellationToken cancellationToken = default)
        => _transport.GetQueryResultAsync(
            AttachmentUrl(agentId, conversationId, messageId, attachmentId, "query-result"), cancellationToken);

    public Task<GenieQueryResult?> ReExecuteQueryAsync(
        string agentId, string conversationId, string messageId, string attachmentId,
        CancellationToken cancellationToken = default)
        => _transport.ReExecuteQueryAsync(
            AttachmentUrl(agentId, conversationId, messageId, attachmentId, "execute-query"),
            AttachmentUrl(agentId, conversationId, messageId, attachmentId, "query-result"),
            cancellationToken);

    public async Task SendFeedbackAsync(
        string agentId,
        string conversationId,
        string messageId,
        GenieFeedbackRating rating,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        // Databricks rejects a comment alongside a NONE rating with an HTTP 400 whose message is
        // easy to misread as a transport problem. The constraint is undocumented and was found by
        // calling the endpoint; catching it here turns a confusing server error into a clear one,
        // and costs a round trip nobody wanted.
        if (rating == GenieFeedbackRating.None && !string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException(
                "Databricks only accepts a feedback comment alongside a positive or negative rating. " +
                "Either choose a rating, or send the rating without a comment.",
                nameof(comment));
        }

        var body = new FeedbackRequestWire
        {
            Rating = rating switch
            {
                GenieFeedbackRating.Positive => "POSITIVE",
                GenieFeedbackRating.Negative => "NEGATIVE",
                GenieFeedbackRating.None => "NONE",
                _ => throw new ArgumentOutOfRangeException(nameof(rating)),
            },
            Comment = comment,
        };

        using var response = await _transport.SendAsync(
            HttpMethod.Post,
            $"{MessageUrl(agentId, conversationId, messageId)}/feedback",
            JsonContent.Create(body, options: GenieTransport.Json),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenieResponse> AskAsync(
        string agentId,
        string question,
        GenieAskOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAskOptions(options);
        var (conversation, messageId) =
            await StartConversationAsync(agentId, question, cancellationToken).ConfigureAwait(false);

        return await CompleteAsync(agentId, conversation.ConversationId, messageId, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GenieResponse> FollowUpAsync(
        string agentId,
        string conversationId,
        string question,
        GenieAskOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAskOptions(options);
        var messageId = await SendMessageAsync(agentId, conversationId, question, cancellationToken)
            .ConfigureAwait(false);

        return await CompleteAsync(agentId, conversationId, messageId, options, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GenieResponse> CompleteAsync(
        string agentId, string conversationId, string messageId,
        GenieAskOptions? options, CancellationToken cancellationToken)
    {
        options ??= new GenieAskOptions();

        var response = await WaitForResponseAsync(agentId, conversationId, messageId, options, cancellationToken)
            .ConfigureAwait(false);

        return options.IncludeQueryResult
            ? await _transport.AttachQueryResultsAsync(response, cancellationToken).ConfigureAwait(false)
            : response;
    }

    private static void ValidateAskOptions(GenieAskOptions? options)
    {
        if (options?.Timeout is { } timeout)
        {
            GenieClientOptions.ValidateWaitTimeout(timeout, nameof(options.Timeout));
        }
    }

    private GenieResponse Normalize(
        MessageWire wire, string agentId, string conversationId, string messageId,
        TimeSpan duration, int pollCount)
    {
        _transport.ValidateMessage(wire, agentId, conversationId, messageId);

        var attachments = wire.Attachments ?? [];

        // Genie can emit several text attachments; intermediate "thinking" phases are working
        // notes, not the answer. Joining every text blob would show the user Genie's scratchpad.
        var answer = attachments
            .Select(a => a.Text)
            .Where(t => t?.Content is { Length: > 0 }
                        && t.Purpose != "FOLLOW_UP_QUESTION"
                        && t.Phase != "RESPONSE_PHASE_THINKING")
            .Select(t => t!.Content!)
            .LastOrDefault();

        var queryAttachments = attachments.Where(a => a.Query is not null)
            .Select(a => new GenieQueryAttachment(a.AttachmentId, NormalizeQuery(a.Query!), null)
            {
                IsTruncated = a.Query!.ResultMetadata?.IsTruncated ?? false,
            }).ToList();

        var suggested = attachments
            .SelectMany(a => a.SuggestedQuestions?.Questions ?? [])
            .ToList();

        return new GenieResponse(
            agentId,
            conversationId,
            messageId,
            GenieMessageStateExtensions.FromWire(wire.Status),
            answer,
            Query: null,
            Result: null,
            suggested,
            new GenieResponseMetadata(
                duration,
                pollCount,
                FromUnixTimestamp(wire.CreatedTimestamp),
                FromUnixTimestamp(wire.LastUpdatedTimestamp)))
        {
            HasVisualization = attachments.Any(a => a.Viz is not null),
        }.WithQueryAttachments(queryAttachments);
    }

    private static GenieQuery NormalizeQuery(QueryAttachmentWire q) => new(
        q.Query, q.Title, q.Description, q.StatementId,
        q.Parameters?.Select(p => new GenieQueryParameter(p.Keyword, p.SqlType, p.Value)).ToList());

    /// <summary>
    /// Converts a Genie timestamp, detecting its unit by magnitude.
    /// </summary>
    /// <remarks>
    /// The field is typed <c>int64</c> and its unit is undocumented — the SDK name suggests
    /// milliseconds, while the one published example is ten digits, which is seconds. Assuming
    /// milliseconds on a seconds value yields January 1970 instead of the real date: a silent
    /// 55-year error on a public property. The threshold is the year 2001 in milliseconds; any
    /// plausible Genie timestamp in seconds is far below it, and any in milliseconds far above.
    /// </remarks>
    private static DateTimeOffset? FromUnixTimestamp(long? value)
    {
        if (value is null or <= 0)
        {
            return null;
        }

        const long millisecondThreshold = 100_000_000_000L;

        return value.Value >= millisecondThreshold
            ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value)
            : DateTimeOffset.FromUnixTimeSeconds(value.Value);
    }

    internal static string Esc(string segment) => Uri.EscapeDataString(segment);

    private static string MessageUrl(string agentId, string conversationId, string messageId) =>
        $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}";

    internal static string AttachmentUrl(
        string agentId, string conversationId, string messageId, string attachmentId, string action) =>
        $"{MessageUrl(agentId, conversationId, messageId)}/attachments/{Esc(attachmentId)}/{action}";

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Agent listing returned a repeated page token; stopping pagination.")]
    private static partial void LogRepeatedPageToken(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Genie message {MessageId} entered state {State}.")]
    private static partial void LogStateChanged(ILogger logger, string messageId, GenieMessageState state);
}
