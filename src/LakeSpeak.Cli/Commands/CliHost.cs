using System.CommandLine;
using LakeSpeak.Application;
using LakeSpeak.Cli.Console;
using LakeSpeak.Configuration;
using LakeSpeak.Genie;
using LakeSpeak.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LakeSpeak.Cli.Commands;

/// <summary>Builds the services a command needs and maps failures onto exit codes.</summary>
internal sealed class CliHost : IDisposable
{
    private readonly ServiceProvider _services;

    private CliHost(ServiceProvider services, ConsoleOutput output, LakeSpeakConfig config, OutputFormat format)
    {
        _services = services;
        Output = output;
        Config = config;
        Format = format;
    }

    internal ConsoleOutput Output { get; }

    internal LakeSpeakConfig Config { get; }

    internal OutputFormat Format { get; }

    internal IGenieClient Client => _services.GetRequiredService<IGenieClient>();

    internal AgentResolver Resolver => new(Client, Config);

    internal TerminalRenderer Renderer => new(Output.Out, Config.Display.MaxRows);

    internal static CliHost Create(ParseResult parseResult)
    {
        var config = LakeSpeakConfig.Load();

        var format = GlobalOptions.ResolveFormat(parseResult, config.Defaults.Output);
        var quiet = parseResult.GetValue(GlobalOptions.Quiet);
        var output = new ConsoleOutput(format, quiet);

        // Precedence: explicit flag, then the profile the last answer came from, then the
        // config default. The pointer sits above the default so `export last` and
        // `feedback last` address the workspace the conversation actually lives in rather than
        // whichever profile happens to be configured. Environment and .databrickscfg are
        // consulted below this, inside AddLakeSpeak.
        var profile = parseResult.GetValue(GlobalOptions.Profile)
            ?? RecentConversation.Load()?.Profile
            ?? config.Defaults.Profile;

        var services = new ServiceCollection();

        // Every record is scrubbed on its way out by RedactingStderrLoggerProvider, which is what
        // makes the --verbose help text's redaction promise true rather than aspirational.
        if (parseResult.GetValue(GlobalOptions.Verbose))
        {
            services.AddLogging(builder => builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddProvider(new RedactingStderrLoggerProvider()));
        }

        services.AddLakeSpeak(options => options.Profile = profile);

        return new CliHost(services.BuildServiceProvider(), output, config, format);
    }

    /// <summary>
    /// Runs a command body, turning every expected failure into a message on stderr and an
    /// exit code, rather than a stack trace.
    /// </summary>
    internal static async Task<int> RunAsync(
        ParseResult parseResult,
        Func<CliHost, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken)
    {
        CliHost? host = null;
        try
        {
            host = Create(parseResult);
            return await body(host, cancellationToken).ConfigureAwait(false);
        }
        catch (CliUsageException ex)
        {
            System.Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCode.InvalidUsage;
        }
        catch (GenieException ex)
        {
            host?.Output.Fail(ex.Message);
            return ExitCode.From(ex.Kind);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is a normal way to end a long question, not an error to shout about.
            host?.Output.Status("Cancelled.");
            return ExitCode.Timeout;
        }
        catch (ArgumentException ex)
        {
            // A bad combination of arguments is the caller's mistake, so it exits as invalid
            // usage rather than as an unexpected failure a script would treat as a crash.
            host?.Output.Fail(ex.Message);
            return ExitCode.InvalidUsage;
        }
        catch (InvalidOperationException ex)
        {
            // Configuration problems surface as this: a malformed config file, or no host.
            host?.Output.Fail(ex.Message);
            return ExitCode.InvalidUsage;
        }
        finally
        {
            host?.Dispose();
        }
    }

    public void Dispose() => _services.Dispose();
}
