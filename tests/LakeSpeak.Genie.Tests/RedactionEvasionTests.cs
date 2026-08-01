namespace LakeSpeak.Genie.Tests;

/// <summary>
/// Attempts to defeat the scrubber, rather than confirming it works on values shaped to suit it.
/// </summary>
/// <remarks>
/// The original tests all used single-token values with no embedded space, which is exactly the
/// shape the pattern handled. A scheme-prefixed credential — the single most common way an
/// Authorization header is written down — slipped straight through.
/// </remarks>
public class RedactionEvasionTests
{
    // Deliberately neither JWT-shaped nor dapi-shaped, so only the named-key rule can catch it.
    private const string Opaque = "sometoken_not_jwt_or_dapi_shaped_1234567890";

    [Theory]
    [InlineData("Authorization: Bearer ")]
    [InlineData("authorization: bearer ")]
    [InlineData("Authorization:Bearer ")]
    [InlineData("Authorization: Basic ")]
    [InlineData("Authorization = Bearer ")]
    public void A_scheme_prefixed_credential_does_not_survive(string prefix)
    {
        // Arrange — redacting only the word "Bearer" and leaving the credential is the failure
        // this whole class exists to catch.
        var diagnostic = prefix + Opaque;

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(diagnostic);

        // Assert
        scrubbed.ShouldNotContain(Opaque);
    }

    [Fact]
    public void A_bearer_token_on_its_own_does_not_survive()
    {
        // Arrange
        var diagnostic = $"Bearer {Opaque}";

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(diagnostic);

        // Assert
        scrubbed.ShouldNotContain(Opaque);
    }

    [Theory]
    [InlineData("access_token: Bearer ")]
    [InlineData("client_secret = ")]
    [InlineData("download_id_signature: ")]
    [InlineData("statement_id_signature: ")]
    public void Named_secrets_do_not_survive_regardless_of_separator(string prefix)
    {
        // Arrange
        var diagnostic = prefix + Opaque;

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(diagnostic);

        // Assert
        scrubbed.ShouldNotContain(Opaque);
    }

    [Fact]
    public void Redacts_the_credential_in_a_header_dump_without_eating_everything()
    {
        // Arrange — a realistic shape, where the credential is followed by more headers.
        var diagnostic = $"Authorization: Bearer {Opaque}\nContent-Type: application/json";

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(diagnostic);

        // Assert
        scrubbed.ShouldNotContain(Opaque);
        scrubbed.ShouldContain("Content-Type: application/json");
    }

    [Fact]
    public void Ordinary_prose_containing_the_word_bearer_is_left_readable()
    {
        // Arrange — over-scrubbing prose would make diagnostics useless.
        const string prose = "The bearer of this message is not authorized.";

        // Act
        var scrubbed = DiagnosticRedaction.Scrub(prose);

        // Assert
        scrubbed.ShouldContain("The bearer");
    }
}
