namespace LakeSpeak.Genie;

/// <summary>
/// Normalized message lifecycle state.
/// </summary>
/// <remarks>
/// Databricks documents the platform status set as open-ended, so this closed enum is the
/// boundary: unrecognised platform states map to <see cref="Unknown"/> rather than throwing.
/// A status Databricks adds next month must not crash a released client, which is why nothing
/// switches exhaustively over the platform's own values.
/// </remarks>
public enum GenieMessageState
{
    /// <summary>A status this version of the client does not recognise. Treated as non-terminal.</summary>
    Unknown = 0,

    Submitted,

    /// <summary>
    /// Genie is working: selecting context, fetching metadata, or waiting on the model.
    /// Collapses <c>FILTERING_CONTEXT</c>, <c>FETCHING_METADATA</c> and <c>ASKING_AI</c>, which
    /// callers have no reason to distinguish.
    /// </summary>
    Thinking,

    /// <summary>Waiting for a SQL warehouse to become available.</summary>
    PendingWarehouse,

    ExecutingQuery,

    Completed,

    Failed,

    Cancelled,

    /// <summary>
    /// The message succeeded but its cached query result has aged out. Not a failure: the
    /// answer and generated SQL are still valid, and the result can be recovered by
    /// re-executing the attachment query rather than re-asking the question.
    /// </summary>
    QueryResultExpired,
}

public static class GenieMessageStateExtensions
{
    /// <summary>
    /// Whether polling should stop. <see cref="GenieMessageState.Unknown"/> is deliberately
    /// non-terminal: an unrecognised status is more likely a new intermediate step than a new
    /// terminal one, and treating it as terminal would truncate a working conversation.
    /// </summary>
    public static bool IsTerminal(this GenieMessageState state) => state switch
    {
        GenieMessageState.Completed => true,
        GenieMessageState.Failed => true,
        GenieMessageState.Cancelled => true,
        GenieMessageState.QueryResultExpired => true,
        _ => false,
    };

    internal static GenieMessageState FromWire(string? status) => status switch
    {
        "SUBMITTED" => GenieMessageState.Submitted,
        "FILTERING_CONTEXT" => GenieMessageState.Thinking,
        "FETCHING_METADATA" => GenieMessageState.Thinking,
        "ASKING_AI" => GenieMessageState.Thinking,
        "PENDING_WAREHOUSE" => GenieMessageState.PendingWarehouse,
        "EXECUTING_QUERY" => GenieMessageState.ExecutingQuery,
        "COMPLETED" => GenieMessageState.Completed,
        "FAILED" => GenieMessageState.Failed,
        "CANCELLED" => GenieMessageState.Cancelled,
        "QUERY_RESULT_EXPIRED" => GenieMessageState.QueryResultExpired,
        _ => GenieMessageState.Unknown,
    };

    /// <summary>Progress text for a human, phrased in product terms rather than API mechanics.</summary>
    public static string ToProgressDescription(this GenieMessageState state) => state switch
    {
        GenieMessageState.Submitted => "Sending your question",
        GenieMessageState.Thinking => "Genie is analyzing your question",
        GenieMessageState.PendingWarehouse => "Waiting for a SQL warehouse",
        GenieMessageState.ExecutingQuery => "Executing query",
        GenieMessageState.Completed => "Preparing answer",
        GenieMessageState.Failed => "Failed",
        GenieMessageState.Cancelled => "Cancelled",
        GenieMessageState.QueryResultExpired => "Query result expired",
        GenieMessageState.Unknown => "Working",
        _ => "Working",   // unreachable for any value FromWire can produce; required by the compiler
    };
}
