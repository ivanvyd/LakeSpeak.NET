using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LakeSpeak.Configuration;

/// <summary>
/// User configuration, read from <c>%APPDATA%\LakeSpeak\config.yaml</c> on Windows and
/// <c>~/.config/lakespeak/config.yaml</c> elsewhere.
/// </summary>
/// <remarks>
/// Holds preferences, Agent aliases and profile names. It deliberately holds no credentials, no
/// questions, no answers and no query results: a configuration file gets copied into dotfile
/// repositories and pasted into issues, so anything sensitive in it eventually escapes.
/// </remarks>
public sealed class LakeSpeakConfig
{
    public int Version { get; set; } = 1;

    public Defaults Defaults { get; set; } = new();

    /// <summary>Alias to Agent mapping, so scripts do not carry raw Agent ids.</summary>
    public Dictionary<string, AgentAlias> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DisplaySettings Display { get; set; } = new();

    public static string DefaultPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LakeSpeak",
                    "config.yaml");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg;
            return Path.Combine(root, "lakespeak", "config.yaml");
        }
    }

    public static LakeSpeakConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return new LakeSpeakConfig();
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        try
        {
            return deserializer.Deserialize<LakeSpeakConfig>(File.ReadAllText(path))
                ?? new LakeSpeakConfig();
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            // Naming the file and the line is the difference between a two-second fix and a
            // confusing "no agents found" further down.
            throw new InvalidOperationException(
                $"{path} is not valid YAML (line {ex.Start.Line}): {ex.Message}", ex);
        }
    }
}

public sealed class Defaults
{
    public string? Profile { get; set; }

    public string? Agent { get; set; }

    public string Output { get; set; } = "text";

    /// <summary>Overall wait for an answer, as a duration such as <c>10m</c>.</summary>
    public string Timeout { get; set; } = "10m";
}

public sealed class AgentAlias
{
    public string? Id { get; set; }

    public string? Profile { get; set; }
}

public sealed class DisplaySettings
{
    /// <summary>Rows shown in a terminal table. The full result is still exported in full.</summary>
    public int MaxRows { get; set; } = 50;

    public bool ShowSqlByDefault { get; set; }

    public bool ShowTimings { get; set; } = true;
}
