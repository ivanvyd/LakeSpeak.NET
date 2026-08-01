using System.CommandLine;
using LakeSpeak.Configuration;
using LakeSpeak.Genie.Authentication;
using Spectre.Console;

namespace LakeSpeak.Cli.Commands;

internal static class ConfigCommand
{
    internal static Command Create()
    {
        var show = new Command("show", "Show the effective configuration and where each value came from.");
        show.SetAction((parseResult, cancellationToken) =>
            CliHost.RunAsync(parseResult, (host, ct) => Task.FromResult(Show(host, parseResult)), cancellationToken));

        var config = new Command("config", "Inspect configuration.");
        config.Subcommands.Add(show);
        return config;
    }

    private static int Show(CliHost host, ParseResult parseResult)
    {
        var console = host.Output.Error;
        var path = LakeSpeakConfig.DefaultPath;

        console.MarkupLine($"Config file: [dim]{Spectre.Console.Markup.Escape(path)}[/]" +
            (File.Exists(path) ? string.Empty : " [dim](not present; defaults in use)[/]"));

        var flagProfile = parseResult.GetValue(GlobalOptions.Profile);
        var envHost = Environment.GetEnvironmentVariable("DATABRICKS_HOST");
        var profile = flagProfile ?? host.Config.Defaults.Profile;

        // Naming the source of each value is the point of this command: "which profile am I
        // actually using" is otherwise guesswork across four layers.
        console.MarkupLine($"Profile: [bold]{Escape(profile ?? "(none)")}[/] [dim]({(flagProfile is not null ? "--profile flag" : host.Config.Defaults.Profile is not null ? "config defaults.profile" : "unset")})[/]");

        var resolvedHost = DatabricksProfiles.ResolveHost(null, profile);
        var hostSource = envHost is { Length: > 0 } ? "DATABRICKS_HOST" : ".databrickscfg";
        console.MarkupLine($"Workspace: [bold]{Escape(resolvedHost?.Host ?? "(unresolved)")}[/] [dim]({hostSource})[/]");

        console.MarkupLine($"Output format: [bold]{host.Format}[/]");
        console.MarkupLine($"Max displayed rows: [bold]{host.Config.Display.MaxRows}[/]");
        console.MarkupLine($"Configured aliases: [bold]{host.Config.Agents.Count}[/]");

        foreach (var (alias, value) in host.Config.Agents)
        {
            console.MarkupLine($"  [dim]{Escape(alias)}[/] → {Escape(value.Id ?? "(no id)")}");
        }

        // No token, no secret, and no query result is ever printed here, so the output of this
        // command is safe to paste into an issue.
        console.MarkupLine("[dim]No credentials are stored by LakeSpeak or shown here.[/]");
        return ExitCode.Success;
    }

    private static string Escape(string value) => Spectre.Console.Markup.Escape(value);
}
