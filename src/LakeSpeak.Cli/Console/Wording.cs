namespace LakeSpeak.Cli.Console;

/// <summary>Shared wording helpers for messages a person reads.</summary>
internal static class Wording
{
    /// <summary>
    /// Formats a count with its noun, agreeing in number: <c>1 Agent</c>, <c>3 Agents</c>.
    /// </summary>
    /// <remarks>
    /// A helper rather than four call sites each appending an "s", because that is how a tool
    /// ends up reporting "1 Agents visible" and "1 rows" — small, but it is the output people
    /// see first and it reads as carelessness about everything underneath it.
    /// English-only, deliberately: this tool has no localisation, and pretending otherwise
    /// would be a bigger lie than the missing plural rules.
    /// </remarks>
    internal static string Count(int count, string singular, string? plural = null) =>
        count == 1 ? $"{count} {singular}" : $"{count} {plural ?? singular + "s"}";
}
