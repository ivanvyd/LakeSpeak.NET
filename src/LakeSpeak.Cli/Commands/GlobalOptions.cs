using System.CommandLine;
using LakeSpeak.Rendering;

namespace LakeSpeak.Cli.Commands;

// Recursive so they are accepted on every subcommand. Without this they parse only
// immediately after the executable name, which is not where anyone types them: the whole
// point of `--profile` is `lakespeak ask --profile prod "..."`.
internal static class GlobalOptions
{
    internal static readonly Option<string?> Profile =
        new("--profile", "-p")
        {
            Description = "Databricks CLI profile to authenticate with.",
            Recursive = true,
        };

    internal static readonly Option<string> Format =
        new("--format", "-f")
        {
            Description = "Output format: text, table, markdown, json, jsonl, csv.",
            Recursive = true,
            DefaultValueFactory = _ => "text",
        };

    internal static readonly Option<bool> Quiet =
        new("--quiet", "-q")
        {
            Description = "Suppress progress output on stderr.",
            Recursive = true,
        };

    internal static readonly Option<bool> Verbose =
        new("--verbose")
        {
            Description = "Print diagnostic detail to stderr. Credentials are always redacted.",
            Recursive = true,
        };

    /// <param name="parseResult">The parsed command line.</param>
    /// <param name="configuredDefault">
    /// `defaults.output` from the config file, used when the flag is absent. Passed in rather
    /// than read here so this stays a pure function of its inputs.
    /// </param>
    internal static OutputFormat ResolveFormat(ParseResult parseResult, string? configuredDefault = null)
    {
        // The flag wins; the configured default is the fallback. System.CommandLine always
        // supplies "text" for an absent flag, so an explicit default is indistinguishable from
        // no flag — which is why the config value is only consulted when the result is "text".
        var raw = parseResult.GetValue(Format);
        if (raw == "text" && configuredDefault is { Length: > 0 })
        {
            raw = configuredDefault;
        }

        if (!OutputFormatExtensions.TryParse(raw, out var format))
        {
            throw new CliUsageException(
                $"Unknown format '{raw}'. Valid values: text, table, markdown, json, jsonl, csv.");
        }

        return format;
    }
}

/// <summary>A problem with what the user typed, as opposed to a failure talking to Databricks.</summary>
internal sealed class CliUsageException(string message) : Exception(message);
