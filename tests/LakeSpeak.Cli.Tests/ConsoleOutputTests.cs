using LakeSpeak.Cli.Console;
using LakeSpeak.Genie;
using LakeSpeak.Rendering;
using Spectre.Console;
using Spectre.Console.Testing;

namespace LakeSpeak.Cli.Tests;

/// <summary>
/// Output discipline, asserted against a captured console rather than by eye.
/// </summary>
/// <remarks>
/// Two properties matter and neither is visible in normal use: results must reach stdout while
/// diagnostics reach stderr, and nothing decorative may reach a machine-readable stream. A
/// spinner or an escape sequence in a JSON pipe is a parse error in somebody's script.
/// </remarks>
public class ConsoleOutputTests
{
    private static GenieQueryResult Result(params string?[] values) =>
        new(
            [new GenieColumn("value", "STRING", "STRING")],
            values.Select(v => (IReadOnlyList<string?>)[v]).ToList(),
            IsTruncated: false,
            TotalRowCount: values.Length);

    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Jsonl)]
    [InlineData(OutputFormat.Csv)]
    public void Machine_formats_are_machine_readable(OutputFormat format)
    {
        // Arrange — the property that keeps `... --format json | jq` working.

        // Act
        var isMachine = format.IsMachineReadable();

        // Assert
        isMachine.ShouldBeTrue();
    }

    [Theory]
    [InlineData(OutputFormat.Text)]
    [InlineData(OutputFormat.Table)]
    [InlineData(OutputFormat.Markdown)]
    public void Human_formats_are_not_machine_readable(OutputFormat format)
    {
        // Arrange — Markdown is for people to read, even though it is plain text.

        // Act
        var isMachine = format.IsMachineReadable();

        // Assert
        isMachine.ShouldBeFalse();
    }

    [Fact]
    public void Results_are_written_to_the_supplied_writer_not_the_process_stdout()
    {
        // Arrange — the seam that makes CLI output assertable at all.
        var captured = new StringWriter();
        var output = new ConsoleOutput(OutputFormat.Json, quiet: false, stdout: captured);

        // Act
        output.WriteResultLine("""{"ok":true}""");

        // Assert
        captured.ToString().Trim().ShouldBe("""{"ok":true}""");
    }

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(200)]
    public void A_table_renders_at_any_terminal_width_without_throwing(int width)
    {
        // Arrange — a narrow terminal is the case that breaks naive column maths.
        var console = new TestConsole().Width(width);
        var renderer = new TerminalRenderer(console);

        // Act
        renderer.WriteResult(Result("a value long enough to need wrapping at forty columns"));

        // Assert
        console.Output.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("€2.4M")]
    [InlineData("東京")]
    [InlineData("Ökonomie")]
    public void Non_ascii_survives_terminal_rendering(string value)
    {
        // Arrange
        var console = new TestConsole().Width(120);
        var renderer = new TerminalRenderer(console);

        // Act
        renderer.WriteResult(Result(value));

        // Assert
        console.Output.ShouldContain(value);
    }

    [Fact]
    public void A_null_cell_is_distinguishable_from_an_empty_one()
    {
        // Arrange — rendering both as blank loses information the reader cannot recover.
        var console = new TestConsole().Width(120);
        var renderer = new TerminalRenderer(console);

        // Act
        renderer.WriteResult(Result(null, string.Empty));

        // Assert
        console.Output.ShouldContain("null");
    }

    [Fact]
    public void A_crafted_cell_cannot_emit_escape_sequences_into_the_terminal()
    {
        // Arrange — Genie returns cells drawn from your tables; a value carrying ANSI could
        // clear the screen or draw something resembling this tool's own prompt.
        var console = new TestConsole().Width(120);
        var renderer = new TerminalRenderer(console);
        var hostile = "[2J[1;1Hlakespeak> ";

        // Act
        renderer.WriteResult(Result(hostile));

        // Assert
        console.Output.ShouldNotContain("[2J");
    }

    [Fact]
    public void Truncation_by_databricks_is_worded_differently_from_a_local_row_cap()
    {
        // Arrange — conflating them tells someone their export is incomplete when only the
        // screen was capped, or the reverse. Two rows, displayed cap of one, not truncated.
        var console = new TestConsole().Width(120);
        var renderer = new TerminalRenderer(console, maxRows: 1);

        // Act
        renderer.WriteResult(Result("first", "second"));

        // Assert
        console.Output.ShouldContain("Export to see all of them");
        console.Output.ShouldNotContain("truncated");
    }

    [Fact]
    public void Databricks_truncation_says_so_explicitly()
    {
        // Arrange
        var console = new TestConsole().Width(120);
        var renderer = new TerminalRenderer(console);
        var truncated = new GenieQueryResult(
            [new GenieColumn("value", "STRING", "STRING")],
            [(IReadOnlyList<string?>)["only row"]],
            IsTruncated: true,
            TotalRowCount: 5000);

        // Act
        renderer.WriteResult(truncated);

        // Assert
        console.Output.ShouldContain("truncated");
        console.Output.ShouldContain("5,000");
    }

    [Fact]
    public void Bound_parameters_are_shown_beneath_the_sql()
    {
        // Arrange — SQL with placeholders and no values shown is half an answer.
        var console = new TestConsole().Width(120);
        var renderer = new TerminalRenderer(console);

        // Act
        renderer.WriteSql(
            "SELECT * FROM t WHERE region = :region",
            [new GenieQueryParameter("region", "STRING", "Germany")]);

        // Assert
        console.Output.ShouldContain("region");
        console.Output.ShouldContain("Germany");
    }
}
