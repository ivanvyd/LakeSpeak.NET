using System.Text;
using LakeSpeak.Genie;
using LakeSpeak.Genie.Authentication;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace LakeSpeak.ContractTests;

/// <summary>
/// Drives <see cref="M2mTokenProvider"/> against a real HTTP server. The OAuth client-credentials
/// grant is a published wire contract, and a stub returning hard-coded JSON cannot catch a
/// redirect that loses the Basic auth header, or a body that mis-names <c>expires_in</c>.
/// </summary>
public sealed class M2mTokenProviderTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private const string ClientId = "test-client-id";
    private const string ClientSecret = "test-client-secret";
    private const string ValidToken = "eyJ.eyJ.signature";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private M2mTokenProvider CreateProvider()
    {
        // Let the provider own its HttpClient — a test-scoped client disposed by `using` is
        // disposed before the provider's first call to it, and the field-scoped lifetime below
        // would have to outlive the test class to be shareable, which it cannot.
        return new M2mTokenProvider(
            new Uri(_server.Url! + "/oidc/v1/token"),
            ClientId,
            ClientSecret,
            httpClient: null,
            _clock);
    }

    private void StubTokenResponse(int statusCode, string body)
    {
        _server.Given(Request.Create()
                .WithPath("/oidc/v1/token")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(statusCode).WithBody(body));
    }

    [Fact]
    public async Task First_call_posts_client_credentials_with_basic_auth()
    {
        // Arrange
        using var provider = CreateProvider();
        StubTokenResponse(200, $$"""
            {
              "access_token": "{{ValidToken}}",
              "token_type": "Bearer",
              "expires_in": 3600
            }
            """);

        // Act
        var token = await provider.GetTokenAsync(Ct);

        // Assert — the token came back, the server saw the right request, and the bearer is
        // exactly what the response said it was.
        token.ShouldBe(ValidToken);

        var requests = _server.LogEntries
            .Where(l => l.RequestMessage is { AbsolutePath: "/oidc/v1/token" })
            .ToList();
        requests.ShouldHaveSingleItem();

        var request = requests[0].RequestMessage;
        Assert.NotNull(request);
        request.Method.ShouldBe("POST");

        // The header is captured by WireMock; asserting the parsed value keeps the test honest
        // about what `Basic base64(client_id:client_secret)` decodes to. WireMock's headers
        // dictionary is case-sensitive, but HTTP header names are not, so the lookup has to
        // enumerate rather than index by the literal name.
        var authHeader = request.Headers!
            .Single(kv => string.Equals(kv.Key, "Authorization", StringComparison.OrdinalIgnoreCase));
        var raw = authHeader.Value.Single();
        raw.ShouldStartWith("Basic ");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw["Basic ".Length..]));
        decoded.ShouldBe($"{ClientId}:{ClientSecret}");

        var body = request.Body;
        Assert.NotNull(body);
        body.ShouldContain("grant_type=client_credentials");
        body.ShouldContain("scope=all-apis");
        // The client_secret must never reach the form body — putting it in both the header and
        // the body doubles the disclosure surface for the same thing.
        body.ShouldNotContain(ClientSecret);
    }

    [Fact]
    public async Task Subsequent_calls_reuse_the_cached_token()
    {
        // Arrange
        using var provider = CreateProvider();
        StubTokenResponse(200, $$"""
            { "access_token": "{{ValidToken}}", "token_type": "Bearer", "expires_in": 3600 }
            """);

        // Act
        var first = await provider.GetTokenAsync(Ct);
        var second = await provider.GetTokenAsync(Ct);
        var third = await provider.GetTokenAsync(Ct);

        // Assert — same token, and the endpoint was hit exactly once.
        first.ShouldBe(ValidToken);
        second.ShouldBe(ValidToken);
        third.ShouldBe(ValidToken);

        var calls = _server.LogEntries.Count(l => l.RequestMessage is { AbsolutePath: "/oidc/v1/token" });
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task Expired_token_is_refreshed_proactively()
    {
        // Arrange
        using var provider = CreateProvider();
        StubTokenResponse(200, $$"""
            { "access_token": "{{ValidToken}}", "token_type": "Bearer", "expires_in": 60 }
            """);

        // Act — first call fetches; advance the fake clock past the grace window; second call
        // must hit the endpoint again.
        await provider.GetTokenAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(120));

        // The second response carries a different token, so the assertion is on a fresh fetch
        // rather than on the cached value being returned.
        _server.Reset();
        _server.Given(Request.Create().WithPath("/oidc/v1/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""
                { "access_token": "second-token", "token_type": "Bearer", "expires_in": 3600 }
                """));

        var refreshed = await provider.GetTokenAsync(Ct);

        // Assert
        refreshed.ShouldBe("second-token");
        _server.LogEntries.Count(l => l.RequestMessage is { AbsolutePath: "/oidc/v1/token" })
            .ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_first_callers_share_a_single_fetch()
    {
        // Arrange — the provider is documented as safe under concurrent first-call; the
        // semaphore inside is what enforces it. A stub that takes the first hit and answers
        // 200s is enough to observe the count.
        using var provider = CreateProvider();
        StubTokenResponse(200, $$"""
            { "access_token": "{{ValidToken}}", "token_type": "Bearer", "expires_in": 3600 }
            """);

        // Act — 16 callers race for the first token. With a working gate exactly one fetch
        // happens; without it, 16 do.
        var tokens = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => provider.GetTokenAsync(Ct).AsTask()));

        // Assert
        tokens.ShouldAllBe(t => t == ValidToken);
        _server.LogEntries.Count(l => l.RequestMessage is { AbsolutePath: "/oidc/v1/token" })
            .ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_client_maps_to_authentication_failure()
    {
        // Arrange
        using var provider = CreateProvider();
        StubTokenResponse(401, """
            {
              "error": "invalid_client",
              "error_description": "Client authentication failed (e.g., unknown client, no client authentication included, or unsupported authentication method)."
            }
            """);

        // Act
        var act = () => provider.GetTokenAsync(Ct).AsTask();

        // Assert — Authentication kind, not Authorization; the credential itself is invalid, so
        // no Databricks call could succeed with it. The oauth_error code surfaces in the
        // message so the operator points at the right config page.
        var ex = await Should.ThrowAsync<GenieException>(act);
        ex.Kind.ShouldBe(GenieFailureKind.Authentication);
        ex.StatusCode.ShouldBe(401);
        ex.Message.ShouldContain("invalid_client");

        // The error description may include a wrapped client identifier on some Databricks
        // deployments; either way, the client_secret is the one credential that must never
        // reach the message.
        ex.Message.ShouldNotContain(ClientSecret);
    }

    [Fact]
    public async Task Non_json_error_body_is_treated_as_a_string()
    {
        // Arrange — a proxy or a captive portal can return HTML on the way to the token
        // endpoint. The provider must not throw a JsonException, which the caller would not
        // recognise as an auth problem.
        using var provider = CreateProvider();
        StubTokenResponse(502, "<html><body>Bad gateway</body></html>");

        // Act
        var act = () => provider.GetTokenAsync(Ct).AsTask();

        // Assert
        var ex = await Should.ThrowAsync<GenieException>(act);
        ex.Kind.ShouldBe(GenieFailureKind.Authentication);
        ex.StatusCode.ShouldBe(502);
    }

    [Fact]
    public async Task Missing_access_token_in_response_is_rejected()
    {
        // Arrange — the contract is that a 200 has a usable token. A 200 with no token is a
        // server bug; the provider should surface it as Authentication, not as a later 401
        // that reads like the caller's fault.
        using var provider = CreateProvider();
        StubTokenResponse(200, """
            { "token_type": "Bearer", "expires_in": 3600 }
            """);

        // Act
        var act = () => provider.GetTokenAsync(Ct).AsTask();

        // Assert
        var ex = await Should.ThrowAsync<GenieException>(act);
        ex.Kind.ShouldBe(GenieFailureKind.Authentication);
        ex.Message.ShouldContain("no access token");
    }

    [Fact]
    public void Constructor_rejects_empty_client_id()
    {
        // Arrange
        using var http = new HttpClient();

        // Act
        var act = () => new M2mTokenProvider(
            new Uri("https://example.invalid/oidc/v1/token"),
            clientId: "",
            ClientSecret,
            http);

        // Assert
        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void Constructor_rejects_empty_client_secret()
    {
        // Arrange
        using var http = new HttpClient();

        // Act
        var act = () => new M2mTokenProvider(
            new Uri("https://example.invalid/oidc/v1/token"),
            ClientId,
            clientSecret: "",
            http);

        // Assert
        Should.Throw<ArgumentException>(act);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }
}
