using System.Globalization;
using LakeSpeak.Genie.Wire;

namespace LakeSpeak.Genie;

public sealed partial class GenieClient
{
    private async Task<GenieQueryResult?> FetchQueryResultAsync(
        string url, HttpMethod method, CancellationToken cancellationToken, ResultBudget? budget = null)
    {
        if (_strictLimits is not null)
        {
            return await FetchStrictQueryResultAsync(url, method, budget ?? new ResultBudget(_strictLimits), cancellationToken).ConfigureAwait(false);
        }
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
        var seenLinks = new HashSet<string>(StringComparer.Ordinal);
        var fetches = 0;

        // Driven by next_chunk_index, not by the link: the index is what says rows are missing.
        // Every way of failing to follow it below still returns true, so a result is never
        // reported complete because the means of completing it was unavailable.
        while (chunk?.NextChunkIndex is not null)
        {
            if (rows.Count >= _options.MaxResultRows)
            {
                LogChunkCapReached(_logger, rows.Count, _options.MaxResultRows);
                return true;
            }

            // Neither the row cap nor the repeated-link guard stops a response that hands back a
            // fresh link every time while carrying no rows: nothing grows toward MaxResultRows,
            // and no link repeats. That is an unbounded request flood at a real workspace, so the
            // number of fetches is bounded outright.
            if (++fetches > MaxChunkFetches)
            {
                LogChunkFetchLimitReached(_logger, MaxChunkFetches);
                return true;
            }

            var link = chunk.NextChunkInternalLink;
            if (link is not { Length: > 0 })
            {
                // Observed on the Genie query-result endpoint: it retains the statement id and
                // next index but omits the link that the SQL Statement endpoint returns. That
                // endpoint's path is documented. Uri normalizes dot-only segments even after
                // escaping, so those malformed ids cannot safely address a statement endpoint.
                if (statementId is not { Length: > 0 } || statementId is "." or "..")
                {
                    LogChunkLinkMissing(_logger);
                    return true;
                }

                link = $"/api/2.0/sql/statements/{Uri.EscapeDataString(statementId)}/result/chunks/" +
                    chunk.NextChunkIndex.Value.ToString(CultureInfo.InvariantCulture);
            }

            // A chunk that points at itself would otherwise spin here until the process is
            // killed — and a chunk carrying no rows would not even grow toward MaxResultRows.
            // Same failure the agent-listing loop guards against with its repeated page token.
            if (!seenLinks.Add(link))
            {
                LogChunkLinkRepeated(_logger);
                return true;
            }

            if (!ResolvesToWorkspace(link, out var chunkUri))
            {
                // The link is server-supplied and the bearer token rides on every workspace
                // request, so a link naming another host would disclose the token to it. The
                // link is not logged: its contents are attacker-chosen in exactly this case.
                LogChunkLinkRejected(_logger);
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
                LogChunkFetchFailed(_logger, ex.Kind);
                return true;
            }

            rows.AddRange(chunk.DataArray ?? []);
        }

        return false;
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

}
