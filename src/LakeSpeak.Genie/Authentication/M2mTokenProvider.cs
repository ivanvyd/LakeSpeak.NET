using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LakeSpeak.Genie.Authentication;

/// <summary>
/// Acquires short-lived tokens via the Databricks OAuth machine-to-machine flow (client
/// credentials grant).
/// </summary>
/// <remarks>
/// <para>
/// This is the unattended path: a service principal's <c>client_id</c> + <c>client_secret</c>
/// exchange for an access token, refreshed proactively before it expires. A Question Pack on a
/// schedule, a CI smoke run, a service-hosted .NET consumer — any of these can call Genie
/// without holding a personal access token and without an interactive browser login.
/// </para>
/// <para>
/// Set <c>DATABRICKS_CLIENT_ID</c> and <c>DATABRICKS_CLIENT_SECRET</c> to opt in. If only one is
/// set, <see cref="LakeSpeak.Genie.ServiceCollectionExtensions.AddLakeSpeak"/> throws at start-up
/// rather than silently falling back to a different provider — the failure mode of a half-set
/// pair is the failure mode of a misconfigured secret, and a clear message beats a 401.
/// </para>
/// <para>
/// The token URL is derived from the configured workspace host as
/// <c>{host}/oidc/v1/token</c>, which is the documented endpoint for Azure, AWS and GCP
/// Databricks. The same client credentials grant is used across clouds; what differs is the
/// workspace host, which the host validation already enforces.
/// </para>
/// </remarks>
public sealed class M2mTokenProvider : IGenieTokenProvider, IDisposable
{
    public const string ClientIdVariable = "DATABRICKS_CLIENT_ID";
    public const string ClientSecretVariable = "DATABRICKS_CLIENT_SECRET";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // Refresh slightly early: a token that expires while in flight fails the request it was
    // fetched for, and the retry looks like an intermittent auth bug.
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromSeconds(60);

    private readonly Uri _tokenEndpoint;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTimeOffset _expiresAt;

    /// <summary>
    /// Constructs a provider that calls the token endpoint at <paramref name="tokenEndpoint"/>.
    /// Pass a null <paramref name="httpClient"/> to use a private <see cref="HttpClient"/> with a
    /// bounded timeout; pass one to share the client's own <see cref="HttpMessageInvoker"/>
    /// pipeline (useful in tests).
    /// </summary>
    public M2mTokenProvider(
        Uri tokenEndpoint,
        string clientId,
        string clientSecret,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentException.ThrowIfNullOrEmpty(clientSecret);

        _tokenEndpoint = tokenEndpoint;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _time = timeProvider ?? TimeProvider.System;

        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = RequestTimeout };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_token is not null && _time.GetUtcNow() < _expiresAt - ExpiryGrace)
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check: several requests can queue behind one refresh, and without this they
            // each fire their own token endpoint call.
            if (_token is not null && _time.GetUtcNow() < _expiresAt - ExpiryGrace)
            {
                return _token;
            }

            var (token, expiresIn) = await FetchAsync(cancellationToken).ConfigureAwait(false);
            _token = token;
            // Databricks returns `expires_in` in seconds. Floor at one second so a clock that
            // ticked past the issued-at between the response and now does not hand out an
            // already-expired token.
            var lifetime = TimeSpan.FromSeconds(Math.Max(1, expiresIn));
            _expiresAt = _time.GetUtcNow().Add(lifetime);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string Token, int ExpiresIn)> FetchAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint);

        // OAuth client credentials uses HTTP Basic with the client credentials in the
        // Authorization header. Sending them in the form body is also accepted; Basic is the
        // shape the Databricks docs show, and matching it removes a class of "works locally,
        // fails behind our proxy that strips form-encoded secrets" surprises.
        var basic = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "all-apis",
        });

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GenieException(
                GenieFailureKind.Authentication,
                $"Could not reach the Databricks token endpoint at {_tokenEndpoint}. " +
                "Check the workspace host and network access.",
                innerException: ex);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GenieException(
                GenieFailureKind.Authentication,
                $"The Databricks token endpoint at {_tokenEndpoint} did not respond within " +
                $"{RequestTimeout.TotalSeconds:F0}s.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return ParseSuccess(body);
            }

            // `invalid_client` is the only failure mode that is the caller's fault and stays
            // their fault — wrong id, wrong secret, or the principal was deleted. The OAuth
            // spec is precise here, and surfacing the exact code lets the maintainer point at
            // a configuration page rather than a 401.
            var (error, description) = ParseError(body);
            throw new GenieException(
                GenieFailureKind.Authentication,
                $"Databricks rejected the service-principal credentials (HTTP {(int)response.StatusCode}, " +
                $"oauth_error={error ?? "(none)"}). " +
                $"{DiagnosticRedaction.Scrub(description ?? "no description returned")}. " +
                "Verify DATABRICKS_CLIENT_ID and DATABRICKS_CLIENT_SECRET, and that the service " +
                "principal exists in the workspace and has access to the Genie API scope.",
                statusCode: (int)response.StatusCode,
                errorCode: error);
        }
    }

    private static (string Token, int ExpiresIn) ParseSuccess(string body)
    {
        TokenResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TokenResponse>(body);
        }
        catch (JsonException)
        {
            throw new GenieException(
                GenieFailureKind.Authentication,
                "Could not parse the Databricks token response as JSON. " +
                "The token endpoint may be behind a proxy that returned HTML.");
        }

        if (string.IsNullOrEmpty(parsed?.AccessToken))
        {
            throw new GenieException(
                GenieFailureKind.Authentication,
                "The Databricks token endpoint returned no access token.");
        }

        return (parsed.AccessToken, parsed.ExpiresIn);
    }

    private static (string? Error, string? Description) ParseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ErrorResponse>(body);
            return (parsed?.Error, parsed?.ErrorDescription);
        }
        catch (JsonException)
        {
            return (null, Truncate(body));
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s.Trim() : s[..500].Trim() + "…";

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }

    private sealed class ErrorResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }
}
