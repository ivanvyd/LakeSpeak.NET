using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;

namespace LakeSpeak.QuestionPacks.Tests;

public class ParserPropertyTests
{
    private const string Valid = """
        apiVersion: lakespeak.net/v1alpha1
        kind: QuestionPack
        metadata:
          name: property-pack
        spec:
          agent: sales
          questions:
            - id: question
              ask: QUESTION
        """;

    [Theory]
    [InlineData(1UL)]
    [InlineData(20261107UL)]
    public void Quoted_unicode_questions_round_trip(ulong seed)
    {
        var directory = NewBaseDirectory();
        Prop.ForAll<int[]>(values =>
        {
            var text = new StringBuilder("Question ");
            foreach (var value in values)
            {
                var scalar = (int)((uint)value % 0x110000);
                text.Append((Rune.IsValid(scalar) ? new Rune(scalar) : Rune.ReplacementChar).ToString());
            }

            text.Append('.');
            var question = text.ToString();
            var yaml = Valid.Replace("QUESTION", JsonSerializer.Serialize(question), StringComparison.Ordinal);

            var pack = QuestionPackLoader.Parse(yaml, directory);

            pack.Questions.Single().Ask.ShouldBe(question);
            pack.Name.ShouldBe("property-pack");
            pack.Output.Path.ShouldBeNull();
            Directory.Exists(directory).ShouldBeFalse();
            return true;
        }).Check(Configuration(seed));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(20261107UL)]
    public void Arbitrary_yaml_stays_inside_the_validation_boundary(ulong seed)
    {
        var directory = NewBaseDirectory();
        Prop.ForAll<byte[]>(bytes =>
        {
            var yaml = Encoding.UTF8.GetString(bytes);
            try
            {
                var pack = QuestionPackLoader.Parse(yaml, directory);
                pack.Name.ShouldNotBeNullOrWhiteSpace();
                pack.Questions.ShouldNotBeEmpty();
            }
            catch (PackValidationException error)
            {
                error.Errors.ShouldNotBeEmpty();
                error.Errors.ShouldAllBe(message => !string.IsNullOrWhiteSpace(message));
            }

            Directory.Exists(directory).ShouldBeFalse();
            return true;
        }).Check(Configuration(seed));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(20261107UL)]
    public void Generated_output_paths_cannot_escape_the_pack_directory(ulong seed)
    {
        var directory = NewBaseDirectory();
        Prop.ForAll<byte[], bool>((bytes, escape) =>
        {
            var file = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16))) + ".md";
            var path = (escape ? "../" : "reports/") + file;
            var yaml = Valid + $"\n  output:\n    path: {JsonSerializer.Serialize(path)}\n";
            if (escape)
            {
                var error = Should.Throw<PackValidationException>(() => QuestionPackLoader.Parse(yaml, directory));
                error.Errors.ShouldContain(message => message.Contains("outside the pack directory", StringComparison.Ordinal));
            }
            else
            {
                var pack = QuestionPackLoader.Parse(yaml, directory);
                pack.Output.Path.ShouldBe(path);
                var relative = Path.GetRelativePath(directory, Path.GetFullPath(Path.Combine(directory, path)));
                Path.IsPathRooted(relative).ShouldBeFalse();
                relative.ShouldNotBe("..");
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal).ShouldBeFalse();
            }

            Directory.Exists(directory).ShouldBeFalse();
            return true;
        }).Check(Configuration(seed));
    }

    private static Config Configuration(ulong seed) => Config.QuickThrowOnFailure
        .WithMaxTest(1000)
        .WithEndSize(256)
        .WithMaxRejected(1000)
        .WithReplay(seed, 1UL)
        .WithQuietOnSuccess(true);

    private static string NewBaseDirectory() =>
        Path.Combine(Path.GetTempPath(), "lakespeak-properties-" + Guid.NewGuid().ToString("N"));
}
