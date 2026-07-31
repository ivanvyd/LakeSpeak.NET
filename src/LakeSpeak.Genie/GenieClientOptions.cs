namespace LakeSpeak.Genie;

/// <summary>Configuration for <see cref="IGenieClient"/>.</summary>
public sealed class GenieClientOptions
{
    /// <summary>Workspace base URL, for example <c>https://example.azuredatabricks.net</c>.</summary>
    public Uri? Host { get; set; }

    /// <summary>Databricks CLI profile to broker a token through. Ignored when a token provider is supplied directly.</summary>
    public string? Profile { get; set; }

    /// <summary>
    /// How long to wait for a message to reach a terminal state.
    /// </summary>
    /// <remarks>
    /// Ten minutes because a Genie question that starts a cold warehouse genuinely can take
    /// minutes, and a client that gives up at 60 seconds reports a timeout for a query that was
    /// about to succeed. Cancellation, not a short timeout, is the answer to impatience.
    /// </remarks>
    public TimeSpan PollingTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>First polling interval. Databricks recommends 1–5 seconds.</summary>
    public TimeSpan InitialPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling for the backed-off polling interval.</summary>
    public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-request HTTP timeout. Separate from <see cref="PollingTimeout"/>, which bounds the whole wait.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>Page size for agent listing.</summary>
    public int PageSize { get; set; } = 100;

    internal void Validate()
    {
        if (Host is null)
        {
            throw new InvalidOperationException(
                "No Databricks host configured. Set GenieClientOptions.Host, the DATABRICKS_HOST " +
                "environment variable, or a profile in .databrickscfg.");
        }

        if (!Host.IsAbsoluteUri || Host.Scheme != Uri.UriSchemeHttps)
        {
            // A bearer token sent over http is a disclosed token, and the mistake is silent.
            throw new InvalidOperationException(
                $"Databricks host must be an absolute https URL. Got: {Host}");
        }

        if (InitialPollInterval > MaxPollInterval)
        {
            throw new InvalidOperationException(
                "InitialPollInterval must not exceed MaxPollInterval.");
        }
    }
}
