using System.CommandLine;
using LakeSpeak.Cli.Commands;

namespace LakeSpeak.Cli;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var root = new RootCommand(
            "LakeSpeak.NET — talk to governed Databricks data from your terminal.\n" +
            "An independent open-source project, not affiliated with Databricks.");

        root.Options.Add(GlobalOptions.Profile);
        root.Options.Add(GlobalOptions.Format);
        root.Options.Add(GlobalOptions.Quiet);
        root.Options.Add(GlobalOptions.Verbose);

        root.Subcommands.Add(AgentsCommand.Create());
        root.Subcommands.Add(AskCommand.Create());
        root.Subcommands.Add(ChatCommand.Create());
        root.Subcommands.Add(AuthCommand.Create());
        root.Subcommands.Add(ConfigCommand.Create());
        root.Subcommands.Add(PackCommand.Create());

        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }
}
