using LakeSpeak.Genie;

namespace LakeSpeak.Cli;

/// <summary>
/// Process exit codes.
/// </summary>
/// <remarks>
/// Deliberately small and stable: scripts branch on these, so an existing code must never change
/// meaning. New conditions get a new code or fold into <see cref="Unexpected"/>.
/// </remarks>
public static class ExitCode
{
    public const int Success = 0;
    public const int Unexpected = 1;
    public const int InvalidUsage = 2;
    public const int Authentication = 3;
    public const int Authorization = 4;
    public const int NotFound = 5;
    public const int GenieFailure = 6;
    public const int Timeout = 7;
    public const int PartialPackFailure = 8;
    public const int MalformedResponse = 9;

    public static int From(GenieFailureKind kind) => kind switch
    {
        GenieFailureKind.Authentication => Authentication,
        GenieFailureKind.Authorization => Authorization,
        GenieFailureKind.AgentNotFound => NotFound,
        GenieFailureKind.ConversationNotFound => NotFound,
        GenieFailureKind.MessageFailed => GenieFailure,
        GenieFailureKind.MessageCancelled => GenieFailure,
        GenieFailureKind.QueryExecutionFailed => GenieFailure,
        GenieFailureKind.QueryResultExpired => GenieFailure,
        GenieFailureKind.PollingTimeout => Timeout,
        GenieFailureKind.RateLimited => GenieFailure,
        GenieFailureKind.MalformedResponse => MalformedResponse,
        GenieFailureKind.UnsupportedResult => MalformedResponse,
        GenieFailureKind.Network => Unexpected,
        GenieFailureKind.Unexpected => Unexpected,
        // GenieFailureKind is this project's own closed set, so an unmapped value is a bug rather
        // than a platform value to tolerate. This arm makes the switch exhaustive to the
        // compiler, which means a NEW member will not raise CS8509 — the gap is caught by
        // ExitCodeTests.Every_failure_kind_is_mapped in CI, not at build time.
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped failure kind."),
    };
}
