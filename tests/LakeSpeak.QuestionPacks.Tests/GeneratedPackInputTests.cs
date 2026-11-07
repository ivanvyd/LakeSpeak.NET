using System.Text.Json;

namespace LakeSpeak.QuestionPacks.Tests;

public class GeneratedPackInputTests
{
    private const string Valid = """
        apiVersion: lakespeak.net/v1alpha1
        kind: QuestionPack
        metadata:
          name: generated-pack
        spec:
          agent: sales
          questions:
            - id: question
              ask: How many orders?
        """;

    public static TheoryData<string> InvalidDocuments
    {
        get
        {
            var cases = new TheoryData<string>
            {
                string.Empty, "null", "[]", "true", "42", "plain text", "{",
                "apiVersion: [", "metadata: *missing", "spec: !!str wrong",
                "metadata: {name: [wrong]}", "spec: {questions: [null]}",
            };
            foreach (var key in new[] { "metadata", "spec" })
            {
                foreach (var value in new[] { "[]", "true", "42", "text" })
                {
                    cases.Add($"{key}: {value}");
                }
            }

            foreach (var name in new[] { "../escape", "a/b", "UPPER", "name space", new string('a', 64) })
            {
                cases.Add(Valid.Replace("name: generated-pack", $"name: {JsonSerializer.Serialize(name)}",
                    StringComparison.Ordinal));
            }

            foreach (var duration in new[] { "0s", "-1s", "1.5h", "1e9s", "999999999999999999999999h" })
            {
                cases.Add(Valid + $"\n  behavior:\n    timeout: {JsonSerializer.Serialize(duration)}\n");
            }

            for (var depth = 1; depth <= 16; depth++)
            {
                cases.Add("spec: " + new string('[', depth) + "null" + new string(']', depth));
                cases.Add(Valid + $"\n  unexpected{depth}: true\n");
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void Malformed_pack_corpus_returns_validation_errors_without_creating_output(string yaml)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lakespeak-corpus-" + Guid.NewGuid().ToString("N"));

        var error = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, directory));

        error.Errors.ShouldNotBeEmpty();
        error.Errors.ShouldAllBe(message => !string.IsNullOrWhiteSpace(message));
        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public void Generated_question_text_is_preserved_without_interpreting_yaml_syntax()
    {
        string[] fragments = [": ", "# comment", "[list]", "*alias", "&anchor", "\n", "\"", "東京 🙂"];
        var directory = Path.Combine(Path.GetTempPath(), "lakespeak-corpus-" + Guid.NewGuid().ToString("N"));
        foreach (var first in fragments)
        {
            foreach (var second in fragments)
            {
                var question = $"Question {first}{second} end";
                var yaml = Valid.Replace("How many orders?", JsonSerializer.Serialize(question), StringComparison.Ordinal);

                var pack = QuestionPackLoader.Parse(yaml, directory);

                pack.Questions.Single().Ask.ShouldBe(question);
                pack.Name.ShouldBe("generated-pack");
                pack.BaseDirectory.ShouldBe(directory);
                pack.Output.Path.ShouldBeNull();
                Directory.Exists(directory).ShouldBeFalse();
            }
        }
    }
}
