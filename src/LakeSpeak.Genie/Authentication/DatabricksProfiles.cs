namespace LakeSpeak.Genie.Authentication;

/// <summary>One profile from <c>.databrickscfg</c>.</summary>
public sealed record DatabricksProfile(string Name, Uri? Host, bool UsesLegacyToken);

/// <summary>
/// Reads <c>.databrickscfg</c> to discover workspace hosts and profile names.
/// </summary>
/// <remarks>
/// Only the host and the presence of a legacy token are read. Token values in the file are never
/// loaded — LakeSpeak brokers credentials through the CLI instead, and a value it never reads is
/// a value it cannot leak.
/// </remarks>
public static class DatabricksProfiles
{
    public static string DefaultConfigPath =>
        Environment.GetEnvironmentVariable("DATABRICKS_CONFIG_FILE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".databrickscfg");

    public static IReadOnlyList<DatabricksProfile> Load(string? path = null)
    {
        path ??= DefaultConfigPath;
        if (!File.Exists(path))
        {
            return [];
        }

        var profiles = new List<DatabricksProfile>();
        var name = "DEFAULT";
        Uri? host = null;
        var legacyToken = false;
        var started = false;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                if (started)
                {
                    profiles.Add(new DatabricksProfile(name, host, legacyToken));
                }

                name = line[1..^1].Trim();
                host = null;
                legacyToken = false;
                started = true;
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (key.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                host = Uri.TryCreate(value, UriKind.Absolute, out var u) ? u : null;
            }
            else if (key.Equals("token", StringComparison.OrdinalIgnoreCase))
            {
                // Recorded as a flag only. The value is deliberately not captured.
                legacyToken = value.Length > 0;
            }
        }

        if (started)
        {
            profiles.Add(new DatabricksProfile(name, host, legacyToken));
        }

        return profiles;
    }

    public static DatabricksProfile? Find(string profileName, string? path = null) =>
        Load(path).FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the workspace host, in the documented precedence order: explicit value, then
    /// <c>DATABRICKS_HOST</c>, then the named profile, then the DEFAULT profile.
    /// </summary>
    public static Uri? ResolveHost(Uri? explicitHost, string? profileName, string? path = null)
    {
        if (explicitHost is not null)
        {
            return explicitHost;
        }

        if (Environment.GetEnvironmentVariable("DATABRICKS_HOST") is { Length: > 0 } env
            && Uri.TryCreate(env, UriKind.Absolute, out var envHost))
        {
            return envHost;
        }

        var profiles = Load(path);
        if (profiles.Count == 0)
        {
            return null;
        }

        var match = profileName is { Length: > 0 }
            ? profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
            : profiles.FirstOrDefault(p => p.Name.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase));

        return match?.Host;
    }
}
