using System.CommandLine;
using System.Text.Json;
using LakeSpeak.Genie;
using LakeSpeak.Rendering;

namespace LakeSpeak.Cli.Commands;

internal static class AgentsCommand
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static Command Create()
    {
        var list = new Command("list", "List the Genie Agents this identity can see.");
        list.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(parseResult, ListAsync, cancellationToken));

        var agents = new Command("agents", "Discover Genie Agents.");
        agents.Subcommands.Add(list);
        return agents;
    }

    private static async Task<int> ListAsync(CliHost host, CancellationToken cancellationToken)
    {
        host.Output.Status("Listing Genie Agents…");

        var agents = new List<GenieAgent>();
        await foreach (var agent in host.Client.ListAllAgentsAsync(cancellationToken).ConfigureAwait(false))
        {
            agents.Add(agent);
        }

        if (agents.Count == 0)
        {
            // Not an error: an identity with no Genie grants legitimately sees nothing, and the
            // fix is a Databricks permission rather than anything this tool can do.
            host.Output.Warn(
                "No Genie Agents are visible to this identity. Access is granted in Databricks.");
            return ExitCode.Success;
        }

        switch (host.Format)
        {
            case OutputFormat.Json:
                host.Output.WriteResultLine(JsonSerializer.Serialize(
                    agents.Select(a => new { id = a.AgentId, title = a.Title, description = a.Description }),
                    Indented));
                break;

            case OutputFormat.Jsonl:
                foreach (var agent in agents)
                {
                    host.Output.WriteResultLine(JsonSerializer.Serialize(
                        new { id = agent.AgentId, title = agent.Title }));
                }

                break;

            case OutputFormat.Csv:
                host.Output.WriteResultLine("id,title");
                foreach (var agent in agents)
                {
                    host.Output.WriteResultLine($"{agent.AgentId},{Escape(agent.Title)}");
                }

                break;

            default:
                host.Renderer.WriteAgents(agents);
                break;
        }

        return ExitCode.Success;
    }

    private static string Escape(string value) =>
        value.AsSpan().ContainsAny(",\"\r\n")
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
