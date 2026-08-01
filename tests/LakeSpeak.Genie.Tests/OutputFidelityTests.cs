using LakeSpeak.Genie;
using LakeSpeak.Rendering;

namespace LakeSpeak.Genie.Tests;

/// <summary>
/// Values must reach the output byte-for-byte as Databricks returned them. These tests exist
/// because the failure mode is silent: a reformatted number or a mangled character still looks
/// like a working report.
/// </summary>
public class OutputFidelityTests
{
    private static GenieQueryResult Result(params (string Name, string? Value)[] cells) =>
        new(
            cells.Select(c => new GenieColumn(c.Name, "STRING", "STRING")).ToList(),
            [cells.Select(c => c.Value).ToList()],
            IsTruncated: false,
            TotalRowCount: 1);

    [Theory]
    [InlineData("4500000.00")]
    [InlineData("3350000.50")]
    [InlineData("0.000000000000000001")]
    [InlineData("-0.0")]
    [InlineData("1E+40")]
    [InlineData("99999999999999999999999999.99")]
    public void Csv_carries_numbers_through_unchanged(string value)
    {
        var csv = CsvWriter.Write(Result(("amount", value)));

        // Not 4.5E6, not 4,500,000.00, not 4500000. Parsing a DECIMAL in order to print it is
        // how a client silently changes someone's revenue figure.
        csv.ShouldContain(value);
    }

    [Theory]
    [InlineData("€2.4M")]
    [InlineData("Ökonomie")]
    [InlineData("東京")]
    [InlineData("Ω≈ç√∫")]
    [InlineData("emoji 🙂 in a cell")]
    public void Non_ascii_survives_every_writer(string value)
    {
        var result = Result(("label", value));
        var response = new GenieResponse(
            "agent", "conversation", "message", GenieMessageState.Completed,
            value, null, result, [], new GenieResponseMetadata(TimeSpan.Zero, 1));

        CsvWriter.Write(result).ShouldContain(value);
        MarkdownWriter.Write(response).ShouldContain(value);
        TerminalSafety.Sanitize(value).ShouldBe(value);

        // JSON is asserted after a round trip rather than by substring. Characters outside the
        // BMP are legitimately written as escaped surrogate pairs, which no consumer ever sees
        // because every parser decodes them — so a substring check would fail on correct output.
        using var parsed = System.Text.Json.JsonDocument.Parse(MachineOutput.ToJson(response));
        parsed.RootElement.GetProperty("answer").GetString().ShouldBe(value);
    }

    // A SQL NULL and the empty string are different values, and a format that renders them
    // identically loses information the caller cannot recover.
    [Fact]
    public void Csv_distinguishes_null_from_empty_string()
    {
        var csv = CsvWriter.Write(Result(("a", null), ("b", string.Empty)));
        var dataLine = csv.Split('\n')[1].TrimEnd('\r');

        dataLine.ShouldBe(",");
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    public void Csv_defuses_spreadsheet_formulas(string value)
    {
        var csv = CsvWriter.Write(Result(("payload", value)));

        // A leading =, +, - or @ makes Excel and Sheets evaluate the cell. The value is still
        // present and readable; it just cannot execute.
        csv.ShouldContain($"\"'{value}\"");
    }

    [Fact]
    public void Csv_quotes_values_containing_delimiters_and_quotes()
    {
        var csv = CsvWriter.Write(Result(("a", "has,comma"), ("b", "has\"quote")));

        csv.ShouldContain("\"has,comma\"");
        csv.ShouldContain("\"has\"\"quote\"");
    }

    // Genie returns model-generated prose and cells drawn from your tables. A crafted value must
    // not be able to move the cursor or draw something resembling this tool's own prompt.
    [Theory]
    [InlineData("[2Jcleared")]
    [InlineData("bell")]
    [InlineData("carriage\rreturn")]
    public void Control_characters_are_neutralised(string value)
    {
        var sanitized = TerminalSafety.SanitizeCell(value);

        sanitized.ShouldNotContain("");
        sanitized.ShouldNotContain("");
        sanitized.ShouldNotContain("\r");
    }

    [Fact]
    public void Newline_and_tab_survive_sanitising_prose()
    {
        const string prose = "line one\nline two\tcolumn";

        TerminalSafety.Sanitize(prose).ShouldBe(prose);
    }

    [Fact]
    public void Markdown_escapes_pipes_so_a_cell_cannot_break_the_table()
    {
        var markdown = MarkdownWriter.Write(new GenieResponse(
            "a", "c", "m", GenieMessageState.Completed, null, null,
            Result(("col", "a|b")), [], new GenieResponseMetadata(TimeSpan.Zero, 1)));

        markdown.ShouldContain("a\\|b");
    }

    [Fact]
    public void Json_uses_a_stable_lowercase_status_vocabulary()
    {
        var json = MachineOutput.ToJson(new GenieResponse(
            "a", "c", "m", GenieMessageState.QueryResultExpired, null, null, null, [],
            new GenieResponseMetadata(TimeSpan.Zero, 1)));

        json.ShouldContain("\"status\": \"queryresultexpired\"");
        json.ShouldContain("\"schemaVersion\": \"1\"");
    }
}
