using System.Globalization;
using LakeSpeak.Genie;
using Spectre.Console;

namespace LakeSpeak.Rendering;

/// <summary>Renders a Genie response for a person reading a terminal.</summary>
public sealed class TerminalRenderer(IAnsiConsole console, int maxRows = 50)
{
    public void WriteAnswer(GenieResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            // Escaped, not interpreted: the answer is model-generated and may contain square
            // brackets that Spectre would otherwise read as markup.
            console.WriteLine(TerminalSafety.Sanitize(response.Text));
            console.WriteLine();
        }
    }

    public void WriteResult(GenieQueryResult result)
    {
        if (result.Columns.Count == 0)
        {
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Expand();

        foreach (var column in result.Columns)
        {
            table.AddColumn(new TableColumn($"[bold]{Markup.Escape(TerminalSafety.SanitizeCell(column.Name))}[/]"));
        }

        var shown = Math.Min(result.Rows.Count, maxRows);
        for (var i = 0; i < shown; i++)
        {
            table.AddRow(result.Rows[i].Select(FormatCell).ToArray());
        }

        console.Write(table);

        WriteRowCountNotice(result, shown);
    }

    /// <summary>
    /// States plainly when rows are missing from the display, and why.
    /// </summary>
    /// <remarks>
    /// Two different kinds of "not all of it" get separate sentences. Truncation happened at
    /// Databricks and the missing rows are unavailable to any export; the display limit is
    /// local and the full result is still exported. Conflating them would tell someone their
    /// CSV is incomplete when it is not, or the reverse.
    /// </remarks>
    private void WriteRowCountNotice(GenieQueryResult result, int shown)
    {
        if (shown < result.Rows.Count)
        {
            console.MarkupLine(
                $"[dim]Showing {shown} of {result.Rows.Count} rows. Export to see all of them.[/]");
        }

        if (result.IsTruncated)
        {
            var total = result.TotalRowCount is { } n
                ? n.ToString("N0", CultureInfo.InvariantCulture)
                : "an unknown number of";
            console.MarkupLine(
                $"[yellow]Databricks truncated this result[/][dim]; it holds {total} rows in full. " +
                $"Narrow the question to see the rest.[/]");
        }
    }

    public void WriteSql(string sql, IReadOnlyList<GenieQueryParameter>? parameters = null)
    {
        var panel = new Panel(Markup.Escape(TerminalSafety.Sanitize(sql)))
            .Header("Generated SQL")
            .Border(BoxBorder.Rounded);

        console.Write(panel);

        // Without these, a reader sees placeholders and has to guess what the statement ran
        // with — which is half of what showing the SQL was for.
        if (parameters is not { Count: > 0 })
        {
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Parameter[/]");
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Value[/]");

        foreach (var parameter in parameters)
        {
            table.AddRow(
                Markup.Escape(TerminalSafety.SanitizeCell(parameter.Keyword)),
                Markup.Escape(TerminalSafety.SanitizeCell(parameter.SqlType)),
                Markup.Escape(TerminalSafety.SanitizeCell(parameter.Value)));
        }

        console.Write(table);
    }

    public void WriteAgents(IReadOnlyList<GenieAgent> agents)
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[bold]Agent[/]");
        table.AddColumn("[bold]Id[/]");

        foreach (var agent in agents)
        {
            table.AddRow(
                Markup.Escape(TerminalSafety.SanitizeCell(agent.Title)),
                $"[dim]{Markup.Escape(agent.AgentId)}[/]");
        }

        console.Write(table);
    }

    // A SQL NULL is dimmed rather than blank, so an empty string and a null are visibly
    // different in a terminal. The value itself is never reformatted.
    private static string FormatCell(string? value) =>
        value is null
            ? "[dim]null[/]"
            : Markup.Escape(TerminalSafety.SanitizeCell(value));
}
