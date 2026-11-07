namespace LakeSpeak.Cli.Console;

/// <summary>Shared wording helpers for messages a person reads.</summary>
internal static class Wording
{
    /// <summary>
    /// Formats a count with its noun, agreeing in number: <c>1 Agent</c>, <c>3 Agents</c>.
    /// </summary>
    /// <remarks>
    /// English-only. Supply an explicit plural for nouns that do not take an "s".
    /// </remarks>
    internal static string Count(int count, string singular, string? plural = null) =>
        count == 1 ? $"{count} {singular}" : $"{count} {plural ?? singular + "s"}";
}
