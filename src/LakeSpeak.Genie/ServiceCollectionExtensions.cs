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

            // DATABRICKS_TOKEN wins when set. Unattended environments — CI, a container, a
            // scheduled Question Pack — have no browser and often no Databricks CLI, so the
            // environment has to be a real path rather than a documented one that silently
            // falls through to a CLI that is not installed.
            if (Environment.GetEnvironmentVariable(EnvironmentTokenProvider.TokenVariable) is { Length: > 0 })
            {
                return new EnvironmentTokenProvider();
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
                resilience.Retry.DisableForUnsafeHttpMethods();

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
