using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LakeSpeak.Genie.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace LakeSpeak.Genie;

/// <inheritdoc cref="IGenieClient"/>
public sealed partial class GenieClient : IGenieClient
{
    private const string Root = "api/2.0/genie/spaces";

    /// <summary>
    /// Hard bound on chunk requests for one result, independent of how many rows each carries.
    /// </summary>
    /// <remarks>
    /// A chunk observed live held roughly 41,000 rows, so even a caller raising
    /// <see cref="GenieClientOptions.MaxResultRows"/> into the millions stays far inside this.
    /// It exists for the case the row cap cannot see: chunks that carry no rows at all.
    /// </remarks>
    internal const int MaxChunkFetches = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly GenieClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GenieClient> _logger;
    private readonly GenieResultLimits? _strictLimits;

    public GenieClient(
        HttpClient http,
        IOptions<GenieClientOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<GenieClient>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _options.Validate();
        _strictLimits = _options.StrictResults;
        // Injected so polling tests advance a fake clock instead of sleeping. A polling loop
        // tested with real delays is either slow or flaky, usually both.
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GenieClient>.Instance;
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

        var wire = await GetAsync<SpaceListWire>(url, cancellationToken).ConfigureAwait(false);

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
        var wire = await GetAsync<SpaceWire>($"{Root}/{Esc(agentId)}", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(wire.SpaceId))
        {
            throw new GenieException(GenieFailureKind.MalformedResponse, "Agent response contained no space_id.");
        }

        if (_strictLimits is not null)
        {
            ValidateIdentity(wire.SpaceId, agentId);
        }

        return new GenieAgent(wire.SpaceId, wire.Title ?? wire.SpaceId, wire.Description, wire.WarehouseId);
    }

    public async Task<(GenieConversationRef Conversation, string MessageId)> StartConversationAsync(
        string agentId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var wire = await PostAsync<StartConversationRequestWire, StartConversationWire>(
            $"{Root}/{Esc(agentId)}/start-conversation",
            new StartConversationRequestWire { Content = question },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(wire.ConversationId) || string.IsNullOrEmpty(wire.MessageId))
        {
            throw new GenieException(
                GenieFailureKind.MalformedResponse,
                "Start-conversation response was missing conversation_id or message_id.");
        }

        if (_strictLimits is not null)
        {
            ValidateIdentity(wire.SpaceId?.GetString(), agentId);
            ValidateIdentity(wire.Conversation?.SpaceId, agentId);
            ValidateIdentity(wire.Conversation?.ConversationId, wire.ConversationId);
            if (wire.Message is { } message)
            {
                ValidateMessageIdentity(message, agentId, wire.ConversationId, wire.MessageId);
            }
        }

        return (new GenieConversationRef(agentId, wire.ConversationId, wire.Conversation?.Title), wire.MessageId);
    }

    public async Task<string> SendMessageAsync(
        string agentId,
        string conversationId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var wire = await PostAsync<StartConversationRequestWire, MessageWire>(
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages",
            new StartConversationRequestWire { Content = question },
            cancellationToken).ConfigureAwait(false);

        var messageId = wire.MessageId ?? wire.Id
            ?? throw new GenieException(GenieFailureKind.MalformedResponse, "Message response contained no message_id.");
        if (_strictLimits is not null)
        {
            ValidateMessageIdentity(wire, agentId, conversationId, messageId);
        }

        return messageId;
    }

    public async Task<GenieResponse> GetMessageAsync(
        string agentId,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var wire = await GetAsync<MessageWire>(
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

                var wire = await GetAsync<MessageWire>(
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
                    return last.State switch
                    {
                        GenieMessageState.Failed => throw new GenieException(
                            GenieFailureKind.MessageFailed,
                            _strictLimits is null && wire.Error?.Error is { Length: > 0 } e
                                ? $"Genie could not answer: {e}"
                                : "Genie could not answer the question.",
                            errorCode: _strictLimits is null ? wire.Error?.Type : null,
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
        => FetchQueryResultAsync(
            AttachmentUrl(agentId, conversationId, messageId, attachmentId, "query-result"),
            HttpMethod.Get, cancellationToken);

    public async Task<GenieQueryResult?> ReExecuteQueryAsync(
        string agentId, string conversationId, string messageId, string attachmentId,
        CancellationToken cancellationToken = default)
    {
        var started = _time.GetTimestamp();
        var timeout = _options.PollingTimeout;
        var interval = _options.InitialPollInterval;
        var budget = _strictLimits is null ? null : new ResultBudget(_strictLimits);
        using var deadlineCancellation = new CancellationTokenSource(timeout, _time);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadlineCancellation.Token);

        try
        {
            // execute-query can acknowledge PENDING without rows. Poll query-result until the
            // result is available, keeping the initial request and chunk fetches inside the wait.
            var result = await FetchQueryResultAsync(
                AttachmentUrl(agentId, conversationId, messageId, attachmentId, "execute-query"),
                HttpMethod.Post,
                executionCancellation.Token, budget).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elapsed = _time.GetElapsedTime(started);
                if (elapsed >= timeout)
                {
                    break;
                }

                if (result is not null)
                {
                    return result;
                }

                var remaining = timeout - elapsed;
                var delay = interval <= remaining ? interval : remaining;
                await Task.Delay(delay, _time, executionCancellation.Token).ConfigureAwait(false);
                if (_time.GetElapsedTime(started) >= timeout)
                {
                    break;
                }

                result = await FetchQueryResultAsync(
                    AttachmentUrl(agentId, conversationId, messageId, attachmentId, "query-result"),
                    HttpMethod.Get, executionCancellation.Token, budget).ConfigureAwait(false);
                interval = TimeSpan.FromMilliseconds(
                    Math.Min(interval.TotalMilliseconds * 1.5, _options.MaxPollInterval.TotalMilliseconds));
            }
        }
        catch (OperationCanceledException) when (
            deadlineCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // An expired cached result stays unavailable when its re-execution times out.
        }

        LogReExecuteTimedOut(_logger, timeout.TotalSeconds);
        if (_strictLimits is not null)
        {
            throw StrictIncomplete();
        }

        return null;
    }

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

        using var response = await SendAsync(
            HttpMethod.Post,
            $"{MessageUrl(agentId, conversationId, messageId)}/feedback",
            JsonContent.Create(body, options: Json),
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

        if (_strictLimits is not null && options.IncludeQueryResult && response.QueryAttachments.Count > 0)
        {
            if (response.State != GenieMessageState.Completed)
            {
                throw StrictIncomplete();
            }

            var budget = new ResultBudget(_strictLimits);
            var queries = new List<GenieQueryAttachment>();
            foreach (var attachment in response.QueryAttachments)
            {
                // An omitted attachment id is valid message metadata, but leaves no way to fetch the table.
                if (attachment.IsTruncated || string.IsNullOrEmpty(attachment.AttachmentId))
                {
                    throw StrictIncomplete();
                }

                var table = await FetchStrictQueryResultAsync(
                    AttachmentUrl(agentId, conversationId, messageId, attachment.AttachmentId, "query-result"),
                    HttpMethod.Get, budget, cancellationToken, attachment.Query.StatementId).ConfigureAwait(false);
                queries.Add(attachment with { Result = table ?? throw StrictIncomplete() });
            }

            return response with { QueryAttachments = queries, Result = queries[0].Result };
        }

        if (!options.IncludeQueryResult || response.Query is null || response.Metadata.AttachmentId is null)
        {
            return response;
        }

        // A missing result must not discard a good answer: the text is the primary payload and
        // the table is an enrichment. Expired results are reported through State, which the
        // caller can act on by re-executing.
        if (response.State == GenieMessageState.QueryResultExpired)
        {
            return response;
        }

        var result = await GetQueryResultAsync(
            agentId, conversationId, messageId, response.Metadata.AttachmentId, cancellationToken)
            .ConfigureAwait(false);

        return response with
        {
            Result = result,
            QueryAttachments = response.QueryAttachments.Select((a, i) => i == 0 ? a with { Result = result } : a).ToList(),
        };
    }

    private static void ValidateAskOptions(GenieAskOptions? options)
    {
        if (options?.Timeout is { } timeout)
        {
            GenieClientOptions.ValidateWaitTimeout(timeout, nameof(options.Timeout));
        }
    }

    /// <summary>
    /// Resolves a server-supplied chunk link against the workspace and accepts it only if it
    /// still points at the workspace.
    /// </summary>
    /// <remarks>
    /// Validating the resolved host rather than the shape of the string is what makes this
    /// robust: <c>//evil.example.com/x</c> looks like a path and resolves to another host, and
    /// no amount of prefix-checking is provably free of the next such quirk. Databricks
    /// documents the link as opaque, so its shape is not ours to constrain — where it ends up is.
    /// </remarks>
    private bool ResolvesToWorkspace(string link, [NotNullWhen(true)] out Uri? resolved)
    {
        resolved = null;
        var workspace = _http.BaseAddress ?? _options.Host!;

        if (!Uri.TryCreate(workspace, link, out var candidate))
        {
            return false;
        }

        if (candidate.Scheme != workspace.Scheme
            || !candidate.Host.Equals(workspace.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != workspace.Port)
        {
            return false;
        }

        resolved = candidate;
        return true;
    }

    private GenieResponse Normalize(
        MessageWire wire, string agentId, string conversationId, string messageId,
        TimeSpan duration, int pollCount)
    {
        if (_strictLimits is not null)
        {
            ValidateMessageIdentity(wire, agentId, conversationId, messageId);
        }

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
        var firstQuery = queryAttachments.FirstOrDefault();

        var suggested = attachments
            .SelectMany(a => a.SuggestedQuestions?.Questions ?? [])
            .ToList();

        return new GenieResponse(
            agentId,
            conversationId,
            messageId,
            GenieMessageStateExtensions.FromWire(wire.Status),
            answer,
            firstQuery?.Query,
            Result: null,
            suggested,
            new GenieResponseMetadata(
                duration,
                pollCount,
                FromUnixTimestamp(wire.CreatedTimestamp),
                FromUnixTimestamp(wire.LastUpdatedTimestamp),
                firstQuery?.AttachmentId))
        {
            HasVisualization = attachments.Any(a => a.Viz is not null),
            QueryAttachments = queryAttachments,
        };
    }

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

    private static string Esc(string segment) => Uri.EscapeDataString(segment);

    private static string MessageUrl(string agentId, string conversationId, string messageId) =>
        $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}";

    private static string AttachmentUrl(
        string agentId, string conversationId, string messageId, string attachmentId, string action) =>
        $"{MessageUrl(agentId, conversationId, messageId)}/attachments/{Esc(attachmentId)}/{action}";

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TOut> PostAsync<TIn, TOut>(string url, TIn body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post, url, JsonContent.Create(body, options: Json), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<TOut>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request,
                _strictLimits is null ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutRejectedException ex)
        {
            throw NetworkFailure("The request to Databricks exceeded the configured resilience timeout.", ex);
        }
        catch (BrokenCircuitException ex)
        {
            throw NetworkFailure("Requests to Databricks are temporarily paused after repeated transient failures.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw NetworkFailure(
                _strictLimits is null
                    ? $"Could not reach the Databricks workspace: {ex.Message}"
                    : "Could not reach the Databricks workspace.",
                ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw NetworkFailure("The request to Databricks timed out.", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await ToFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    // Strict errors carry no transport exception details.
    private GenieException NetworkFailure(string message, Exception cause) =>
        new(GenieFailureKind.Network, message, innerException: _strictLimits is null ? cause : null);

    private async Task<GenieException> ToFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // The Genie error body shape is not documented, so parsing is best-effort and the
        // status code remains the source of truth for how to classify the failure. Strict mode
        // reports no server error text, so it never reads the body: an oversized or failing
        // body cannot then displace the status classification.
        ApiErrorWire? error = null;
        if (_strictLimits is null)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    error = JsonSerializer.Deserialize<ApiErrorWire>(body, Json);
                }
            }
            catch (JsonException)
            {
                // Not JSON. The status code below still classifies it.
            }
        }

        var status = (int)response.StatusCode;
        var detail = error?.Message is { Length: > 0 } m ? $": {m}" : ".";

        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => GenieFailureKind.Authentication,
            HttpStatusCode.Forbidden => GenieFailureKind.Authorization,
            HttpStatusCode.NotFound => GenieFailureKind.AgentNotFound,
            HttpStatusCode.TooManyRequests => GenieFailureKind.RateLimited,
            _ => GenieFailureKind.Unexpected,
        };

        var message = kind switch
        {
            GenieFailureKind.Authentication =>
                $"Databricks rejected the credentials (HTTP 401){detail} The OAuth token may have expired; " +
                "try `databricks auth login --profile <profile>`.",
            GenieFailureKind.Authorization =>
                $"The authenticated identity is not permitted to do this (HTTP 403){detail} " +
                "Genie Agent access and the underlying Unity Catalog grants are managed in Databricks.",
            GenieFailureKind.AgentNotFound =>
                $"Not found (HTTP 404){detail} The Agent, conversation or message may not exist, " +
                "or may be in a different workspace than the configured profile.",
            GenieFailureKind.RateLimited =>
                $"Databricks is rate limiting this client (HTTP 429){detail}",
            _ => $"Databricks returned HTTP {status}{detail}",
        };

        return new GenieException(kind, message, status, error?.ErrorCode);
    }

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

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Stopped after {Rows} rows, at the MaxResultRows limit of {Limit}. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkCapReached(ILogger logger, int rows, int limit);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Refused a next-chunk link that was not a workspace-relative path. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkLinkRejected(ILogger logger);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "A result chunk advertised a further chunk but carried no link to it. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkLinkMissing(ILogger logger);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "A result chunk link repeated one already followed; stopping to avoid looping. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkLinkRepeated(ILogger logger);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "Stopped after {Limit} chunk requests for a single result. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkFetchLimitReached(ILogger logger, int limit);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Warning,
        Message = "A re-executed query did not produce a result within {Seconds}s.")]
    private static partial void LogReExecuteTimedOut(ILogger logger, double seconds);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Could not fetch a further result chunk ({Kind}). " +
                  "The rows already retrieved are returned and reported as truncated.")]
    private static partial void LogChunkFetchFailed(ILogger logger, GenieFailureKind kind);

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (_strictLimits is not null)
        {
            return await ReadStrictAsync<T>(response, new ResultBudget(_strictLimits), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var value = await response.Content
                .ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);

            return value ?? throw new GenieException(
                GenieFailureKind.MalformedResponse, "Databricks returned an empty response body.");
        }
        catch (JsonException ex)
        {
            // The body is deliberately not included: it can contain query results, which are
            // governed data, and this message may reach a log or a bug report.
            throw new GenieException(
                GenieFailureKind.MalformedResponse,
                "Databricks returned a response this client could not parse.",
                innerException: ex);
        }
    }
}
