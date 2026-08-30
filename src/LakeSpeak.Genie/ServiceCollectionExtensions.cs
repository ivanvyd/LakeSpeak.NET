using System.Net.Http;
using LakeSpeak.Genie.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace LakeSpeak.Genie;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGenieClient"/> with a typed <see cref="HttpClient"/>, bearer-token
    /// authentication and the standard resilience pipeline.
    /// </summary>
    /// <remarks>
    /// If no <see cref="IGenieTokenProvider"/> has been registered, the Databricks CLI broker is
    /// used. Register your own before calling this to override that.
    /// </remarks>
    public static IServiceCollection AddLakeSpeak(
        this IServiceCollection services,
        Action<GenieClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<GenieClientOptions>()
            .Configure(options =>
            {
                configure?.Invoke(options);

                // Fill the host from the environment or .databrickscfg only if the caller did
                // not set one, so an explicit value always wins.
                options.Host = DatabricksProfiles.ResolveHost(options.Host, options.Profile);
            })
            .Validate(
                options => options.Host is not null,
                "No Databricks host configured. Set GenieClientOptions.Host, DATABRICKS_HOST, or a profile in .databrickscfg.");

        services.TryAddSingleton<IGenieTokenProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GenieClientOptions>>().Value;

            // PAT wins when set. It is the only path that works without configuration and is
            // documented as the local-debugging option, so the rule of least surprise says it
            // stays the first check.
            if (Environment.GetEnvironmentVariable(EnvironmentTokenProvider.TokenVariable) is { Length: > 0 })
            {
                return new EnvironmentTokenProvider();
            }

            // OAuth M2M is the unattended path. A service principal's client_id + client_secret
            // rotate via the Databricks token endpoint, so a Question Pack on a schedule does
            // not have to hold a long-lived personal credential. The two variables have to be
            // set together: a half-set pair is a misconfiguration, not a fallback.
            var clientId = Environment.GetEnvironmentVariable(M2mTokenProvider.ClientIdVariable);
            var clientSecret = Environment.GetEnvironmentVariable(M2mTokenProvider.ClientSecretVariable);
            if (!string.IsNullOrEmpty(clientId) || !string.IsNullOrEmpty(clientSecret))
            {
                if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                {
                    throw new InvalidOperationException(
                        $"{M2mTokenProvider.ClientIdVariable} and {M2mTokenProvider.ClientSecretVariable} " +
                        "must be set together. Setting only one causes an OAuth failure that reads as a " +
                        "credential problem; unset both to use the Databricks CLI broker, or set both to " +
                        "use the OAuth M2M flow.");
                }

                if (options.Host is null)
                {
                    // M2M cannot derive a token URL without a workspace host. Throw at start-up so
                    // a misconfigured environment fails loudly, not on the first Genie call.
                    throw new InvalidOperationException(
                        "OAuth M2M requires a Databricks host. Set GenieClientOptions.Host, DATABRICKS_HOST, " +
                        "or a profile in .databrickscfg.");
                }

                var tokenEndpoint = new Uri(options.Host, "/oidc/v1/token");
                return new M2mTokenProvider(tokenEndpoint, clientId, clientSecret);
            }

            return new DatabricksCliTokenProvider(options.Profile);
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<GenieAuthenticationHandler>();

        services.AddHttpClient<IGenieClient, GenieClient>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<GenieClientOptions>>().Value;
                http.BaseAddress = options.Host;
                http.Timeout = options.RequestTimeout;
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent.Value);
            })
            .AddHttpMessageHandler<GenieAuthenticationHandler>()
            .AddStandardResilienceHandler(resilience =>
            {
                resilience.Retry.MaxRetryAttempts = 3;
                resilience.Retry.UseJitter = true;

                // The standard handler retries POST by default. That is wrong for this API:
                // start-conversation and create-message are not idempotent, so a retry after a
                // transient 5xx asks Genie the SAME question a second time — running the SQL
                // warehouse twice, billing twice, and leaving an orphaned conversation whose
                // id the client never returns. Reads stay retryable, which is where retrying
                // actually helps: the polling loop is almost all of the request volume.
                //
                // On net9.0+ the helper DisableForUnsafeHttpMethods excludes POST/PATCH/PUT/
                // DELETE/CONNECT in one call. The net8 line of Microsoft.Extensions.Http.Resilience
                // (8.10.0) does not expose that helper — it landed in 9.x — so we install the same
                // exclusion by hand at the predicate level, matching the net9+ behaviour on both
                // TFMs. The exception branch of the outcome has no request method to inspect, so it
                // falls through to the transient check; the net9+ line is in the same place.
                resilience.Retry.ShouldHandle = args =>
                {
                    if (!HttpClientResiliencePredicates.IsTransient(args.Outcome))
                    {
                        return new ValueTask<bool>(false);
                    }

                    var method = args.Outcome.Result?.RequestMessage?.Method;
                    var isUnsafe = method == HttpMethod.Post
                        || method == HttpMethod.Put
                        || method == HttpMethod.Patch
                        || method == HttpMethod.Delete
                        || method == HttpMethod.Connect;
                    return new ValueTask<bool>(!isUnsafe);
                };

                // These sit under the default 100s RequestTimeout. Nothing enforces that
                // relationship, so a caller who lowers RequestTimeout below 30s can have a
                // slow-but-succeeding call cancelled by the pipeline rather than by their own
                // timeout. Not guarded today; stated here rather than implied to be safe.
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });

        return services;
    }

    /// <summary>Registers a token provider that calls the Databricks CLI for the named profile.</summary>
    public static IServiceCollection AddDatabricksCliAuthentication(
        this IServiceCollection services, string? profile = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IGenieTokenProvider>(_ => new DatabricksCliTokenProvider(profile));
        return services;
    }

    /// <summary>Registers a caller-supplied token source, for hosts with their own OAuth flow.</summary>
    public static IServiceCollection AddGenieTokenProvider(
        this IServiceCollection services, Func<CancellationToken, ValueTask<string>> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        services.AddSingleton<IGenieTokenProvider>(_ => new DelegateTokenProvider(factory));
        return services;
    }
}

internal static class UserAgent
{
    // Databricks correlates client traffic by user agent; an unidentified client is
    // indistinguishable from a script when someone is investigating workspace load.
    internal static string Value { get; } =
        $"LakeSpeak.NET/{typeof(UserAgent).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";
}
