using System.CommandLine;
using LakeSpeak.Configuration;
using LakeSpeak.Rendering;
using Spectre.Console;

namespace LakeSpeak.Cli.Commands;

internal static class ExportCommand
{
    private static readonly Argument<string> Target =
        new("target")
        {
            Description = "Which result to export. Currently only 'last'.",
            DefaultValueFactory = _ => "last",
        };

    private static readonly Option<string?> Output =
        new("--output", "-o") { Description = "File to write. Defaults to stdout." };

    private static readonly Option<bool> Force =
        new("--force") { Description = "Overwrite the output file if it exists." };

    internal static Command Create()
    {
        var command = new Command("export", "Export the last query result without opening a chat session.")
        {
            Target,
            Output,
            Force,
        };

        command.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(parseResult, (host, ct) => RunAsync(host, parseResult, ct), cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(CliHost host, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue(Target);
        if (!string.Equals(target, "last", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                $"Unknown target '{target}'. Only 'last' is supported in this version.");
        }

        var recent = RecentConversation.Load()
            ?? throw new CliUsageException("No previous answer to export. Run `lakespeak ask` first.");

        if (recent.AttachmentId is null
            || recent.AgentId is null
            || recent.ConversationId is null
            || recent.MessageId is null)
        {
            throw new CliUsageException(
                "The last answer had no query result to export. Only answers backed by a query can be exported.");
        }

        // The result is re-fetched rather than cached locally: keeping rows on disk between
        // commands would be a second copy of governed data with none of the governance, and
        // Databricks is already the system of record.
        var result = await host.Client.GetQueryResultAsync(
            recent.AgentId, recent.ConversationId, recent.MessageId, recent.AttachmentId, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            throw new CliUsageException(
                "Databricks no longer has that query result. Cached results expire; ask the question again.");
        }

        var csv = CsvWriter.Write(result);
        var path = parseResult.GetValue(Output);

        if (path is null)
        {
            host.Output.WriteResult(csv);
            return ExitCode.Success;
        }

        var full = Path.GetFullPath(path);
        if (File.Exists(full) && !parseResult.GetValue(Force))
        {
            throw new CliUsageException($"{full} already exists. Pass --force to overwrite it.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, csv, cancellationToken).ConfigureAwait(false);

        host.Output.Error.MarkupLine(
            $"[green]Wrote[/] {Markup.Escape(full)} [dim]({result.RowCount} rows). " +
            "It contains governed data; look after it.[/]");

        if (result.IsTruncated)
        {
            // Loud, because an export that is quietly partial is the failure mode that matters.
            host.Output.Warn(
                "This result is incomplete — Databricks truncated it, or it continues beyond the " +
                "rows this version reads. Narrow the question to export everything.");
        }

        return ExitCode.Success;
    }
}
