using System.Net.Http.Json;
using System.Text.Json;
using LakeSpeak.Genie.Wire;
using Microsoft.Extensions.Logging;

namespace LakeSpeak.Genie;

internal sealed partial class LegacyGenieTransport(
    HttpClient http, GenieClientOptions options, TimeProvider time, ILogger logger)
    : GenieTransport(http, options, time, logger)
{
    protected override HttpCompletionOption CompletionOption => HttpCompletionOption.ResponseContentRead;

    internal override MessageErrorWire? ReportedError(MessageWire wire) => wire.Error;

    internal override async Task<GenieResponse> AttachQueryResultsAsync(
        GenieResponse response, CancellationToken cancellationToken)
    {
        if (response.Query is null || response.Metadata.AttachmentId is null)
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

        var result = await GetQueryResultAsync(ResultUrl(response, response.Metadata.AttachmentId), cancellationToken)
            .ConfigureAwait(false);

        return response.WithQueryAttachments(
            [response.QueryAttachments[0] with { Result = result }, .. response.QueryAttachments.Skip(1)]);
    }

    protected override async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
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

    protected override async Task<ApiErrorWire?> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // The Genie error body shape is not documented, so parsing is best-effort and the
        // status code remains the source of truth for how to classify the failure.
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ApiErrorWire>(body, Json);
        }
        catch (JsonException)
        {
            // Not JSON. The status code still classifies it.
            return null;
        }
    }

    protected override GenieException NetworkFailure(string message, Exception cause) =>
        new(GenieFailureKind.Network, message, innerException: cause);

    protected override GenieException Unreachable(HttpRequestException cause) =>
        NetworkFailure($"Could not reach the Databricks workspace: {cause.Message}", cause);

    protected override ResultFetch BeginResultFetch() => FetchQueryResultAsync;

    protected override GenieQueryResult? ReExecuteTimedOut() => null;

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

        var cols = columns.Select(NormalizeColumn).ToList();

        RejectExternalLinks(statement.Result);

        var rows = new List<IReadOnlyList<string?>>(statement.Result?.DataArray ?? []);
        var incomplete = await AppendRemainingChunksAsync(
                rows, statement.Result, statement.StatementId, cancellationToken)
            .ConfigureAwait(false);

        // `manifest.truncated` alone does NOT mean the caller has every row: it reports
        // statement-level truncation by Databricks, and is false for a result that was merely
        // split into chunks.
        //
        // shortOfTotal is not a second guard on the loop stopping early — every such exit already
        // returns true above. It catches the case the loop cannot see: a walk that ran to a clean
        // finish, with no further chunk advertised, yet still ended up with fewer rows than the
        // manifest promised. That is Databricks disagreeing with itself, and the caller should
        // hear about it rather than receive a short result labelled whole.
        var shortOfTotal = statement.Manifest.TotalRowCount is { } total && rows.Count < total;

        return new GenieQueryResult(
            cols,
            rows,
            (statement.Manifest.Truncated ?? false) || incomplete || shortOfTotal,
            statement.Manifest.TotalRowCount);
    }

    /// <summary>
    /// Follows <c>next_chunk_internal_link</c> until the result is complete, appending each
    /// chunk's rows. Returns <see langword="true"/> if rows are known to be missing.
    /// </summary>
    /// <remarks>
    /// Stopping early is never silent: every exit that leaves rows behind returns
    /// <see langword="true"/>, which the caller turns into <c>IsTruncated</c>. A chunk that
    /// cannot be fetched degrades to the rows already in hand rather than discarding a good
    /// partial answer, because the text of the answer is the primary payload.
    /// </remarks>
    private async Task<bool> AppendRemainingChunksAsync(
        List<IReadOnlyList<string?>> rows,
        ResultDataWire? firstChunk,
        string? statementId,
        CancellationToken cancellationToken)
    {
        var chunk = firstChunk;
        var seenLinks = new HashSet<Uri>();
        var fetches = 0;

        // Driven by next_chunk_index, not by the link: the index is what says rows are missing.
        // Every way of failing to follow it below still returns true, so a result is never
        // reported complete because the means of completing it was unavailable.
        while (chunk?.NextChunkIndex is { } nextChunkIndex)
        {
            if (rows.Count >= Options.MaxResultRows)
            {
                LogChunkCapReached(Logger, rows.Count, Options.MaxResultRows);
                return true;
            }

            // Neither the row cap nor the repeated-link guard stops a response that hands back a
            // fresh link every time while carrying no rows: nothing grows toward MaxResultRows,
            // and no link repeats. That is an unbounded request flood at a real workspace, so the
            // number of fetches is bounded outright.
            if (++fetches > MaxChunkFetches)
            {
                LogChunkFetchLimitReached(Logger, MaxChunkFetches);
                return true;
            }

            if (!TryResolveNextChunk(
                    chunk.NextChunkInternalLink, nextChunkIndex, statementId, seenLinks, out var chunkUri, out var refusal))
            {
                LogRefusal(refusal);
                return true;
            }

            try
            {
                chunk = await GetAsync<ResultDataWire>(chunkUri.AbsoluteUri, cancellationToken)
                    .ConfigureAwait(false);

                // Inside the try on purpose. Refusing external links is right for the *first*
                // chunk, where there is nothing to hand back — but here rows and an answer are
                // already in hand, and throwing would discard both. Short and flagged beats
                // losing the answer text, which is the primary payload.
                RejectExternalLinks(chunk);
            }
            catch (GenieException ex)
            {
                // Genie executes the statement; whether the caller may read its remaining chunks
                // is a workspace permission question this client cannot settle in advance.
                LogChunkFetchFailed(Logger, ex.Kind);
                return true;
            }

            rows.AddRange(chunk.DataArray ?? []);
        }

        return false;
    }

    private void LogRefusal(ChunkRefusal refusal)
    {
        switch (refusal)
        {
            case ChunkRefusal.Missing:
                LogChunkLinkMissing(Logger);
                break;
            case ChunkRefusal.Rejected:
                LogChunkLinkRejected(Logger);
                break;
            case ChunkRefusal.Repeated:
                LogChunkLinkRepeated(Logger);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Unmapped chunk refusal.");
        }
    }

    /// <summary>
    /// EXTERNAL_LINKS disposition puts rows behind presigned URLs and omits <c>data_array</c>.
    /// Returning an empty row set as a successful complete result would be the worst outcome
    /// available — a silently empty export that reads as a real answer.
    /// </summary>
    private static void RejectExternalLinks(ResultDataWire? result)
    {
        if (result?.ExternalLinks is not { Count: > 0 } links || result.DataArray is not null)
        {
            return;
        }

        var rowsBehindLinks = links.Sum(l => l.RowCount ?? 0);
        throw new GenieException(
            GenieFailureKind.UnsupportedResult,
            $"Databricks returned this result as {links.Count} external link(s) covering " +
            $"{rowsBehindLinks} row(s) rather than inline rows. This version cannot read that " +
            "form, and will not report an empty result as a complete one. Narrow the question " +
            "so the result comes back inline.");
    }

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
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Could not fetch a further result chunk ({Kind}). " +
                  "The rows already retrieved are returned and reported as truncated.")]
    private static partial void LogChunkFetchFailed(ILogger logger, GenieFailureKind kind);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "Stopped after {Limit} chunk requests for a single result. " +
                  "The result is reported as truncated.")]
    private static partial void LogChunkFetchLimitReached(ILogger logger, int limit);
}
