using LakeSpeak.Genie;

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
    // The token here is deliberately neither JWT-shaped nor dapi-shaped, so the only thing that
    // can catch it is the named-key rule.
    private const string Opaque = "sometoken_not_jwt_or_dapi_shaped_1234567890";

    [Theory]
    [InlineData("Authorization: Bearer ")]
    [InlineData("authorization: bearer ")]
    [InlineData("Authorization:Bearer ")]
    [InlineData("Authorization: Basic ")]
    [InlineData("Authorization = Bearer ")]
    public void A_scheme_prefixed_credential_does_not_survive(string prefix)
    {
        var scrubbed = DiagnosticRedaction.Scrub(prefix + Opaque);

        // Redacting only the word "Bearer" and leaving the credential is the failure this
        // whole class exists to catch.
        scrubbed.ShouldNotContain(Opaque);
    }

    [Fact]
    public void A_bearer_token_on_its_own_does_not_survive()
    {
        DiagnosticRedaction.Scrub($"Bearer {Opaque}").ShouldNotContain(Opaque);
    }

    [Theory]
    [InlineData("access_token: Bearer ")]
    [InlineData("client_secret = ")]
    [InlineData("download_id_signature: ")]
    [InlineData("statement_id_signature: ")]
    public void Named_secrets_do_not_survive_regardless_of_separator(string prefix)
    {
        DiagnosticRedaction.Scrub(prefix + Opaque).ShouldNotContain(Opaque);
    }

    // Realistic shape: a header dump, where the credential is followed by more headers. The
    // scrubber must take the credential without eating the rest of the line's structure.
    [Fact]
    public void Redacts_the_credential_in_a_header_dump_without_eating_everything()
    {
        var scrubbed = DiagnosticRedaction.Scrub(
            $"Authorization: Bearer {Opaque}\nContent-Type: application/json");

        scrubbed.ShouldNotContain(Opaque);
        scrubbed.ShouldContain("Content-Type: application/json");
    }

    [Fact]
    public void Ordinary_prose_containing_the_word_bearer_is_left_readable()
    {
        const string prose = "The bearer of this message is not authorized.";

        // Over-scrubbing prose would make diagnostics useless; the value after the keyword is
        // taken, but the sentence must stay recognisable.
        DiagnosticRedaction.Scrub(prose).ShouldContain("The bearer");
    }
}
