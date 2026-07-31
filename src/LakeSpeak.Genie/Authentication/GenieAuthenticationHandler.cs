using System.Net.Http.Headers;

namespace LakeSpeak.Genie.Authentication;

/// <summary>
/// Attaches the bearer token to outbound Databricks requests.
/// </summary>
/// <remarks>
/// The token is fetched per request rather than captured once, so a refresh by the provider takes
/// effect without rebuilding the client. It is attached here and nowhere else: no call site
/// handles a raw token, which is what keeps the credential out of URLs, logs and argument vectors
/// by construction rather than by review.
/// </remarks>
public sealed class GenieAuthenticationHandler(IGenieTokenProvider tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // A presigned URL carries its own authorization in the query string. Sending the
        // Databricks bearer token to blob storage would disclose it to a third party;
        // Databricks rejects such requests with HTTP 400, so the mistake is loud, but the
        // client must not make it in the first place.
        if (request.RequestUri is not null && IsPresigned(request.RequestUri))
        {
            request.Headers.Authorization = null;
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var token = await tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsPresigned(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        var query = uri.Query;
        return query.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase)
            || query.Contains("X-Amz-Credential", StringComparison.OrdinalIgnoreCase)
            || query.Contains("sig=", StringComparison.OrdinalIgnoreCase)
            || query.Contains("GoogleAccessId", StringComparison.OrdinalIgnoreCase);
    }
}
