using System.Net;
using System.Text;
using System.Text.Json;
using LakeSpeak.Rendering;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic.FileIO;

namespace LakeSpeak.Genie.Tests;

public class GeneratedInputTests
{
    private static readonly Uri Host = new("https://example.azuredatabricks.net");

    public static TheoryData<string> MalformedResponses
    {
        get
        {
            var cases = new TheoryData<string>
            {
                string.Empty, "null", "[]", "true", "42", "\"text\"", "{}",
                "{\"space_id\":[]}", "{\"space_id\":{}}", "{\"space_id\":1}",
                "{\"space_id\":\"agent\",\"title\":[]}",
                "{\"space_id\":\"agent\",\"description\":{}}",
                new string('[', 80) + "0" + new string(']', 80),
            };
            const string valid = "{\"space_id\":\"agent\",\"title\":\"response-body-marker\"}";
            for (var length = 1; length < valid.Length; length++)
            {
                cases.Add(valid[..length]);
            }

            cases.Add(valid + " trailing-data");
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(MalformedResponses))]
    public async Task Malformed_response_corpus_stays_inside_the_typed_failure_boundary(string body)
    {
        using var handler = new ResponseHandler(body);
        using var http = new HttpClient(handler) { BaseAddress = Host };
        var client = new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = Host,
        }));

        var error = await Should.ThrowAsync<GenieException>(() =>
            client.GetAgentAsync("agent", TestContext.Current.CancellationToken));

        error.Kind.ShouldBe(GenieFailureKind.MalformedResponse);
        error.IsRetryable.ShouldBeFalse();
        error.Message.ShouldNotContain("response-body-marker");
        error.ToString().ShouldNotContain("response-body-marker");
        handler.RequestCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("東京 🙂")]
    [InlineData("comma, quote\" and newline\n")]
    public async Task Valid_response_fields_survive_unknown_nested_fields(string title)
    {
        var body = JsonSerializer.Serialize(new
        {
            space_id = "agent",
            title,
            future = new { nested = new { added = true } },
        });
        using var handler = new ResponseHandler(body);
        using var http = new HttpClient(handler) { BaseAddress = Host };
        var client = new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = Host,
        }));

        var agent = await client.GetAgentAsync("agent", TestContext.Current.CancellationToken);

        agent.Title.ShouldBe(title);
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public void Terminal_sanitization_is_idempotent_across_all_utf16_code_units()
    {
        var input = new string(Enumerable.Range(char.MinValue, char.MaxValue + 1)
            .Select(value => (char)value).ToArray());

        var clean = TerminalSafety.Sanitize(input);
        var cell = TerminalSafety.SanitizeCell(input);

        clean.Length.ShouldBe(input.Length);
        for (var index = 0; index < input.Length; index++)
        {
            if (!char.IsControl(input[index]) || input[index] is '\n' or '\r' or '\t')
            {
                clean[index].ShouldBe(input[index]);
            }
            else
            {
                char.IsControl(clean[index]).ShouldBeFalse();
            }
        }

        TerminalSafety.Sanitize(clean).ShouldBe(clean);
        TerminalSafety.SanitizeCell(cell).ShouldBe(cell);
        cell.ShouldNotContain('\r');
        cell.ShouldNotContain('\n');
    }

    [Fact]
    public void Generated_csv_fields_round_trip_with_formula_guards()
    {
        string[] fragments = [string.Empty, ",", "\"", "\r", "\n", "\r\n", "'", "東京", "🙂"];
        string[] prefixes = [string.Empty, "=", "+", "-", "@", " \t=", "\r+", "\t@"];
        foreach (var first in fragments)
        {
            foreach (var second in fragments)
            {
                foreach (var prefix in prefixes)
                {
                    var value = $"{prefix}value:{first}{second}";
                    var encoded = CsvWriter.EscapeField(value);
                    var decoded = encoded.StartsWith('"')
                        ? encoded[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
                        : encoded;
                    decoded.ShouldBe(prefix.Length == 0 ? value : "'" + value);
                    using var reader = new StringReader(encoded + ",sentinel");
                    using var parser = new TextFieldParser(reader)
                    {
                        HasFieldsEnclosedInQuotes = true,
                        TrimWhiteSpace = false,
                    };
                    parser.SetDelimiters(",");

                    var fields = parser.ReadFields();

                    fields.ShouldNotBeNull();
                    fields.Length.ShouldBe(2);
                    fields[1].ShouldBe("sentinel");
                    parser.EndOfData.ShouldBeTrue();
                }
            }
        }
    }

    private sealed class ResponseHandler(string body) : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsolutePath.ShouldBe("/api/2.0/genie/spaces/agent");
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
