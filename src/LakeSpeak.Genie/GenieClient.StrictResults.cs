using System.Text.Json;
using LakeSpeak.Genie.Wire;

namespace LakeSpeak.Genie;

public sealed partial class GenieClient
{
    // Only the identities strict mode compares. Other *_id fields are not identities this client
    // checks, and some are not strings: Genie messages carry a numeric user_id.
    private static readonly HashSet<string> IdentityProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "space_id", "conversation_id", "message_id", "id", "attachment_id", "statement_id",
    };

    private sealed class ResultBudget(GenieResultLimits limits)
    {
        internal int BytesRemaining { get; set; } = limits.MaxBytes;
        internal int RowsRemaining { get; set; } = limits.MaxRows;
        internal int ChunkFetches { get; set; }
    }

    private static GenieException StrictLimit() => new(
        GenieFailureKind.UnsupportedResult, "The strict result budget was exceeded.",
        errorCode: GenieErrorCodes.StrictResultLimit);

    private static GenieException StrictIncomplete() => new(
        GenieFailureKind.UnsupportedResult, "A complete inline query result was not available.",
        errorCode: GenieErrorCodes.StrictResultIncomplete);

    private static GenieException StrictMalformed() => new(
        GenieFailureKind.MalformedResponse, "The response contained invalid or conflicting identity or result metadata.");

    private static GenieQuery NormalizeQuery(QueryAttachmentWire q) => new(
        q.Query, q.Title, q.Description, q.StatementId,
        q.Parameters?.Select(p => new GenieQueryParameter(p.Keyword, p.SqlType, p.Value)).ToList());

    private static GenieColumn NormalizeColumn(ColumnWire c) => new(c.Name ?? string.Empty, c.TypeText ?? c.TypeName, c.TypeName);

    private static string? StatementState(JsonElement? status)
    {
        if (status is not { } value)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StrictMalformed();
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Equals("state", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : throw StrictMalformed();
            }
        }

        return null;
    }

    private static void ValidateIdentity(string? actual, string expected)
    {
        if (actual is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw StrictMalformed();
        }
    }

    private static void ValidateMessageIdentity(MessageWire wire, string agentId, string conversationId, string messageId)
    {
        ValidateIdentity(wire.SpaceId, agentId);
        ValidateIdentity(wire.ConversationId, conversationId);
        ValidateIdentity(wire.MessageId, messageId);
        ValidateIdentity(wire.Id, messageId);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attachment in wire.Attachments ?? [])
        {
            if (attachment is null || attachment.Query?.Parameters?.Any(p => p is null) == true)
            {
                throw StrictMalformed();
            }

            if (attachment.AttachmentId is { } id && !ids.Add(id))
            {
                throw StrictMalformed();
            }
        }
    }

    internal static void ValidateJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                // The serializer accepts duplicates and takes the last value. Reject them before
                // deserialization so one identity (or its containing object) cannot hide another.
                if (!names.Add(property.Name))
                {
                    throw StrictMalformed();
                }

                if (IdentityProperties.Contains(property.Name)
                    && (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(property.Value.GetString())))
                {
                    throw StrictMalformed();
                }

                ValidateJsonProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateJsonProperties(item);
            }
        }
    }

    private async Task<byte[]> ReadBoundedBodyAsync(
        HttpResponseMessage response, ResultBudget budget, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_options.RequestTimeout, _time);
        using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ReadBoundedBodyCoreAsync(response, budget, bodyCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new GenieException(GenieFailureKind.Network, "Reading the Databricks response body timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new GenieException(GenieFailureKind.Network, "Could not read the Databricks response body.");
        }
    }

    private static async Task<byte[]> ReadBoundedBodyCoreAsync(
        HttpResponseMessage response, ResultBudget budget, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > budget.BytesRemaining)
        {
            throw StrictLimit();
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // One byte past the remaining budget is enough to detect overflow without buffering more.
            var readSize = budget.BytesRemaining < buffer.Length ? budget.BytesRemaining + 1 : buffer.Length;
            var count = await stream.ReadAsync(buffer.AsMemory(0, readSize), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0)
            {
                return body.ToArray();
            }

            if (count > budget.BytesRemaining)
            {
                throw StrictLimit();
            }

            budget.BytesRemaining -= count;
            body.Write(buffer, 0, count);
        }
    }

    private async Task<T> ReadStrictAsync<T>(
        HttpResponseMessage response, ResultBudget budget, CancellationToken cancellationToken)
    {
        var body = await ReadBoundedBodyAsync(response, budget, cancellationToken).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(body);
            ValidateJsonProperties(json.RootElement);
            return json.RootElement.Deserialize<T>(Json) ?? throw StrictMalformed();
        }
        catch (JsonException)
        {
            // JsonException paths can include server-controlled property names. Keep the cause
            // out of public diagnostics as well as the body and continuation URLs.
            throw StrictMalformed();
        }
    }

    private async Task<GenieQueryResult?> FetchStrictQueryResultAsync(
        string url, HttpMethod method, ResultBudget budget, CancellationToken cancellationToken,
        string? expectedStatementId = null)
    {
        using var response = await SendAsync(method, url, null, cancellationToken).ConfigureAwait(false);
        var wire = await ReadStrictAsync<QueryResultWire>(response, budget, cancellationToken).ConfigureAwait(false);
        var statement = wire.StatementResponse;
        if (expectedStatementId is not null)
        {
            ValidateIdentity(statement?.StatementId, expectedStatementId);
        }

        if (StatementState(statement?.Status) is { } state && state != "SUCCEEDED")
        {
            if (state is "PENDING" or "RUNNING" && statement?.Manifest is null)
            {
                return null;
            }

            throw StrictIncomplete();
        }

        if (statement?.Manifest?.Schema?.Columns is not { } columns)
        {
            return null;
        }

        if (statement.Manifest.Truncated == true || statement.Manifest.TotalRowCount < 0)
        {
            throw StrictIncomplete();
        }

        if (columns.Any(column => column is null))
        {
            throw StrictMalformed();
        }

        // A zero-row statement may omit its result; only an explicit zero total makes that complete.
        var chunk = statement.Result
            ?? (statement.Manifest.TotalRowCount == 0 ? new ResultDataWire { DataArray = [] } : throw StrictIncomplete());
        var rows = new List<IReadOnlyList<string?>>();
        var seenLinks = new HashSet<Uri>();
        var expectedChunkIndex = 0;
        while (true)
        {
            if (chunk.ExternalLinks is { Count: > 0 })
            {
                throw StrictIncomplete();
            }

            IReadOnlyList<IReadOnlyList<string?>> data = chunk.DataArray
                ?? (chunk.RowCount == 0 ? [] : throw StrictIncomplete());
            if (chunk.RowCount is { } rowCount && rowCount != data.Count)
            {
                throw StrictIncomplete();
            }

            if ((chunk.ChunkIndex is { } chunkIndex && chunkIndex != expectedChunkIndex)
                || data.Any(row => row is null || row.Count != columns.Count))
            {
                throw StrictMalformed();
            }

            if (data.Count > budget.RowsRemaining)
            {
                throw StrictLimit();
            }

            budget.RowsRemaining -= data.Count;
            rows.AddRange(data);
            if (chunk.NextChunkIndex is not { } nextChunkIndex)
            {
                if (chunk.NextChunkInternalLink is not null)
                {
                    throw StrictIncomplete();
                }

                break;
            }

            if (++budget.ChunkFetches > MaxChunkFetches)
            {
                throw StrictLimit();
            }

            if (nextChunkIndex != expectedChunkIndex + 1)
            {
                throw StrictMalformed();
            }

            expectedChunkIndex = nextChunkIndex;
            if (!TryResolveNextChunk(chunk.NextChunkInternalLink, nextChunkIndex, statement.StatementId, seenLinks, out var uri, out _))
            {
                throw StrictIncomplete();
            }

            using var nextResponse = await SendAsync(HttpMethod.Get, uri.AbsoluteUri, null, cancellationToken).ConfigureAwait(false);
            chunk = await ReadStrictAsync<ResultDataWire>(nextResponse, budget, cancellationToken).ConfigureAwait(false);
        }

        if (statement.Manifest.TotalRowCount is { } total && total != rows.Count)
        {
            throw StrictIncomplete();
        }

        return new GenieQueryResult(columns.Select(NormalizeColumn).ToList(), rows, false, statement.Manifest.TotalRowCount);
    }
}
