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
        CliArguments.RequireLastTarget(parseResult.GetValue(Target));

        // The pointer records which profile the conversation lives in. Without consulting it,
        // this command would resolve the profile from the flag or config default and could
        // address a different workspace than the answer came from.
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
            // The cached result aged out. Re-running the attachment's query is the documented
            // recovery, and it is the right call here specifically because the user asked to
            // export: they want the rows, and re-executing costs warehouse time they have
            // implicitly agreed to. `ask` deliberately does not do this on their behalf.
            host.Output.Status("The cached result expired; re-running the query…");

            result = await host.Client.ReExecuteQueryAsync(
                recent.AgentId, recent.ConversationId, recent.MessageId, recent.AttachmentId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (result is null)
        {
            throw new CliUsageException(
                "Databricks returned no result for that query, even after re-running it. Ask the question again.");
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
