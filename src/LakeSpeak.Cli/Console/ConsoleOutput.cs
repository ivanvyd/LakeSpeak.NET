using LakeSpeak.Rendering;
using Spectre.Console;

namespace LakeSpeak.Cli.Console;

/// <summary>
/// Decides where output goes and whether it may be decorated.
/// </summary>
/// <remarks>
/// Two rules hold everywhere: results go to stdout and diagnostics go to stderr, so a pipeline
/// can consume one without the other; and no ANSI reaches stdout when the format is machine
/// readable, output is redirected, or NO_COLOR is set. A spinner written into a JSON pipe is a
/// parse error in someone's script.
/// </remarks>
public sealed class ConsoleOutput
{
    private readonly OutputFormat _format;
    private readonly TextWriter _stdout;

    /// <param name="format">Output format, which decides whether decoration is allowed.</param>
    /// <param name="quiet">Suppress progress messages on stderr.</param>
    /// <param name="stdout">
    /// Where results go. Defaults to the process stdout; a test supplies a StringWriter so CLI
    /// output can be asserted without spawning a process.
    /// </param>
    public ConsoleOutput(OutputFormat format, bool quiet = false, TextWriter? stdout = null)
    {
        _format = format;
        _stdout = stdout ?? System.Console.Out;
        Quiet = quiet;

        var redirected = System.Console.IsOutputRedirected;
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 };

        IsInteractive = !redirected
            && !noColor
            && !format.IsMachineReadable()
            && !System.Console.IsInputRedirected;

        Error = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(System.Console.Error),
            Ansi = IsInteractive ? AnsiSupport.Detect : AnsiSupport.No,
            ColorSystem = IsInteractive ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
        });

        Out = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(System.Console.Out),
            Ansi = IsInteractive ? AnsiSupport.Detect : AnsiSupport.No,
            ColorSystem = IsInteractive ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
        });
    }

    /// <summary>Whether progress, colour and prompts are allowed.</summary>
    public bool IsInteractive { get; }

    public bool Quiet { get; }

    /// <summary>Results. Never carries diagnostics.</summary>
    public IAnsiConsole Out { get; }

    /// <summary>Diagnostics, progress and errors.</summary>
    public IAnsiConsole Error { get; }

    public int Width => IsInteractive ? System.Console.WindowWidth : 120;

    /// <summary>Writes raw text to stdout with no markup interpretation.</summary>
    public void WriteResult(string text) => _stdout.Write(text);

    public void WriteResultLine(string text) => _stdout.WriteLine(text);

    public void Status(string message)
    {
        if (!Quiet && !_format.IsMachineReadable())
        {
            Error.MarkupLine($"[dim]{Markup.Escape(TerminalSafety.Sanitize(message))}[/]");
        }
    }

    public void Warn(string message) =>
        Error.MarkupLine($"[yellow]warning:[/] {Markup.Escape(TerminalSafety.Sanitize(message))}");

    public void Fail(string message) =>
        Error.MarkupLine($"[red]error:[/] {Markup.Escape(TerminalSafety.Sanitize(message))}");
}
