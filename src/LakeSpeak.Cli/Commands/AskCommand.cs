using System.CommandLine;
using LakeSpeak.Genie;
using LakeSpeak.Rendering;

namespace LakeSpeak.Cli.Commands;

internal static class AskCommand
{
    private static readonly Argument<string> Question =
        new("question") { Description = "The question to ask." };

    private static readonly Option<string?> Agent =
        new("--agent", "-a") { Description = "Agent id, title, or a configured alias." };

    private static readonly Option<bool> ShowSql =
        new("--show-sql") { Description = "Print the generated SQL alongside the answer." };

    internal static Command Create()
    {
        var command = new Command("ask", "Ask a question and print the answer.")
        {
            Question,
            Agent,
            ShowSql,
        };

        command.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(parseResult, (host, ct) =>
                RunAsync(host, parseResult, ct), cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(CliHost host, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var question = parseResult.GetValue(Question)
            ?? throw new CliUsageException("A question is required.");

        var agentName = parseResult.GetValue(Agent) ?? host.Config.Defaults.Agent
            ?? throw new CliUsageException(
                "No Agent specified. Pass --agent, or set defaults.agent in your config file. " +
                "Run `lakespeak agents list` to see what is available.");

        var resolution = await host.Resolver.ResolveAsync(agentName, cancellationToken).ConfigureAwait(false);

        if (resolution.IsAmbiguous)
        {
            // Never guessed, in any mode. Picking the first of two Agents called "Finance"
            // would answer against the wrong data and look like it worked.
            var ids = string.Join(", ", resolution.Candidates.Select(c => c.AgentId));
            throw new CliUsageException(
                $"'{agentName}' matches {resolution.Candidates.Count} Agents ({ids}). Use the id.");
        }

        if (resolution.NotFound)
        {
            throw new CliUsageException(
                $"No Genie Agent matches '{agentName}'. Run `lakespeak agents list` to see what is available.");
        }

        var agent = resolution.Agent!;
        var showSql = parseResult.GetValue(ShowSql) || host.Config.Display.ShowSqlByDefault;

        var response = await host.Client.AskAsync(
            agent.AgentId,
            question,
            new GenieAskOptions
            {
                IncludeQueryResult = true,
                OnStateChanged = state => host.Output.Status(state.ToProgressDescription() + "…"),
            },
            cancellationToken).ConfigureAwait(false);

        Write(host, response, agent, showSql);
        return ExitCode.Success;
    }

    private static void Write(CliHost host, GenieResponse response, GenieAgent agent, bool showSql)
    {
        switch (host.Format)
        {
            case OutputFormat.Json:
                host.Output.WriteResultLine(MachineOutput.ToJson(response, agent.Title));
                break;

            case OutputFormat.Jsonl:
                host.Output.WriteResult(MachineOutput.ToJsonLines(response, agent.Title));
                break;

            case OutputFormat.Csv:
                if (response.Result is null)
                {
                    // CSV means the query result, and there is no honest way to render prose as
                    // rows. Saying so on stderr keeps stdout empty and parseable.
                    host.Output.Warn("This answer has no query result, so there is nothing to write as CSV.");
                    break;
                }

                host.Output.WriteResult(CsvWriter.Write(response.Result));
                break;

            case OutputFormat.Markdown:
                host.Output.WriteResult(MarkdownWriter.Write(response, agent.Title));
                break;

            case OutputFormat.Table:
                if (response.Result is not null)
                {
                    host.Renderer.WriteResult(response.Result);
                }

                break;

            case OutputFormat.Text:
            default:
                host.Renderer.WriteAnswer(response);
                if (response.Result is not null)
                {
                    host.Renderer.WriteResult(response.Result);
                }

                if (showSql && response.Query?.Sql is { Length: > 0 } sql)
                {
                    host.Renderer.WriteSql(sql);
                }

                if (response.State == GenieMessageState.QueryResultExpired)
                {
                    host.Output.Warn(
                        "The cached query result has expired, so no table is shown. The answer and SQL are still valid.");
                }

                break;
        }
    }
}
