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

    private static GenieResponse Response(string? text, GenieQueryResult? result) =>
        new("agent", "conversation", "message", GenieMessageState.Completed,
            text, null, result, [], new GenieResponseMetadata(TimeSpan.Zero, 1));

    [Theory]
    [InlineData("4500000.00")]
    [InlineData("3350000.50")]
    [InlineData("0.000000000000000001")]
    [InlineData("-0.0")]
    [InlineData("1E+40")]
    [InlineData("99999999999999999999999999.99")]
    public void Csv_carries_numbers_through_unchanged(string value)
    {
        // Arrange
        var result = Result(("amount", value));

        // Act
        var csv = CsvWriter.Write(result);

        // Assert — not 4.5E6, not 4,500,000.00, not 4500000. Parsing a DECIMAL in order to
        // print it is how a client silently changes someone's revenue figure.
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
        // Arrange
        var result = Result(("label", value));
        var response = Response(value, result);

        // Act
        var csv = CsvWriter.Write(result);
        var markdown = MarkdownWriter.Write(response);
        var sanitized = TerminalSafety.Sanitize(value);
        using var json = System.Text.Json.JsonDocument.Parse(MachineOutput.ToJson(response));

        // Assert — JSON is checked after a round trip rather than by substring: characters
        // outside the BMP are legitimately written as escaped surrogate pairs, which every
        // parser decodes, so a substring check would fail on correct output.
        csv.ShouldContain(value);
        markdown.ShouldContain(value);
        sanitized.ShouldBe(value);
        json.RootElement.GetProperty("answer").GetString().ShouldBe(value);
    }

    [Fact]
    public void Csv_distinguishes_null_from_empty_string()
    {
        // Arrange — a SQL NULL and the empty string are different values, and a format that
        // renders them identically loses information the caller cannot recover.
        var result = Result(("a", null), ("b", string.Empty));

        // Act
        var csv = CsvWriter.Write(result);

        // Assert
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
        // Arrange
        var result = Result(("payload", value));

        // Act
        var csv = CsvWriter.Write(result);

        // Assert — the value is still present and readable; it just cannot execute.
        csv.ShouldContain($"\"'{value}\"");
    }

    [Theory]
    [InlineData("\t=cmd|' /c calc'!A1")]
    [InlineData("\r=1+1")]
    public void Csv_defuses_a_formula_hidden_behind_leading_whitespace(string value)
    {
        // Arrange — OWASP documents tab and carriage return as accepted prefixes before the
        // formula marker; a guard that only checks value[0] misses both.
        var result = Result(("payload", value));

        // Act
        var csv = CsvWriter.Write(result);

        // Assert
        csv.ShouldContain("'");
    }

    [Fact]
    public void Csv_quotes_values_containing_delimiters_and_quotes()
    {
        // Arrange
        var result = Result(("a", "has,comma"), ("b", "has\"quote"));

        // Act
        var csv = CsvWriter.Write(result);

        // Assert
        csv.ShouldContain("\"has,comma\"");
        csv.ShouldContain("\"has\"\"quote\"");
    }

    [Theory]
    [InlineData("[2Jcleared")]
    [InlineData("bell")]
    [InlineData("carriage\rreturn")]
    public void Control_characters_are_neutralised(string value)
    {
        // Arrange — Genie returns model-generated prose and cells drawn from your tables. A
        // crafted value must not be able to move the cursor or draw this tool's own prompt.

        // Act
        var sanitized = TerminalSafety.SanitizeCell(value);

        // Assert
        sanitized.ShouldNotContain("");
        sanitized.ShouldNotContain("");
        sanitized.ShouldNotContain("\r");
    }

    [Fact]
    public void Newline_and_tab_survive_sanitising_prose()
    {
        // Arrange
        const string prose = "line one\nline two\tcolumn";

        // Act
        var sanitized = TerminalSafety.Sanitize(prose);

        // Assert
        sanitized.ShouldBe(prose);
    }

    [Fact]
    public void Markdown_escapes_pipes_so_a_cell_cannot_break_the_table()
    {
        // Arrange
        var response = Response(null, Result(("col", "a|b")));

        // Act
        var markdown = MarkdownWriter.Write(response);

        // Assert
        markdown.ShouldContain("a\\|b");
    }

    [Fact]
    public void Json_uses_a_stable_lowercase_status_vocabulary()
    {
        // Arrange
        var response = new GenieResponse(
            "a", "c", "m", GenieMessageState.QueryResultExpired, null, null, null, [],
            new GenieResponseMetadata(TimeSpan.Zero, 1));

        // Act
        var json = MachineOutput.ToJson(response);

        // Assert
        json.ShouldContain("\"status\": \"queryresultexpired\"");
        json.ShouldContain("\"schemaVersion\": \"1\"");
    }
}
