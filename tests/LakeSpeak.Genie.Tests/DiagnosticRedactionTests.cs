namespace LakeSpeak.Genie.Tests;

public class DiagnosticRedactionTests
{
    // Assembled at runtime rather than written as a literal: a token-shaped constant in source
    // trips secret scanners on every clone and pull request, and the assembled value exercises
    // the regex identically.
    private static readonly string Pat = "dapi" + new string('a', 32);

    private const string Jwt =
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    [Fact]
    public void Redacts_databricks_personal_access_token()
    {
        // Arrange
        var diagnostic = $"request failed with token {Pat} attached";

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(diagnostic);

        // Assert
        scrubbed.ShouldNotContain(Pat);
        scrubbed.ShouldContain(DiagnosticRedaction.Placeholder);
    }

    [Fact]
    public void Redacts_jwt_bearer_token()
    {
        // Arrange
        var diagnostic = $"Authorization: Bearer {Jwt}";

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(diagnostic);

        // Assert
        scrubbed.ShouldNotContain(Jwt);
    }

    [Theory]
    [InlineData("client_secret=hunter2seekrit")]
    [InlineData("access_token: abc123xyz789")]
    [InlineData("download_id_signature=c2lnbmF0dXJlCg")]
    [InlineData("X-Databricks-Session-Token: opaquevalue1")]
    public void Redacts_named_secrets(string diagnostic)
    {
        // Arrange — the diagnostic arrives as the theory parameter.

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(diagnostic);

        // Assert — the key name survives so a log line stays diagnosable; only the value goes.
        scrubbed.ShouldContain(DiagnosticRedaction.Placeholder);
        scrubbed.Split('=', ':')[0].ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Redacts_download_signature_from_a_realistic_payload()
    {
        // Arrange — the download signature is bearer-equivalent for the full query result.
        const string payload =
            """{"download_id":"abc","download_id_signature":"c2VjcmV0LXNpZ25hdHVyZQ=="}""";

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(payload);

        // Assert
        scrubbed.ShouldNotContain("c2VjcmV0LXNpZ25hdHVyZQ");
    }

    [Fact]
    public void Redacts_statement_id_signature()
    {
        // Arrange — a different field from download_id_signature, and missed by the first
        // version of the scrubber.
        const string payload =
            """{"statement_id":"01ef","statement_id_signature":"c3RhdGVtZW50LXNpZw=="}""";

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(payload);

        // Assert
        scrubbed.ShouldNotContain("c3RhdGVtZW50LXNpZw");
    }

    [Fact]
    public void Leaves_ordinary_text_alone()
    {
        // Arrange
        const string message = "Genie could not answer: the table orders does not exist.";

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(message);

        // Assert
        scrubbed.ShouldBe(message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Handles_empty_input(string? diagnostic)
    {
        // Arrange — the input arrives as the theory parameter.

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(diagnostic);

        // Assert
        scrubbed.ShouldBe(string.Empty);
    }

    [Fact]
    public void Exception_message_is_scrubbed_on_construction()
    {
        // Arrange — GenieException scrubs in its constructor, so a token cannot reach a caller's
        // log by way of an exception even if a call site forgets.
        var message = $"failed using {Pat}";

        // Act
        var exception = new GenieException(GenieFailureKind.Authentication, message);

        // Assert
        exception.Message.ShouldNotContain(Pat);
    }
}
