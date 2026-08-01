using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LakeSpeak.Genie.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeSpeak.Genie;

/// <inheritdoc cref="IGenieClient"/>
public sealed partial class GenieClient : IGenieClient
{
    private const string Root = "api/2.0/genie/spaces";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly GenieClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GenieClient> _logger;

    public GenieClient(
        HttpClient http,
        IOptions<GenieClientOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<GenieClient>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _options.Validate();
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

        return wire.MessageId ?? wire.Id
            ?? throw new GenieException(GenieFailureKind.MalformedResponse, "Message response contained no message_id.");
    }

    public async Task<GenieResponse> GetMessageAsync(
        string agentId,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var wire = await GetAsync<MessageWire>(
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}",
            cancellationToken).ConfigureAwait(false);

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
        var started = _time.GetTimestamp();
        var interval = _options.InitialPollInterval;
        var polls = 0;
        GenieMessageState? lastState = null;
        GenieResponse? last = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var elapsed = _time.GetElapsedTime(started);
            if (elapsed > timeout)
            {
                throw new GenieException(
                    GenieFailureKind.PollingTimeout,
                    $"Genie did not finish within {timeout.TotalSeconds:F0}s (last state: {lastState?.ToString() ?? "none"}). " +
                    "The question may still complete; it can be re-read with its message id.",
                    lastKnownResponse: last);
            }

            var wire = await GetAsync<MessageWire>(
                $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}",
                cancellationToken).ConfigureAwait(false);

            polls++;
            last = Normalize(wire, agentId, conversationId, messageId, _time.GetElapsedTime(started), polls);

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
                        wire.Error?.Error is { Length: > 0 } e
                            ? $"Genie could not answer: {e}"
                            : "Genie could not answer the question.",
                        errorCode: wire.Error?.Type,
                        lastKnownResponse: last),

                    GenieMessageState.Cancelled => throw new GenieException(
                        GenieFailureKind.MessageCancelled,
                        "The Genie message was cancelled.",
                        lastKnownResponse: last),

                    _ => last,
                };
            }

            await Task.Delay(interval, _time, cancellationToken).ConfigureAwait(false);

            // Back off gently. Genie questions are usually seconds, occasionally minutes; a
            // fixed 1s interval is wasteful for the long tail and a fixed 5s makes the common
            // case feel sluggish.
            interval = TimeSpan.FromMilliseconds(
                Math.Min(interval.TotalMilliseconds * 1.5, _options.MaxPollInterval.TotalMilliseconds));
        }
    }

    public Task<GenieQueryResult?> GetQueryResultAsync(
        string agentId, string conversationId, string messageId, string attachmentId,
        CancellationToken cancellationToken = default)
        => FetchQueryResultAsync(
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}/attachments/{Esc(attachmentId)}/query-result",
            HttpMethod.Get, cancellationToken);

    public Task<GenieQueryResult?> ReExecuteQueryAsync(
        string agentId, string conversationId, string messageId, string attachmentId,
        CancellationToken cancellationToken = default)
        => FetchQueryResultAsync(
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}/attachments/{Esc(attachmentId)}/execute-query",
            HttpMethod.Post, cancellationToken);

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
            $"{Root}/{Esc(agentId)}/conversations/{Esc(conversationId)}/messages/{Esc(messageId)}/feedback",
            JsonContent.Create(body, options: Json),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenieResponse> AskAsync(
        string agentId,
        string question,
        GenieAskOptions? options = null,
        CancellationToken cancellationToken = default)
    {
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

        return response with { Result = result };
    }

    private async Task<GenieQueryResult?> FetchQueryResultAsync(
        string url, HttpMethod method, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, url, content: null, cancellationToken).ConfigureAwait(false);

        var wire = await ReadAsync<QueryResultWire>(response, cancellationToken).ConfigureAwait(false);
        var statement = wire.StatementResponse;
        if (statement?.Manifest?.Schema?.Columns is not { } columns)
        {
            return null;
        }

        var cols = columns
            .Select(c => new GenieColumn(c.Name ?? string.Empty, c.TypeText ?? c.TypeName, c.TypeName))
            .ToList();

        var rows = statement.Result?.DataArray ?? [];

        // The Statement Execution contract chunks large results, and this client reads only the
        // first chunk. `manifest.truncated` does NOT cover that: it reports statement-level
        // truncation by Databricks, and is false for a result that is merely split. Reporting
        // only that flag would hand back the first chunk with truncated=false — a partial
        // result presented as complete, which is the one failure this client must never have.
        // Fetching the remaining chunks is v0.2 work; until then the shortfall is visible.
        var hasMoreChunks = statement.Result?.NextChunkIndex is not null
            || (statement.Manifest.TotalRowCount is { } total && rows.Count < total);

        return new GenieQueryResult(
            cols,
            rows,
            (statement.Manifest.Truncated ?? false) || hasMoreChunks,
            statement.Manifest.TotalRowCount);
    }

    private static GenieResponse Normalize(
        MessageWire wire, string agentId, string conversationId, string messageId,
        TimeSpan duration, int pollCount)
    {
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

        var queryAttachment = attachments.FirstOrDefault(a => a.Query is not null);

        var query = queryAttachment?.Query is { } q
            ? new GenieQuery(
                q.Query,
                q.Title,
                q.Description,
                q.StatementId,
                q.Parameters?.Select(p => new GenieQueryParameter(p.Keyword, p.SqlType, p.Value)).ToList())
            : null;

        var suggested = attachments
            .SelectMany(a => a.SuggestedQuestions?.Questions ?? [])
            .ToList();

        return new GenieResponse(
            agentId,
            conversationId,
            messageId,
            GenieMessageStateExtensions.FromWire(wire.Status),
            answer,
            query,
            Result: null,
            suggested,
            new GenieResponseMetadata(
                duration,
                pollCount,
                FromUnixMillis(wire.CreatedTimestamp),
                FromUnixMillis(wire.LastUpdatedTimestamp),
                queryAttachment?.AttachmentId))
        {
            HasVisualization = attachments.Any(a => a.Viz is not null),
        };
    }

    private static DateTimeOffset? FromUnixMillis(long? value)
        => value is null or 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    private static string Esc(string segment) => Uri.EscapeDataString(segment);

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
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GenieException(
                GenieFailureKind.Network, $"Could not reach the Databricks workspace: {ex.Message}",
                innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GenieException(
                GenieFailureKind.Network, "The request to Databricks timed out.", innerException: ex);
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

    private static async Task<GenieException> ToFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // The Genie error body shape is not documented, so parsing is best-effort and the
        // status code remains the source of truth for how to classify the failure.
        ApiErrorWire? error = null;
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

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
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
