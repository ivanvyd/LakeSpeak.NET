using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LakeSpeak.Genie.Authentication;

/// <summary>Supplies a bearer token for Databricks REST calls.</summary>
public interface IGenieTokenProvider
{
    /// <summary>
    /// Returns a currently-valid access token. Implementations are responsible for refresh;
    /// callers must not cache the result beyond the request they are making.
    /// </summary>
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Brokers short-lived OAuth tokens through the installed Databricks CLI.
/// </summary>
/// <remarks>
/// <para>
/// This is the default for interactive use, and the reason LakeSpeak has no credential store of
/// its own. <c>databricks auth token</c> returns a cached U2M token and refreshes it when needed,
/// so the browser login flow, the refresh logic and the token at rest all stay inside a tool
/// Databricks maintains.
/// </para>
/// <para>
/// The CLI is invoked with an argument vector and <c>UseShellExecute = false</c>. The profile name
/// can come from a configuration file or a Question Pack, so it must never reach a shell
/// interpreter.
/// </para>
/// </remarks>
public sealed class DatabricksCliTokenProvider : IGenieTokenProvider, IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    // Refresh slightly early: a token that expires while in flight fails the request it was
    // fetched for, and the retry looks like an intermittent auth bug.
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromSeconds(60);

    private readonly string? _profile;
    private readonly string _executable;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTimeOffset _expiresAt;

    public DatabricksCliTokenProvider(
        string? profile = null,
        string executable = "databricks",
        TimeProvider? timeProvider = null)
    {
        _profile = profile;
        _executable = executable;
        _time = timeProvider ?? TimeProvider.System;
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
            // each spawn their own CLI process.
            if (_token is not null && _time.GetUtcNow() < _expiresAt - ExpiryGrace)
            {
                return _token;
            }

            var (token, expiry) = await FetchAsync(cancellationToken).ConfigureAwait(false);
            _token = token;
            _expiresAt = expiry ?? _time.GetUtcNow().AddMinutes(30);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string Token, DateTimeOffset? Expiry)> FetchAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("token");
        if (!string.IsNullOrEmpty(_profile))
        {
            psi.ArgumentList.Add("--profile");
            psi.ArgumentList.Add(_profile);
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new GenieException(
                GenieFailureKind.Authentication,
                $"Could not run the Databricks CLI ('{_executable}'). Install it from " +
                "https://docs.databricks.com/dev-tools/cli/install.html and run " +
                "`databricks auth login` before using LakeSpeak.",
                innerException: ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProcessTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new GenieException(
                GenieFailureKind.Authentication,
                $"The Databricks CLI did not return a token within {ProcessTimeout.TotalSeconds:F0}s. " +
                "If this profile needs a browser login, run `databricks auth login` first.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            // stderr is scrubbed: it can echo configuration, and on some failures the token
            // itself. GenieException scrubs again, which is intentional belt-and-braces.
            throw new GenieException(
                GenieFailureKind.Authentication,
                $"`databricks auth token` failed (exit {process.ExitCode}). " +
                $"{DiagnosticRedaction.Scrub(Truncate(stderr))}".TrimEnd());
        }

        return Parse(stdout);
    }

    private static (string Token, DateTimeOffset? Expiry) Parse(string stdout)
    {
        // The CLI's output field names are not contractual, so this parses defensively and
        // fails with a clear message rather than returning an empty token that produces a
        // confusing 401 later.
        CliToken? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CliToken>(stdout);
        }
        catch (JsonException)
        {
            throw new GenieException(
                GenieFailureKind.Authentication,
                "Could not parse the Databricks CLI token output as JSON. " +
                "This usually means an incompatible CLI version.");
        }

        if (string.IsNullOrEmpty(parsed?.AccessToken))
        {
            throw new GenieException(
                GenieFailureKind.Authentication,
                "The Databricks CLI returned no access token. Run `databricks auth login " +
                "--profile <profile>` and try again.");
        }

        return (parsed.AccessToken, parsed.Expiry);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill.
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s.Trim() : s[..500].Trim() + "…";

    public void Dispose() => _gate.Dispose();

    private sealed record CliToken
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }

        [JsonPropertyName("expiry")]
        public DateTimeOffset? Expiry { get; init; }
    }
}

/// <summary>
/// Reads a token from the environment. For unattended use where a browser login is impossible.
/// </summary>
/// <remarks>
/// Accepts <c>DATABRICKS_TOKEN</c>. This is the least preferred path — a long-lived personal
/// access token is a standing credential with no refresh and no expiry pressure — and the CLI
/// documents it as such for local debugging rather than production.
/// </remarks>
public sealed class EnvironmentTokenProvider : IGenieTokenProvider
{
    public const string TokenVariable = "DATABRICKS_TOKEN";

    private readonly string _token;

    public EnvironmentTokenProvider(string? token = null)
    {
        var value = token ?? Environment.GetEnvironmentVariable(TokenVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new GenieException(
                GenieFailureKind.Authentication,
                $"{TokenVariable} is not set.");
        }

        _token = value;
    }

    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_token);
}

/// <summary>A token supplied by the host application, for example from its own OAuth flow.</summary>
public sealed class DelegateTokenProvider(Func<CancellationToken, ValueTask<string>> factory) : IGenieTokenProvider
{
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
        => factory(cancellationToken);
}
