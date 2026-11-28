using System.Text;
using LakeSpeak.Rendering;

namespace LakeSpeak.Cli.Console;

/// <summary>
/// Forwards raw result text to a terminal with control characters replaced.
/// </summary>
/// <remarks>
/// Machine-readable formats such as CSV write cells exactly as Databricks returned them, which is
/// right for a file or a pipe and wrong for a terminal, where an ESC in a cell is a command. The
/// writer is stateless per character, so a sequence split across writes is still neutralised.
/// </remarks>
internal sealed class TerminalSafeWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public override IFormatProvider FormatProvider => inner.FormatProvider;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string NewLine
    {
        get => inner.NewLine;
        set
        {
            inner.NewLine = value;
            base.NewLine = value;
        }
    }

    public override void Write(char value) => inner.Write(TerminalSafety.Sanitize(value.ToString()));

    public override void Write(string? value) => inner.Write(TerminalSafety.Sanitize(value));

    public override void Write(char[] buffer, int index, int count) =>
        inner.Write(TerminalSafety.Sanitize(new string(buffer, index, count)));

    public override void Write(ReadOnlySpan<char> buffer) => inner.Write(TerminalSafety.Sanitize(buffer.ToString()));

    public override void WriteLine(string? value) => inner.WriteLine(TerminalSafety.Sanitize(value));

    public override void WriteLine(ReadOnlySpan<char> buffer) =>
        inner.WriteLine(TerminalSafety.Sanitize(buffer.ToString()));

    public override Task WriteAsync(string? value) => inner.WriteAsync(TerminalSafety.Sanitize(value));

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(TerminalSafety.Sanitize(buffer.ToString()).AsMemory(), cancellationToken);

    public override Task WriteLineAsync(string? value) => inner.WriteLineAsync(TerminalSafety.Sanitize(value));

    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteLineAsync(TerminalSafety.Sanitize(buffer.ToString()).AsMemory(), cancellationToken);

    public override void Flush() => inner.Flush();

    public override Task FlushAsync() => inner.FlushAsync();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
}
