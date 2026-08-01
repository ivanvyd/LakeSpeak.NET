using LakeSpeak.Genie;
using Microsoft.Extensions.Logging;

namespace LakeSpeak.Cli.Commands;

/// <summary>
/// Writes diagnostics to stderr with every record scrubbed.
/// </summary>
/// <remarks>
/// Diagnostics go to stderr so `--verbose` never corrupts machine-readable stdout. Scrubbing
/// happens here, at the single point every record passes through, rather than at each call site:
/// a log line is exactly the kind of thing that ends up in a CI transcript or a pasted bug
/// report, and one forgotten call site would be enough to disclose a credential.
/// </remarks>
internal sealed class RedactingStderrLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RedactingStderrLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class RedactingStderrLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = DiagnosticRedaction.Scrub(formatter(state, exception));
            var name = category.Split('.')[^1];

            System.Console.Error.WriteLine($"  [{logLevel.ToString().ToLowerInvariant()}] {name}: {line}");

            if (exception is not null)
            {
                System.Console.Error.WriteLine(
                    $"  [{logLevel.ToString().ToLowerInvariant()}] {name}: {DiagnosticRedaction.Scrub(exception.ToString())}");
            }
        }
    }
}
