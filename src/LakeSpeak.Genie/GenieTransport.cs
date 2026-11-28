using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LakeSpeak.Genie.Wire;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace LakeSpeak.Genie;

/// <summary>
/// The HTTP side of <see cref="GenieClient"/>. Legacy and strict result handling differ in how
/// bodies are read, how failures are reported and how results are fetched; the client picks one
/// implementation at construction so no call path has to ask which mode it is in.
/// </summary>
internal abstract partial class GenieTransport(
    HttpClient http, GenieClientOptions options, TimeProvider time, ILogger logger)
{
    /// <summary>
    /// Hard bound on chunk requests for one result, independent of how many rows each carries.
    /// </summary>
    /// <remarks>
    /// A chunk observed live held roughly 41,000 rows, so even a caller raising
    /// <see cref="GenieClientOptions.MaxResultRows"/> into the millions stays far inside this.
    /// It exists for the case the row cap cannot see: chunks that carry no rows at all.
    /// </remarks>
    internal const int MaxChunkFetches = 1000;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    protected delegate Task<GenieQueryResult?> ResultFetch(
        string url, HttpMethod method, CancellationToken cancellationToken);

    protected enum ChunkRefusal
    {
        Missing,
        Rejected,
        Repeated,
    }

    protected GenieClientOptions Options { get; } = options;

    protected TimeProvider Time { get; } = time;

    protected ILogger Logger { get; } = logger;

    protected abstract HttpCompletionOption CompletionOption { get; }

    internal virtual void ValidateAgent(SpaceWire wire, string agentId)
    {
    }

    internal virtual void ValidateStartConversation(
        StartConversationWire wire, string agentId, string conversationId, string messageId)
    {
    }

    internal virtual void ValidateMessage(MessageWire wire, string agentId, string conversationId, string messageId)
    {
    }

    /// <summary>The server's explanation of a failed message that may be reported to the caller.</summary>
    internal abstract MessageErrorWire? ReportedError(MessageWire wire);

    /// <summary>Fetches the query results <see cref="GenieAskOptions.IncludeQueryResult"/> asks for.</summary>
    internal abstract Task<GenieResponse> AttachQueryResultsAsync(
        GenieResponse response, CancellationToken cancellationToken);

    protected abstract Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken);

    protected abstract Task<ApiErrorWire?> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken);

    protected abstract GenieException NetworkFailure(string message, Exception cause);

    protected abstract GenieException Unreachable(HttpRequestException cause);

    /// <summary>Starts one result operation; every fetch it makes goes through the returned delegate.</summary>
    protected abstract ResultFetch BeginResultFetch();

    protected abstract GenieQueryResult? ReExecuteTimedOut();

    internal Task<GenieQueryResult?> GetQueryResultAsync(string url, CancellationToken cancellationToken) =>
        BeginResultFetch()(url, HttpMethod.Get, cancellationToken);

    internal async Task<GenieQueryResult?> ReExecuteQueryAsync(
        string executeUrl, string resultUrl, CancellationToken cancellationToken)
    {
        var fetch = BeginResultFetch();
        var started = Time.GetTimestamp();
        var timeout = Options.PollingTimeout;
        var interval = Options.InitialPollInterval;
        using var deadlineCancellation = new CancellationTokenSource(timeout, Time);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadlineCancellation.Token);

        try
        {
            // execute-query can acknowledge PENDING without rows. Poll query-result until the
            // result is available, keeping the initial request and chunk fetches inside the wait.
            var result = await fetch(executeUrl, HttpMethod.Post, executionCancellation.Token).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elapsed = Time.GetElapsedTime(started);
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
                await Task.Delay(delay, Time, executionCancellation.Token).ConfigureAwait(false);
                if (Time.GetElapsedTime(started) >= timeout)
                {
                    break;
                }

                result = await fetch(resultUrl, HttpMethod.Get, executionCancellation.Token).ConfigureAwait(false);
                interval = TimeSpan.FromMilliseconds(
                    Math.Min(interval.TotalMilliseconds * 1.5, Options.MaxPollInterval.TotalMilliseconds));
            }
        }
        catch (OperationCanceledException) when (
            deadlineCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // An expired cached result stays unavailable when its re-execution times out.
        }

        LogReExecuteTimedOut(Logger, timeout.TotalSeconds);
        return ReExecuteTimedOut();
    }

    internal async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TOut> PostAsync<TIn, TOut>(string url, TIn body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post, url, JsonContent.Create(body, options: Json), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<TOut>(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, CompletionOption, cancellationToken).ConfigureAwait(false);
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
            throw Unreachable(ex);
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

    private async Task<GenieException> ToFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
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

    protected static string ResultUrl(GenieResponse response, string attachmentId) =>
        GenieClient.AttachmentUrl(response.AgentId, response.ConversationId, response.MessageId, attachmentId, "query-result");

    protected static GenieColumn NormalizeColumn(ColumnWire c) => new(c.Name ?? string.Empty, c.TypeText ?? c.TypeName, c.TypeName);

    /// <summary>
    /// Resolves the link to the next result chunk, shared by the legacy and strict walkers so
    /// the two cannot disagree about which links are safe to follow.
    /// </summary>
    /// <remarks>
    /// On failure, <paramref name="refusal"/> names why the link was refused.
    /// </remarks>
    protected bool TryResolveNextChunk(
        string? link,
        int nextChunkIndex,
        string? statementId,
        HashSet<Uri> seenLinks,
        [NotNullWhen(true)] out Uri? chunkUri,
        out ChunkRefusal refusal)
    {
        chunkUri = null;
        refusal = default;
        if (link is not { Length: > 0 })
        {
            // Observed on the Genie query-result endpoint: it retains the statement id and
            // next index but omits the link that the SQL Statement endpoint returns. That
            // endpoint's path is documented. Uri normalizes dot-only segments even after
            // escaping, so those malformed ids cannot safely address a statement endpoint.
            if (statementId is not { Length: > 0 } || statementId is "." or "..")
            {
                refusal = ChunkRefusal.Missing;
                return false;
            }

            link = $"/api/2.0/sql/statements/{GenieClient.Esc(statementId)}/result/chunks/" +
                nextChunkIndex.ToString(CultureInfo.InvariantCulture);
        }

        if (!ResolvesToWorkspace(link, out var resolved) || resolved.UserInfo.Length != 0)
        {
            // The link is server-supplied and the bearer token rides on every workspace
            // request, so a link naming another host would disclose the token to it. The
            // link is not logged: its contents are attacker-chosen in exactly this case.
            refusal = ChunkRefusal.Rejected;
            return false;
        }

        // A chunk that points at itself would otherwise spin here until the process is
        // killed — and a chunk carrying no rows would not even grow toward MaxResultRows.
        // Same failure the agent-listing loop guards against with its repeated page token.
        if (!seenLinks.Add(resolved))
        {
            refusal = ChunkRefusal.Repeated;
            return false;
        }

        chunkUri = resolved;
        return true;
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
        var workspace = http.BaseAddress ?? Options.Host!;

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

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Warning,
        Message = "A re-executed query did not produce a result within {Seconds}s.")]
    private static partial void LogReExecuteTimedOut(ILogger logger, double seconds);
}
