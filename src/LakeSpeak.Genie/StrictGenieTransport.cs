using System.Text.Json;
using LakeSpeak.Genie.Wire;
using Microsoft.Extensions.Logging;

namespace LakeSpeak.Genie;

internal sealed class StrictGenieTransport(
    HttpClient http, GenieClientOptions options, TimeProvider time, ILogger logger, GenieResultLimits limits)
    : GenieTransport(http, options, time, logger)
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

    protected override HttpCompletionOption CompletionOption => HttpCompletionOption.ResponseHeadersRead;

    internal override void ValidateAgent(SpaceWire wire, string agentId) => ValidateIdentity(wire.SpaceId, agentId);

    internal override void ValidateStartConversation(
        StartConversationWire wire, string agentId, string conversationId, string messageId)
    {
        ValidateIdentity(wire.SpaceId?.GetString(), agentId);
        ValidateIdentity(wire.Conversation?.SpaceId, agentId);
        ValidateIdentity(wire.Conversation?.ConversationId, conversationId);
        if (wire.Message is { } message)
        {
            ValidateMessage(message, agentId, conversationId, messageId);
        }
    }

    internal override void ValidateMessage(MessageWire wire, string agentId, string conversationId, string messageId)
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

    internal override MessageErrorWire? ReportedError(MessageWire wire) => null;

    internal override async Task<GenieResponse> AttachQueryResultsAsync(
        GenieResponse response, CancellationToken cancellationToken)
    {
        if (response.QueryAttachments.Count == 0)
        {
            return response;
        }

        if (response.State != GenieMessageState.Completed)
        {
            throw StrictIncomplete();
        }

        var budget = new ResultBudget(limits);
        var queries = new List<GenieQueryAttachment>();
        foreach (var attachment in response.QueryAttachments)
        {
            // An omitted attachment id is valid message metadata, but leaves no way to fetch the table.
            if (attachment.IsTruncated || string.IsNullOrEmpty(attachment.AttachmentId))
            {
                throw StrictIncomplete();
            }

            var table = await FetchQueryResultAsync(
                ResultUrl(response, attachment.AttachmentId),
                HttpMethod.Get, budget, cancellationToken, attachment.Query.StatementId).ConfigureAwait(false);
            queries.Add(attachment with { Result = table ?? throw StrictIncomplete() });
        }

        return response.WithQueryAttachments(queries);
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

    protected override async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        Parse<T>(await ReadBoundedBodyAsync(response, limits.MaxBytes, cancellationToken).ConfigureAwait(false));

    // Strict mode reports no server error text, so it never reads the body: an oversized or
    // failing body cannot then displace the status classification.
    protected override Task<ApiErrorWire?> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken) => Task.FromResult<ApiErrorWire?>(null);

    // Strict errors carry no transport exception details.
    protected override GenieException NetworkFailure(string message, Exception cause) =>
        new(GenieFailureKind.Network, message);

    protected override GenieException Unreachable(HttpRequestException cause) =>
        NetworkFailure("Could not reach the Databricks workspace.", cause);

    protected override ResultFetch BeginResultFetch()
    {
        var budget = new ResultBudget(limits);
        return (url, method, cancellationToken) => FetchQueryResultAsync(url, method, budget, cancellationToken);
    }

    protected override GenieQueryResult? ReExecuteTimedOut() => throw StrictIncomplete();

    private static GenieException StrictLimit() => new(
        GenieFailureKind.UnsupportedResult, "The strict result budget was exceeded.",
        errorCode: GenieErrorCodes.StrictResultLimit);

    private static GenieException StrictIncomplete() => new(
        GenieFailureKind.UnsupportedResult, "A complete inline query result was not available.",
        errorCode: GenieErrorCodes.StrictResultIncomplete);

    private static GenieException StrictMalformed() => new(
        GenieFailureKind.MalformedResponse, "The response contained invalid or conflicting identity or result metadata.");

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

    private async Task<byte[]> ReadBoundedBodyAsync(
        HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(Options.RequestTimeout, Time);
        using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ReadBoundedBodyCoreAsync(response, maxBytes, bodyCancellation.Token).ConfigureAwait(false);
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
        HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw StrictLimit();
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        var remaining = maxBytes;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // One byte past the remaining budget is enough to detect overflow without buffering more.
            var readSize = remaining < buffer.Length ? remaining + 1 : buffer.Length;
            var count = await stream.ReadAsync(buffer.AsMemory(0, readSize), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0)
            {
                return body.ToArray();
            }

            if (count > remaining)
            {
                throw StrictLimit();
            }

            remaining -= count;
            body.Write(buffer, 0, count);
        }
    }

    private static T Parse<T>(byte[] body)
    {
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

    private async Task<T> ReadResultAsync<T>(
        HttpResponseMessage response, ResultBudget budget, CancellationToken cancellationToken)
    {
        var body = await ReadBoundedBodyAsync(response, budget.BytesRemaining, cancellationToken).ConfigureAwait(false);
        budget.BytesRemaining -= body.Length;
        return Parse<T>(body);
    }

    private async Task<GenieQueryResult?> FetchQueryResultAsync(
        string url, HttpMethod method, ResultBudget budget, CancellationToken cancellationToken,
        string? expectedStatementId = null)
    {
        using var response = await SendAsync(method, url, null, cancellationToken).ConfigureAwait(false);
        var wire = await ReadResultAsync<QueryResultWire>(response, budget, cancellationToken).ConfigureAwait(false);
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
            chunk = await ReadResultAsync<ResultDataWire>(nextResponse, budget, cancellationToken).ConfigureAwait(false);
        }

        if (statement.Manifest.TotalRowCount is { } total && total != rows.Count)
        {
            throw StrictIncomplete();
        }

        return new GenieQueryResult(columns.Select(NormalizeColumn).ToList(), rows, false, statement.Manifest.TotalRowCount);
    }
}
