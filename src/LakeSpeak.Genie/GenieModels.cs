namespace LakeSpeak.Genie;

/// <summary>A Genie Agent. Called a "space" by the REST API; see docs/planning/genie-api-surface.md.</summary>
public sealed record GenieAgent(
    string AgentId,
    string Title,
    string? Description = null,
    string? WarehouseId = null);

/// <summary>One page of agents. <see cref="NextPageToken"/> is null on the last page.</summary>
public sealed record GenieAgentPage(
    IReadOnlyList<GenieAgent> Agents,
    string? NextPageToken);

public sealed record GenieConversationRef(
    string AgentId,
    string ConversationId,
    string? Title = null);

/// <summary>
/// A normalized Genie response: the answer text, the generated SQL if any, the query result if
/// requested, and the identifiers needed to trace or re-open the conversation.
/// </summary>
public sealed record GenieResponse(
    string AgentId,
    string ConversationId,
    string MessageId,
    GenieMessageState State,
    string? Text,
    GenieQuery? Query,
    GenieQueryResult? Result,
    IReadOnlyList<string> SuggestedQuestions,
    GenieResponseMetadata Metadata)
{
    /// <summary>True when a visualization attachment was present. v0.1 reports it and does not render it.</summary>
    public bool HasVisualization { get; init; }
}

/// <summary>
/// The SQL Genie generated, and the parameters it bound.
/// </summary>
/// <remarks>
/// There is deliberately no trusted-asset flag. The plan this project was built from proposed
/// one, but no such field exists on the API's query attachment; surfacing it would have meant
/// inventing a trust signal and showing it to users as if Databricks had provided it.
/// </remarks>
public sealed record GenieQuery(
    string? Sql,
    string? Title = null,
    string? Description = null,
    string? StatementId = null,
    IReadOnlyList<GenieQueryParameter>? Parameters = null);

public sealed record GenieQueryParameter(string? Keyword, string? SqlType, string? Value);

public sealed record GenieColumn(string Name, string? DataType);

/// <summary>
/// A tabular result.
/// </summary>
/// <remarks>
/// Cells stay as strings, exactly as the API returned them, with null for SQL NULL. Parsing a
/// DECIMAL into a double so it can be printed is how a client silently changes someone's revenue
/// figure; callers that want a typed value convert deliberately, with the column type in hand.
/// </remarks>
public sealed record GenieQueryResult(
    IReadOnlyList<GenieColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool IsTruncated,
    long? TotalRowCount)
{
    public int RowCount => Rows.Count;
}

public sealed record GenieResponseMetadata(
    TimeSpan Duration,
    int PollCount,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? LastUpdatedAt = null,
    string? AttachmentId = null);

public enum GenieFeedbackRating
{
    Positive,
    Negative,
    None,
}

/// <summary>Options for a single ask. Null values fall back to <see cref="GenieClientOptions"/>.</summary>
public sealed record GenieAskOptions
{
    /// <summary>Fetch the query result when the response carries a query attachment. Default true.</summary>
    public bool IncludeQueryResult { get; init; } = true;

    /// <summary>Overall wait for a terminal state. Null uses the client default.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Invoked on each observed state change. Used by the CLI to drive progress output.</summary>
    public Action<GenieMessageState>? OnStateChanged { get; init; }
}
