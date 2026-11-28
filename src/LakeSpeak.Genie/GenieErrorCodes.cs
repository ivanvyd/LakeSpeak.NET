namespace LakeSpeak.Genie;

/// <summary>
/// <see cref="GenieException.ErrorCode"/> values that this client assigns itself, as opposed to
/// codes copied from a Databricks error response.
/// </summary>
public static class GenieErrorCodes
{
    /// <summary>
    /// A strict result exceeded its <see cref="GenieResultLimits"/> row, byte or chunk-request
    /// budget. Raised with <see cref="GenieFailureKind.UnsupportedResult"/>; no partial rows are returned.
    /// </summary>
    public const string StrictResultLimit = "STRICT_RESULT_LIMIT";

    /// <summary>
    /// A strict result could not be shown to be complete and inline, for example because of
    /// server truncation, external links, a failed statement or an unusable continuation. Raised
    /// with <see cref="GenieFailureKind.UnsupportedResult"/>; no partial rows are returned.
    /// </summary>
    public const string StrictResultIncomplete = "STRICT_RESULT_INCOMPLETE";
}
