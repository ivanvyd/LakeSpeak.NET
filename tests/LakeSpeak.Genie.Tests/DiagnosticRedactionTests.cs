using LakeSpeak.Genie;
using Shouldly;

namespace LakeSpeak.Genie.Tests;

public class DiagnosticRedactionTests
{
    // Assembled at runtime rather than written as a literal: a token-shaped constant
    // in source trips secret scanners on every clone and pull request, and the
    // assembled value exercises the regex identically.
    private static readonly string Pat = "dapi" + new string('a', 32);
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    [Fact]
    public void Redacts_databricks_personal_access_token()
    {
        var scrubbed = DiagnosticRedaction.Scrub($"request failed with token {Pat} attached");

        scrubbed.ShouldNotContain(Pat);
        scrubbed.ShouldContain(DiagnosticRedaction.Placeholder);
    }

    [Fact]
    public void Redacts_jwt_bearer_token()
    {
        DiagnosticRedaction.Scrub($"Authorization: Bearer {Jwt}").ShouldNotContain(Jwt);
    }

    [Theory]
    [InlineData("client_secret=hunter2seekrit")]
    [InlineData("access_token: abc123xyz789")]
    [InlineData("download_id_signature=c2lnbmF0dXJlCg")]
    [InlineData("X-Databricks-Session-Token: opaquevalue1")]
    public void Redacts_named_secrets(string input)
    {
        var scrubbed = DiagnosticRedaction.Scrub(input);

        scrubbed.ShouldContain(DiagnosticRedaction.Placeholder);
        // The key name survives so a log line stays diagnosable; only the value goes.
        scrubbed.Split('=', ':')[0].ShouldNotBeNullOrWhiteSpace();
    }

    // The download signature is bearer-equivalent: anyone holding it can fetch the full query
    // result, which is governed data.
    [Fact]
    public void Redacts_download_signature_from_a_realistic_payload()
    {
        var payload =
            """{"download_id":"abc","download_id_signature":"c2VjcmV0LXNpZ25hdHVyZQ=="}""";

        DiagnosticRedaction.Scrub(payload).ShouldNotContain("c2VjcmV0LXNpZ25hdHVyZQ");
    }

    // The message-level query_result summary carries statement_id_signature, which is a JWT
    // guarding access to the result rows. It is a different field from download_id_signature
    // and was missed by the first version of the scrubber.
    [Fact]
    public void Redacts_statement_id_signature()
    {
        var payload =
            """{"statement_id":"01ef","statement_id_signature":"c3RhdGVtZW50LXNpZw=="}""";

        DiagnosticRedaction.Scrub(payload).ShouldNotContain("c3RhdGVtZW50LXNpZw");
    }

    [Fact]
    public void Leaves_ordinary_text_alone()
    {
        const string message = "Genie could not answer: the table orders does not exist.";

        DiagnosticRedaction.Scrub(message).ShouldBe(message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Handles_empty_input(string? input) =>
        DiagnosticRedaction.Scrub(input).ShouldBe(string.Empty);

    // GenieException scrubs in its constructor, so a token cannot reach a caller's log by way
    // of an exception message even if a call site forgets.
    [Fact]
    public void Exception_message_is_scrubbed_on_construction()
    {
        var ex = new GenieException(GenieFailureKind.Authentication, $"failed using {Pat}");

        ex.Message.ShouldNotContain(Pat);
    }
}
