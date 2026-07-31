using System.Text;

namespace LakeSpeak.Rendering;

/// <summary>
/// Strips control characters from text that came back from Databricks.
/// </summary>
/// <remarks>
/// Genie returns model-generated prose and cells drawn from your tables. Both are untrusted for
/// rendering: a value containing ANSI escapes can move the cursor, clear the screen, recolour
/// later output, or draw something that looks like this tool's own prompt. Sanitizing once at
/// the boundary is cheaper and more reliable than auditing every write site.
/// </remarks>
public static class TerminalSafety
{
    private const char Replacement = '�';

    /// <summary>Replaces control characters with U+FFFD, preserving newline, carriage return and tab.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var index = text.AsSpan().IndexOfAnyExcept(SafeAscii);
        if (index < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(IsUnsafe(c) ? Replacement : c);
        }

        return builder.ToString();
    }

    /// <summary>Sanitizes and collapses line breaks, for a value going into a table cell.</summary>
    public static string SanitizeCell(string? text)
    {
        var clean = Sanitize(text);
        return clean.AsSpan().ContainsAny('\n', '\r')
            ? clean.ReplaceLineEndings(" ")
            : clean;
    }

    // char.IsControl already spans U+0000-U+001F (including ESC), U+007F (DEL), and the
    // U+0080-U+009F C1 range that some terminals still decode as escape introducers. Newline,
    // carriage return and tab are the only ones worth keeping.
    private static bool IsUnsafe(char c) =>
        c is not ('\n' or '\r' or '\t') && char.IsControl(c);

    // Plain ASCII is the common case and skips the rebuild entirely. Anything else, including
    // ordinary accented or non-Latin text, falls through to the per-character pass, which
    // replaces only genuine control characters.
    private static readonly System.Buffers.SearchValues<char> SafeAscii =
        System.Buffers.SearchValues.Create(BuildSafeAscii());

    private static string BuildSafeAscii()
    {
        var builder = new StringBuilder("\n\r\t");
        for (var c = ' '; c <= '~'; c++)
        {
            builder.Append(c);
        }

        return builder.ToString();
    }
}
