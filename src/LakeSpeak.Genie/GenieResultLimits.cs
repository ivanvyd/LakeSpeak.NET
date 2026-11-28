namespace LakeSpeak.Genie;

/// <summary>Finite limits for strict results; exceeding a limit rejects the operation.</summary>
/// <remarks>
/// Rows and decoded JSON body bytes are aggregated across all query attachments and chunks in
/// Ask/FollowUp, or across a single GetQueryResult/ReExecute call. Each other response envelope
/// has a separate MaxBytes bound. These are not CLR heap or compressed wire byte limits.
/// Strict mode rejects incomplete results instead of returning partial rows; the failure's
/// <see cref="GenieException.ErrorCode"/> is one of the <see cref="GenieErrorCodes"/> values. Metadata-only
/// operations (including IncludeQueryResult=false) do not establish result completeness.
/// </remarks>
public sealed record GenieResultLimits(int MaxRows, int MaxBytes)
{
    internal void Validate()
    {
        if (MaxRows < 1 || MaxBytes < 1)
        {
            throw new InvalidOperationException("Strict result MaxRows and MaxBytes must be positive.");
        }
    }
}
