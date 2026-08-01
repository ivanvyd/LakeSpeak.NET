namespace LakeSpeak.Rendering;

public enum OutputFormat
{
    /// <summary>Prose answer with a result table when one is present. The default for a terminal.</summary>
    Text,

    /// <summary>Result table only, no prose.</summary>
    Table,

    Markdown,

    /// <summary>One JSON document with a versioned schema.</summary>
    Json,

    /// <summary>One JSON object per result row. For streaming into other tools.</summary>
    Jsonl,

    /// <summary>The query result as CSV. Not the prose answer.</summary>
    Csv,
}

public static class OutputFormatExtensions
{
    /// <summary>
    /// Whether this format is meant for another program rather than a person.
    /// </summary>
    /// <remarks>
    /// Machine formats suppress spinners, colour and decoration, and send every diagnostic to
    /// stderr so stdout stays parseable. Interactive prompts are also disabled: a script that
    /// blocks on a hidden prompt looks like a hang.
    /// </remarks>
    public static bool IsMachineReadable(this OutputFormat format) =>
        format is OutputFormat.Json or OutputFormat.Jsonl or OutputFormat.Csv;

    public static bool TryParse(string? value, out OutputFormat format)
    {
        format = OutputFormat.Text;
        return value is not null && Enum.TryParse(value, ignoreCase: true, out format);
    }
}
