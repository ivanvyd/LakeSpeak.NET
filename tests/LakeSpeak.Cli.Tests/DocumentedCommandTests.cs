using System.CommandLine.Parsing;
using System.Text.RegularExpressions;

namespace LakeSpeak.Cli.Tests;

/// <summary>
/// Every `lakespeak …` example in the documentation, parsed against the real command tree.
/// </summary>
/// <remarks>
/// The documentation is the product for anyone who has not installed the tool yet, and a command
/// that no longer exists is a broken promise nothing else catches. This test found its own
/// justification: a stale test count sat in the README for several releases because prose has no
/// owner and no compiler.
/// </remarks>
public sealed class DocumentedCommandTests
{
    /// <summary>
    /// Matches a fenced code block, capturing its body. Leading whitespace is allowed because a
    /// fence nested in a numbered list is indented, and this repository's docs already do that.
    /// </summary>
    private static readonly Regex FencedBlock = new(
        @"^[ \t]*```[^\n]*\n(.*?)^[ \t]*```", RegexOptions.Multiline | RegexOptions.Singleline);

    /// <summary>
    /// A placeholder is a synopsis rather than a runnable example — `lakespeak &lt;command&gt;
    /// [options]` documents the shape of the CLI, not a command to run.
    /// </summary>
    private static readonly Regex Placeholder = new(@"[<\[]");

    public static TheoryData<string, string> DocumentedInvocations()
    {
        var data = new TheoryData<string, string>();

        foreach (var file in Directory.EnumerateFiles(RepositoryRoot(), "*.md", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(RepositoryRoot(), file);

            foreach (var block in FencedBlock.Matches(File.ReadAllText(file)).Cast<Match>())
            {
                foreach (var invocation in InvocationsIn(block.Groups[1].Value))
                {
                    data.Add(invocation, relative);
                }
            }
        }

        // Runnable examples are not only in markdown. The shipped GitHub Actions sample invokes
        // the CLI for real, and a flag renamed out from under it would otherwise go unnoticed
        // precisely because nobody re-reads a sample workflow.
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot(), "examples"), "*.yml", SearchOption.AllDirectories))
        {
            foreach (var invocation in InvocationsIn(File.ReadAllText(file)))
            {
                data.Add(invocation, Path.GetRelativePath(RepositoryRoot(), file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DocumentedInvocations))]
    public void Every_documented_invocation_parses_against_the_real_command_tree(string invocation, string file)
    {
        // Arrange
        var root = Program.CreateRootCommand();
        var args = CommandLineParser.SplitCommandLine(invocation).Skip(1).ToArray();

        // Act
        var parseResult = root.Parse(args);

        // Assert
        var errors = string.Join("; ", parseResult.Errors.Select(e => e.Message));
        errors.ShouldBeEmpty($"`{invocation}` in {file} does not parse. {errors}");
    }

    /// <summary>
    /// Guards the guard: if the extractor silently stopped finding invocations, every assertion
    /// above would pass by being asked nothing.
    /// </summary>
    [Fact]
    public void The_documentation_contains_invocations_to_check()
    {
        // Arrange, Act
        var found = DocumentedInvocations().Count;

        // Assert — a floor set near the real total (42 at the time of writing), not a token one.
        // A floor of 15 would have let a regression silently drop two thirds of the corpus while
        // still passing, which is the failure this guard exists to catch.
        found.ShouldBeGreaterThan(35);
    }

    /// <summary>
    /// A redirection or comment character inside a quoted question is part of the question.
    /// </summary>
    /// <remarks>
    /// Stripping shell terminators off the raw string used to cut here, which was worse than a
    /// crash: `lakespeak ask --agent sales "priced &gt; $50?"` truncated to a command that still
    /// parsed, so the test passed while checking a string the documentation does not contain.
    /// </remarks>
    [Theory]
    [InlineData("""lakespeak ask --agent sales "What sold at a price > 50?" """, "What sold at a price > 50?")]
    [InlineData("""lakespeak ask --agent sales "Which SKUs are #1 by revenue?" """, "Which SKUs are #1 by revenue?")]
    public void A_shell_character_inside_a_quoted_question_is_kept(string line, string expectedQuestion)
    {
        // Arrange, Act
        var extracted = InvocationsIn(line).Single();
        var tokens = CommandLineParser.SplitCommandLine(extracted).ToList();

        // Assert — the question survives whole, and the command still parses.
        tokens[^1].ShouldBe(expectedQuestion);
        Program.CreateRootCommand().Parse(tokens.Skip(1).ToArray()).Errors.ShouldBeEmpty();
    }

    /// <summary>
    /// Pulls each `lakespeak …` command out of a code block, dropping the shell around it: a
    /// PowerShell assignment before it, and a pipe, redirection or comment after it.
    /// </summary>
    private static IEnumerable<string> InvocationsIn(string block)
    {
        foreach (var raw in block.Split('\n'))
        {
            var start = raw.IndexOf("lakespeak ", StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            // `dotnet tool install --global LakeSpeak.Cli` and prose mentioning a path are not
            // invocations. A real one starts its line, or follows a shell assignment, a prompt,
            // or a YAML key — `run: lakespeak …` in a workflow is a command like any other, and
            // omitting that case made this scan pass by finding nothing.
            var prefix = raw[..start].TrimEnd();
            if (prefix.Length > 0
                && !prefix.EndsWith('=') && !prefix.EndsWith('$') && !prefix.EndsWith(':'))
            {
                continue;
            }

            // Tokenize before dropping the shell around the command. Truncating the raw string
            // at the first `>` or `#` cuts inside a quoted question — "products priced > $50"
            // would silently become a different, still-parseable command, and the test would go
            // green having validated something the documentation does not say.
            var tokens = CommandLineParser.SplitCommandLine(raw[start..]).ToList();

            var shell = tokens.FindIndex(IsShellTerminator);
            if (shell >= 0)
            {
                tokens = tokens[..shell];
            }

            if (tokens.Count > 1 && !tokens.Any(t => Placeholder.IsMatch(t)))
            {
                yield return string.Join(' ', tokens.Select(Requote));
            }
        }
    }

    private static bool IsShellTerminator(string token)
        => token is "|" or "#" or "&&" or ";"
            || token.StartsWith('>')
            || token.StartsWith("2>", StringComparison.Ordinal);

    /// <summary>
    /// Puts quotes back around a token that has whitespace in it, so the joined string splits
    /// back into the same tokens when the test re-reads it.
    /// </summary>
    private static string Requote(string token)
        => token.Any(char.IsWhiteSpace) ? $"\"{token}\"" : token;

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution file. The docs are
    /// repository content, not test content, so they are not copied to the output directory.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LakeSpeak.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repository root (no LakeSpeak.slnx above the test binary).");
    }
}
