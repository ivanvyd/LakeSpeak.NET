using System.Globalization;
using System.Text.Json;
using LakeSpeak.Genie.Wire;

namespace LakeSpeak.Genie;

public sealed partial class GenieClient
{
    private sealed class ResultBudget(GenieResultLimits limits)
    {
        internal int BytesRemaining { get; set; } = limits.MaxBytes;
        internal int RowsRemaining { get; set; } = limits.MaxRows;
        internal int ChunkFetches { get; set; }
    }

    private static GenieException StrictLimit() => new(
        GenieFailureKind.UnsupportedResult, "The strict result budget was exceeded.", errorCode: "STRICT_RESULT_LIMIT");

    private static GenieException StrictIncomplete() => new(
        GenieFailureKind.UnsupportedResult, "A complete inline query result was not available.", errorCode: "STRICT_RESULT_INCOMPLETE");

    private static GenieException StrictMalformed() => new(
        GenieFailureKind.MalformedResponse, "The response contained invalid or conflicting identity or result metadata.");

    private static GenieQuery NormalizeQuery(QueryAttachmentWire q) => new(
        q.Query, q.Title, q.Description, q.StatementId,
        q.Parameters?.Select(p => new GenieQueryParameter(p.Keyword, p.SqlType, p.Value)).ToList());

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
                if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase)
                    || property.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(property.Value.GetString()))
                    {
                        throw StrictMalformed();
                    }
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
        catch (HttpRequestException)
        {
            throw new GenieException(GenieFailureKind.Network, "Could not read the Databricks response body.");
        }
        catch (IOException)
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
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, budget.BytesRemaining == int.MaxValue
                ? buffer.Length : budget.BytesRemaining + 1)), cancellationToken).ConfigureAwait(false);
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

        var rows = new List<IReadOnlyList<string?>>();
        var chunk = statement.Result;
        var seenLinks = new HashSet<Uri>();
        var expectedChunkIndex = 0;
        while (true)
        {
            if (chunk is null || chunk.ExternalLinks is { Count: > 0 } || chunk.DataArray is null)
            {
                throw StrictIncomplete();
            }
            if (chunk.RowCount is { } rowCount && rowCount != chunk.DataArray.Count)
            {
                throw StrictIncomplete();
            }
            if ((chunk.ChunkIndex is { } chunkIndex && chunkIndex != expectedChunkIndex)
                || chunk.DataArray.Any(row => row is null || row.Count != columns.Count))
            {
                throw StrictMalformed();
            }
            if (chunk.DataArray.Count > budget.RowsRemaining)
            {
                throw StrictLimit();
            }
            budget.RowsRemaining -= chunk.DataArray.Count;
            rows.AddRange(chunk.DataArray);
            if (chunk.NextChunkIndex is null)
            {
                if (chunk.NextChunkInternalLink is not null)
                {
                    throw StrictIncomplete();
                }
                break;
            }
            if (budget.RowsRemaining == 0 || ++budget.ChunkFetches > MaxChunkFetches)
            {
                throw StrictLimit();
            }
            if (chunk.NextChunkIndex <= expectedChunkIndex)
            {
                throw StrictMalformed();
            }
            expectedChunkIndex = chunk.NextChunkIndex.Value;
            var link = chunk.NextChunkInternalLink;
            if (string.IsNullOrEmpty(link))
            {
                if (statement.StatementId is not { Length: > 0 } id || id is "." or "..")
                {
                    throw StrictIncomplete();
                }
                link = $"/api/2.0/sql/statements/{Esc(id)}/result/chunks/" + chunk.NextChunkIndex.Value.ToString(CultureInfo.InvariantCulture);
            }
            if (!ResolvesToWorkspace(link, out var uri) || uri.UserInfo.Length != 0 || !seenLinks.Add(uri))
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
        return new GenieQueryResult(columns.Select(c => new GenieColumn(c.Name ?? string.Empty, c.TypeText ?? c.TypeName, c.TypeName)).ToList(),
            rows, false, statement.Manifest.TotalRowCount);
    }
}
