using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LakeSpeak.Genie;

namespace LakeSpeak.Rendering;

/// <summary>
/// Serializes a Genie response for another program.
/// </summary>
/// <remarks>
/// The shape is versioned by <see cref="SchemaVersion"/> and is part of the tool's contract:
/// scripts and coding agents parse it. Adding a field is a minor change; renaming or removing
/// one is breaking and needs the version bumped.
/// </remarks>
public static class MachineOutput
{
    public const string SchemaVersion = "1";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactOptions = new(Options)
    {
        WriteIndented = false,
    };

    public static string ToJson(GenieResponse response, string? agentTitle = null, bool indented = true) =>
        JsonSerializer.Serialize(
            Envelope.From(response, agentTitle),
            indented ? Options : CompactOptions);

    /// <summary>
    /// One JSON object per result row, for streaming into another tool.
    /// </summary>
    /// <remarks>
    /// The answer is not repeated on every row. When there is no query result there is nothing
    /// row-shaped to emit, so a single metadata object is written instead of nothing at all —
    /// a consumer reading zero lines cannot tell success from failure.
    /// </remarks>
    public static string ToJsonLines(GenieResponse response, string? agentTitle = null)
    {
        var builder = new StringBuilder();

        if (response.Result is null)
        {
            builder.AppendLine(JsonSerializer.Serialize(
                Envelope.From(response, agentTitle), CompactOptions));
            return builder.ToString();
        }

        var columns = response.Result.Columns;
        foreach (var row in response.Result.Rows)
        {
            var obj = new Dictionary<string, string?>(columns.Count, StringComparer.Ordinal);
            for (var i = 0; i < columns.Count && i < row.Count; i++)
            {
                obj[columns[i].Name] = row[i];
            }

            builder.AppendLine(JsonSerializer.Serialize(obj, CompactOptions));
        }

        return builder.ToString();
    }

    private sealed record Envelope
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; init; } = MachineOutput.SchemaVersion;

        [JsonPropertyName("agent")]
        public required AgentRef Agent { get; init; }

        [JsonPropertyName("conversationId")]
        public required string ConversationId { get; init; }

        [JsonPropertyName("messageId")]
        public required string MessageId { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("answer")]
        public string? Answer { get; init; }

        [JsonPropertyName("query")]
        public QueryRef? Query { get; init; }

        [JsonPropertyName("result")]
        public ResultRef? Result { get; init; }

        [JsonPropertyName("suggestedQuestions")]
        public IReadOnlyList<string>? SuggestedQuestions { get; init; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; init; }

        internal static Envelope From(GenieResponse r, string? agentTitle) => new()
        {
            Agent = new AgentRef { Id = r.AgentId, Title = agentTitle },
            ConversationId = r.ConversationId,
            MessageId = r.MessageId,
            // Lowercase for a stable, script-friendly vocabulary that does not change if the
            // display name of a state changes.
            Status = r.State.ToString().ToLowerInvariant(),
            Answer = r.Text,
            Query = r.Query is null ? null : new QueryRef { Sql = r.Query.Sql, Title = r.Query.Title },
            Result = r.Result is null ? null : ResultRef.From(r.Result),
            SuggestedQuestions = r.SuggestedQuestions.Count == 0 ? null : r.SuggestedQuestions,
            DurationMs = (long)r.Metadata.Duration.TotalMilliseconds,
        };
    }

    private sealed record AgentRef
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }
    }

    private sealed record QueryRef
    {
        [JsonPropertyName("sql")]
        public string? Sql { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }
    }

    private sealed record ResultRef
    {
        [JsonPropertyName("columns")]
        public required IReadOnlyList<ColumnRef> Columns { get; init; }

        [JsonPropertyName("rows")]
        public required IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; }

        [JsonPropertyName("truncated")]
        public required bool Truncated { get; init; }

        [JsonPropertyName("totalRowCount")]
        public long? TotalRowCount { get; init; }

        internal static ResultRef From(GenieQueryResult result) => new()
        {
            Columns = result.Columns
                .Select(c => new ColumnRef { Name = c.Name, Type = c.DataType })
                .ToList(),
            Rows = result.Rows,
            Truncated = result.IsTruncated,
            TotalRowCount = result.TotalRowCount,
        };
    }

    private sealed record ColumnRef
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }
    }
}

/// <summary>Writes a query result as RFC 4180 CSV.</summary>
public static class CsvWriter
{
    /// <summary>
    /// Formats the query result. Values are written exactly as Databricks returned them.
    /// </summary>
    /// <remarks>
    /// No locale formatting and no numeric parsing. A DECIMAL rendered through a double, or a
    /// thousands separator inserted for readability, changes the number a downstream tool
    /// reads. Only the CSV quoting rules are applied.
    /// </remarks>
    public static string Write(GenieQueryResult result)
    {
        var builder = new StringBuilder();

        builder.AppendLine(string.Join(',', result.Columns.Select(c => Quote(c.Name))));

        foreach (var row in result.Rows)
        {
            builder.AppendLine(string.Join(',', row.Select(Quote)));
        }

        return builder.ToString();
    }

    private static string Quote(string? value)
    {
        // A SQL NULL becomes an empty unquoted field, which is how every CSV reader
        // distinguishes it from the literal empty string written as "".
        if (value is null)
        {
            return string.Empty;
        }

        // A leading =, +, - or @ makes a spreadsheet treat the cell as a formula. Prefixing a
        // single quote is the conventional defence and is visible rather than silent.
        var needsFormulaGuard = value.Length > 0 && value[0] is '=' or '+' or '-' or '@';
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);

        if (needsFormulaGuard)
        {
            return $"\"'{escaped}\"";
        }

        return value.AsSpan().ContainsAny(",\"\r\n") ? $"\"{escaped}\"" : value;
    }
}

/// <summary>Writes a response as Markdown, for reports and pull-request comments.</summary>
public static class MarkdownWriter
{
    public static string Write(GenieResponse response, string? agentTitle = null)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            builder.AppendLine(TerminalSafety.Sanitize(response.Text)).AppendLine();
        }

        if (response.Result is { Columns.Count: > 0 } result)
        {
            WriteTable(builder, result);
        }

        if (response.Query?.Sql is { Length: > 0 } sql)
        {
            builder.AppendLine("<details><summary>Generated SQL</summary>")
                .AppendLine()
                .AppendLine("```sql")
                .AppendLine(TerminalSafety.Sanitize(sql))
                .AppendLine("```")
                .AppendLine()
                .AppendLine("</details>")
                .AppendLine();
        }

        if (agentTitle is { Length: > 0 })
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"_Agent: {agentTitle}_");
        }

        return builder.ToString();
    }

    private static void WriteTable(StringBuilder builder, GenieQueryResult result)
    {
        builder.Append("| ")
            .Append(string.Join(" | ", result.Columns.Select(c => Escape(c.Name))))
            .AppendLine(" |");

        builder.Append('|')
            .Append(string.Concat(Enumerable.Repeat("---|", result.Columns.Count)))
            .AppendLine();

        foreach (var row in result.Rows)
        {
            builder.Append("| ")
                .Append(string.Join(" | ", row.Select(Escape)))
                .AppendLine(" |");
        }

        builder.AppendLine();

        if (result.IsTruncated)
        {
            var total = result.TotalRowCount is { } n
                ? n.ToString(CultureInfo.InvariantCulture)
                : "an unknown number of";
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"_Showing {result.RowCount} of {total} rows; the result was truncated by Databricks._")
                .AppendLine();
        }
    }

    // A null renders as an empty cell rather than the four letters "null", which would be
    // indistinguishable from a string containing them.
    private static string Escape(string? value) =>
        value is null
            ? string.Empty
            : TerminalSafety.SanitizeCell(value).Replace("|", "\\|", StringComparison.Ordinal);
}
