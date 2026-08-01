using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LakeSpeak.QuestionPacks;

public sealed record QuestionPack(
    string Name,
    string? Description,
    string Agent,
    string? Profile,
    IReadOnlyList<PackQuestion> Questions,
    PackOutput Output,
    PackBehavior Behavior)
{
    /// <summary>Directory the pack was loaded from. Output paths resolve against it, not the cwd.</summary>
    public string BaseDirectory { get; init; } = ".";
}

public sealed record PackQuestion(string Id, string? Title, string Ask, TimeSpan? Timeout);

public sealed record PackOutput(string Format, string? Path);

public sealed record PackBehavior(
    bool ContinueOnQuestionFailure,
    bool IncludeGeneratedSql,
    bool IncludeTimings,
    bool IncludeIdentifiers,
    TimeSpan Timeout);

public sealed class PackValidationException(IReadOnlyList<string> errors)
    : Exception("The Question Pack is not valid:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Loads and validates a Question Pack.
/// </summary>
/// <remarks>
/// Validation is strict and happens before anything is executed. A pack is data: it names an
/// Agent and some questions, and it cannot run a command, read a file, or reach outside its own
/// directory to write one. Unknown keys are rejected rather than ignored, so a typo in
/// <c>continueOnQuestionFailure</c> fails loudly instead of silently changing behaviour.
/// </remarks>
public static partial class QuestionPackLoader
{
    private const int MaxQuestions = 50;

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^(\d+)([smh])$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Duration();

    public static QuestionPack Load(string path)
    {
        var text = File.ReadAllText(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return Parse(text, directory);
    }

    public static QuestionPack Parse(string yaml, string baseDirectory)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        RawPack? raw;
        try
        {
            raw = deserializer.Deserialize<RawPack>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new PackValidationException([$"line {ex.Start.Line}: {ex.Message}"]);
        }

        if (raw is null)
        {
            throw new PackValidationException(["the file is empty"]);
        }

        var errors = new List<string>();

        if (raw.ApiVersion != "lakespeak.dev/v1alpha1")
        {
            errors.Add($"apiVersion must be 'lakespeak.dev/v1alpha1' (found '{raw.ApiVersion ?? "nothing"}')");
        }

        if (raw.Kind != "QuestionPack")
        {
            errors.Add($"kind must be 'QuestionPack' (found '{raw.Kind ?? "nothing"}')");
        }

        var name = raw.Metadata?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("metadata.name is required");
        }
        else if (!SafeName().IsMatch(name))
        {
            // The name reaches a filename, so anything else is a path-traversal vector.
            errors.Add($"metadata.name '{name}' must be lowercase kebab-case");
        }

        if (string.IsNullOrWhiteSpace(raw.Spec?.Agent))
        {
            errors.Add("spec.agent is required; a pack must say which Agent it runs against");
        }

        var questions = new List<PackQuestion>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        if (raw.Spec?.Questions is not { Count: > 0 } rawQuestions)
        {
            errors.Add("spec.questions must contain at least one question");
        }
        else
        {
            if (rawQuestions.Count > MaxQuestions)
            {
                errors.Add($"spec.questions has {rawQuestions.Count} entries; the maximum is {MaxQuestions}");
            }

            foreach (var q in rawQuestions)
            {
                if (string.IsNullOrWhiteSpace(q.Id) || !SafeName().IsMatch(q.Id))
                {
                    errors.Add($"question id '{q.Id}' must be lowercase kebab-case");
                    continue;
                }

                if (!seenIds.Add(q.Id))
                {
                    // Duplicate ids would collide as report anchors and make results ambiguous.
                    errors.Add($"duplicate question id '{q.Id}'");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(q.Ask))
                {
                    errors.Add($"question '{q.Id}' has an empty ask");
                    continue;
                }

                questions.Add(new PackQuestion(q.Id, q.Title, q.Ask.Trim(), ParseDuration(q.Timeout, errors, q.Id)));
            }
        }

        var outputPath = raw.Spec?.Output?.Path;
        if (outputPath is { Length: > 0 })
        {
            ValidateOutputPath(outputPath, baseDirectory, errors);
        }

        var format = raw.Spec?.Output?.Format ?? "markdown";
        if (format is not ("markdown" or "json"))
        {
            errors.Add($"spec.output.format must be 'markdown' or 'json' (found '{format}')");
        }

        if (errors.Count > 0)
        {
            throw new PackValidationException(errors);
        }

        var behavior = raw.Spec!.Behavior;
        return new QuestionPack(
            name!,
            raw.Metadata?.Description,
            raw.Spec.Agent!,
            raw.Spec.Profile,
            questions,
            new PackOutput(format, outputPath),
            new PackBehavior(
                behavior?.ContinueOnQuestionFailure ?? true,
                behavior?.IncludeGeneratedSql ?? false,
                behavior?.IncludeTimings ?? true,
                behavior?.IncludeIdentifiers ?? false,
                ParseDuration(behavior?.Timeout, errors, "behavior") ?? TimeSpan.FromMinutes(10)))
        {
            BaseDirectory = baseDirectory,
        };
    }

    /// <summary>
    /// Rejects an output path that escapes the pack's own directory.
    /// </summary>
    /// <remarks>
    /// A pack can arrive from a pull request or a shared repository, so its output path is
    /// attacker-influenced. Without this, <c>../../.ssh/authorized_keys</c> is a valid target.
    /// </remarks>
    private static void ValidateOutputPath(string path, string baseDirectory, List<string> errors)
    {
        if (Path.IsPathRooted(path))
        {
            errors.Add($"spec.output.path '{path}' must be relative to the pack file");
            return;
        }

        var root = Path.GetFullPath(baseDirectory);
        var target = Path.GetFullPath(Path.Combine(root, path));

        if (!IsInside(target, root))
        {
            errors.Add($"spec.output.path '{path}' resolves outside the pack directory");
            return;
        }

        // The lexical check above is not enough on its own. A pack arrives as a YAML file inside
        // a directory the author controls, and that directory can contain a symlink or a Windows
        // junction. `link/report.md` then passes every string comparison while the write follows
        // the reparse point and lands anywhere the attacker chose — traversal without a single
        // `..`. Each component between the root and the target is therefore resolved.
        if (FollowsALink(target, root))
        {
            errors.Add(
                $"spec.output.path '{path}' passes through a symbolic link or junction, so its " +
                "real destination is outside the pack directory");
        }
    }

    private static bool IsInside(string target, string root) =>
        target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || string.Equals(target, root, StringComparison.Ordinal);

    private static bool FollowsALink(string target, string root)
    {
        for (var current = Path.GetDirectoryName(target);
             current is not null && current.Length >= root.Length;
             current = Path.GetDirectoryName(current))
        {
            var info = new DirectoryInfo(current);
            if (!info.Exists)
            {
                continue;
            }

            // ResolveLinkTarget returns null for an ordinary directory, so a non-null answer
            // means this component redirects somewhere. Re-check where it actually lands.
            if (info.ResolveLinkTarget(returnFinalTarget: true) is { } resolved
                && !IsInside(Path.GetFullPath(resolved.FullName), root))
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan? ParseDuration(string? value, List<string> errors, string context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Duration().Match(value);
        if (!match.Success)
        {
            errors.Add($"{context}: '{value}' is not a duration such as 90s, 5m or 1h");
            return null;
        }

        var amount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch
        {
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            _ => null,
        };
    }

    // Deserialization shapes. Separate from the validated model so a half-valid file never
    // reaches the runner.
    private sealed class RawPack
    {
        public string? ApiVersion { get; set; }
        public string? Kind { get; set; }
        public RawMetadata? Metadata { get; set; }
        public RawSpec? Spec { get; set; }
    }

    private sealed class RawMetadata
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    private sealed class RawSpec
    {
        public string? Agent { get; set; }
        public string? Profile { get; set; }
        public List<RawQuestion>? Questions { get; set; }
        public RawOutput? Output { get; set; }
        public RawBehavior? Behavior { get; set; }
    }

    private sealed class RawQuestion
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Ask { get; set; }
        public string? Timeout { get; set; }
    }

    private sealed class RawOutput
    {
        public string? Format { get; set; }
        public string? Path { get; set; }
    }

    private sealed class RawBehavior
    {
        public bool? ContinueOnQuestionFailure { get; set; }
        public bool? IncludeGeneratedSql { get; set; }
        public bool? IncludeTimings { get; set; }
        public bool? IncludeIdentifiers { get; set; }
        public string? Timeout { get; set; }
    }
}
