using LakeSpeak.QuestionPacks;

namespace LakeSpeak.QuestionPacks.Tests;

public class QuestionPackLoaderTests
{
    private const string Valid =
        """
        apiVersion: lakespeak.dev/v1alpha1
        kind: QuestionPack
        metadata:
          name: daily-brief
          description: A daily summary
        spec:
          agent: platform-operations
          questions:
            - id: failed-jobs
              title: Failed jobs
              ask: Which production jobs failed in the last 24 hours?
              timeout: 90s
          output:
            format: markdown
            path: reports/daily.md
          behavior:
            continueOnQuestionFailure: false
            includeGeneratedSql: true
            includeIdentifiers: true
            timeout: 5m
        """;

    [Fact]
    public void Parses_a_valid_pack()
    {
        var pack = QuestionPackLoader.Parse(Valid, "/packs");

        pack.Name.ShouldBe("daily-brief");
        pack.Agent.ShouldBe("platform-operations");
        pack.Questions.Single().Id.ShouldBe("failed-jobs");
        pack.Questions.Single().Timeout.ShouldBe(TimeSpan.FromSeconds(90));
        pack.Behavior.ContinueOnQuestionFailure.ShouldBeFalse();
        pack.Behavior.IncludeGeneratedSql.ShouldBeTrue();
        pack.Behavior.Timeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Defaults_are_the_safe_ones()
    {
        var pack = QuestionPackLoader.Parse(
            """
            apiVersion: lakespeak.dev/v1alpha1
            kind: QuestionPack
            metadata:
              name: minimal
            spec:
              agent: sales
              questions:
                - id: q1
                  ask: How did revenue change?
            """,
            "/packs");

        // Identifiers off by default: a conversation id points at governed data, and a report
        // gets committed.
        pack.Behavior.IncludeIdentifiers.ShouldBeFalse();
        pack.Behavior.IncludeGeneratedSql.ShouldBeFalse();
        // Partial results beat no results for a scheduled report.
        pack.Behavior.ContinueOnQuestionFailure.ShouldBeTrue();
    }

    // A pack can arrive from a pull request, so its output path is attacker-influenced.
    [Theory]
    [InlineData("../escaped.md")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../escaped.md")]
    public void Rejects_an_output_path_that_escapes_the_pack_directory(string path)
    {
        var yaml = Valid.Replace("path: reports/daily.md", $"path: {path}", StringComparison.Ordinal);

        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        ex.Errors.ShouldContain(e => e.Contains("outside the pack directory", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_absolute_output_path()
    {
        var absolute = OperatingSystem.IsWindows() ? @"C:\temp\out.md" : "/tmp/out.md";
        var yaml = Valid.Replace("path: reports/daily.md", $"path: '{absolute}'", StringComparison.Ordinal);

        Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"))
            .Errors.ShouldContain(e => e.Contains("must be relative", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        var yaml =
            """
            apiVersion: lakespeak.dev/v1alpha1
            kind: QuestionPack
            metadata:
              name: Bad_Name
            spec:
              agent: sales
              questions:
                - id: Upper-Case
                  ask: one
                - id: dup
                  ask: two
                - id: dup
                  ask: three
            """;

        var ex = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));

        // Reporting only the first turns fixing a pack into repeated guessing.
        ex.Errors.Count.ShouldBeGreaterThanOrEqualTo(3);
        ex.Errors.ShouldContain(e => e.Contains("kebab-case", StringComparison.Ordinal));
        ex.Errors.ShouldContain(e => e.Contains("duplicate question id", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("lakespeak.dev/v1", "apiVersion")]
    [InlineData("lakespeak.dev/v1alpha1", "kind")]
    public void Rejects_a_wrong_apiVersion_or_kind(string apiVersion, string broken)
    {
        var yaml = Valid.Replace("apiVersion: lakespeak.dev/v1alpha1", $"apiVersion: {apiVersion}", StringComparison.Ordinal);
        if (broken == "kind")
        {
            yaml = yaml.Replace("kind: QuestionPack", "kind: Something", StringComparison.Ordinal);
        }

        Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));
    }

    [Fact]
    public void Requires_an_agent()
    {
        var yaml = Valid.Replace("  agent: platform-operations\n", string.Empty, StringComparison.Ordinal);

        Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"))
            .Errors.ShouldContain(e => e.Contains("spec.agent is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Caps_the_number_of_questions()
    {
        var questions = string.Join('\n',
            Enumerable.Range(1, 51).Select(i => $"    - id: q{i}\n      ask: question {i}"));

        var yaml =
            $"""
            apiVersion: lakespeak.dev/v1alpha1
            kind: QuestionPack
            metadata:
              name: huge
            spec:
              agent: sales
              questions:
            {questions}
            """;

        Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"))
            .Errors.ShouldContain(e => e.Contains("maximum is 50", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_unparseable_duration()
    {
        var yaml = Valid.Replace("timeout: 90s", "timeout: soon", StringComparison.Ordinal);

        Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"))
            .Errors.ShouldContain(e => e.Contains("is not a duration", StringComparison.Ordinal));
    }

    // An unknown key is far more likely a typo in a real key than a deliberate extension, and
    // silently ignoring it means the setting the author intended never takes effect.
    [Fact]
    public void Rejects_an_unknown_key()
    {
        var yaml = Valid.Replace("  agent: platform-operations",
            "  agent: platform-operations\n  contineuOnFailure: true", StringComparison.Ordinal);

        Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, "/packs"));
    }
}
