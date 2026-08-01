using System.Text.Json.Serialization;

namespace LakeSpeak.Genie.Wire;

// Wire contracts for /api/2.0/genie. Field names mirror the API exactly, including
// `space_id`, which the public model renames to AgentId. Keeping the rename at this
// boundary is what stops "space" leaking into the developer-facing surface, and what
// stops a future Databricks rename from rippling through the whole codebase.
//
// Every property is nullable. The API marks several fields required, but a client that
// throws on a missing "required" field turns a partial response into an outage; the
// normalization layer decides what is genuinely mandatory.

internal sealed record SpaceListWire
{
    [JsonPropertyName("spaces")]
    public IReadOnlyList<SpaceWire>? Spaces { get; init; }

    [JsonPropertyName("next_page_token")]
    public string? NextPageToken { get; init; }
}

internal sealed record SpaceWire
{
    [JsonPropertyName("space_id")]
    public string? SpaceId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("warehouse_id")]
    public string? WarehouseId { get; init; }
}

internal sealed record StartConversationWire
{
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }

    [JsonPropertyName("conversation")]
    public ConversationWire? Conversation { get; init; }

    [JsonPropertyName("message")]
    public MessageWire? Message { get; init; }
}

internal sealed record ConversationWire
{
    [JsonPropertyName("space_id")]
    public string? SpaceId { get; init; }

    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("created_timestamp")]
    public long? CreatedTimestamp { get; init; }

    [JsonPropertyName("last_updated_timestamp")]
    public long? LastUpdatedTimestamp { get; init; }
}

internal sealed record MessageWire
{
    [JsonPropertyName("space_id")]
    public string? SpaceId { get; init; }

    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }

    // A duplicate of message_id. Published examples have omitted message_id even though the
    // SDK marks it required, so it is read as a fallback rather than trusted to be absent.
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    // The question that was asked, not the answer. The answer is in
    // attachments[].text.content. Mislabelling this is the easiest mistake to make
    // against this API, so the name says what it is.
    [JsonPropertyName("content")]
    public string? Question { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("attachments")]
    public IReadOnlyList<AttachmentWire>? Attachments { get; init; }

    [JsonPropertyName("error")]
    public MessageErrorWire? Error { get; init; }

    [JsonPropertyName("created_timestamp")]
    public long? CreatedTimestamp { get; init; }

    [JsonPropertyName("last_updated_timestamp")]
    public long? LastUpdatedTimestamp { get; init; }
}

internal sealed record MessageErrorWire
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

// Exactly one variant is populated per attachment. `suggested_questions` is a real
// fourth variant that most summaries of this API omit.
internal sealed record AttachmentWire
{
    [JsonPropertyName("attachment_id")]
    public string? AttachmentId { get; init; }

    [JsonPropertyName("text")]
    public TextAttachmentWire? Text { get; init; }

    [JsonPropertyName("query")]
    public QueryAttachmentWire? Query { get; init; }

    [JsonPropertyName("suggested_questions")]
    public SuggestedQuestionsAttachmentWire? SuggestedQuestions { get; init; }

    [JsonPropertyName("viz")]
    public VizAttachmentWire? Viz { get; init; }
}

internal sealed record TextAttachmentWire
{
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("purpose")]
    public string? Purpose { get; init; }

    [JsonPropertyName("phase")]
    public string? Phase { get; init; }
}

internal sealed record QueryAttachmentWire
{
    // The generated SQL. The field is `query`; there is no `sql` field.
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("statement_id")]
    public string? StatementId { get; init; }

    [JsonPropertyName("parameters")]
    public IReadOnlyList<QueryParameterWire>? Parameters { get; init; }

    [JsonPropertyName("query_result_metadata")]
    public ResultMetadataWire? ResultMetadata { get; init; }
}

internal sealed record QueryParameterWire
{
    [JsonPropertyName("keyword")]
    public string? Keyword { get; init; }

    [JsonPropertyName("sql_type")]
    public string? SqlType { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

internal sealed record ResultMetadataWire
{
    [JsonPropertyName("is_truncated")]
    public bool? IsTruncated { get; init; }

    [JsonPropertyName("row_count")]
    public long? RowCount { get; init; }
}

internal sealed record SuggestedQuestionsAttachmentWire
{
    [JsonPropertyName("questions")]
    public IReadOnlyList<string>? Questions { get; init; }
}

internal sealed record VizAttachmentWire
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

// Genie returns the SQL Statement Execution API's response verbatim here, so this
// mirrors that contract rather than inventing a Genie-specific result shape.
internal sealed record QueryResultWire
{
    [JsonPropertyName("statement_response")]
    public StatementResponseWire? StatementResponse { get; init; }
}

internal sealed record StatementResponseWire
{
    [JsonPropertyName("statement_id")]
    public string? StatementId { get; init; }

    [JsonPropertyName("manifest")]
    public ManifestWire? Manifest { get; init; }

    [JsonPropertyName("result")]
    public ResultDataWire? Result { get; init; }
}

internal sealed record ManifestWire
{
    [JsonPropertyName("schema")]
    public SchemaWire? Schema { get; init; }

    [JsonPropertyName("total_row_count")]
    public long? TotalRowCount { get; init; }

    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }
}

internal sealed record SchemaWire
{
    [JsonPropertyName("columns")]
    public IReadOnlyList<ColumnWire>? Columns { get; init; }
}

internal sealed record ColumnWire
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    // Both exist. type_text renders precision and scale ("DECIMAL(10,2)"); type_name is the
    // base type ("DECIMAL"). Callers converting a cell need the base type; callers printing a
    // header want the rendered one.
    [JsonPropertyName("type_text")]
    public string? TypeText { get; init; }

    [JsonPropertyName("type_name")]
    public string? TypeName { get; init; }

    [JsonPropertyName("position")]
    public int? Position { get; init; }
}

internal sealed record ResultDataWire
{
    // Values arrive as JSON strings, with null for SQL NULL. They stay strings all the
    // way to the renderer: parsing a DECIMAL into a double to print it is how a client
    // silently changes someone's revenue figure.
    [JsonPropertyName("data_array")]
    public IReadOnlyList<IReadOnlyList<string?>>? DataArray { get; init; }

    [JsonPropertyName("row_count")]
    public long? RowCount { get; init; }

    [JsonPropertyName("chunk_index")]
    public int? ChunkIndex { get; init; }

    // Present when the result continues beyond this chunk. The client reads only the first
    // chunk, so this is what stops a partial result being reported as complete.
    [JsonPropertyName("next_chunk_index")]
    public int? NextChunkIndex { get; init; }

    // Under EXTERNAL_LINKS disposition the rows are NOT inline: data_array is absent and the
    // data sits behind presigned URLs. Deserialised solely so that case can be detected and
    // refused — returning zero rows as a successful, complete result would be worse than any
    // error this client can raise.
    [JsonPropertyName("external_links")]
    public IReadOnlyList<ExternalLinkWire>? ExternalLinks { get; init; }
}

internal sealed record ExternalLinkWire
{
    [JsonPropertyName("chunk_index")]
    public int? ChunkIndex { get; init; }

    [JsonPropertyName("row_count")]
    public long? RowCount { get; init; }
}

internal sealed record DownloadHandleWire
{
    [JsonPropertyName("download_id")]
    public string? DownloadId { get; init; }

    // Bearer-equivalent. Never logged; see DiagnosticRedaction.
    [JsonPropertyName("download_id_signature")]
    public string? DownloadIdSignature { get; init; }
}

internal sealed record FeedbackRequestWire
{
    [JsonPropertyName("rating")]
    public string? Rating { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }
}

internal sealed record StartConversationRequestWire
{
    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

internal sealed record ApiErrorWire
{
    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
