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
    /// <summary>Matches a fenced code block, capturing its body.</summary>
    private static readonly Regex FencedBlock = new(
        @"^```[^\n]*\n(.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline);

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

        return data;
    }

    [Theory]
    [MemberData(nameof(DocumentedInvocations))]
    public void Every_documented_invocation_parses_against_the_real_command_tree(string invocation, string file)
    {
        // Arrange
        var root = Program.CreateRootCommand();
        var args = Tokenize(invocation).Skip(1).ToArray();

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

        // Assert — a floor, not a count: it catches the extractor silently finding nothing, and
        // does not need updating when the documentation gains or loses an example.
        found.ShouldBeGreaterThan(15);
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
            // invocations; a command starts the line or follows an assignment or a prompt.
            var prefix = raw[..start].TrimEnd();
            if (prefix.Length > 0 && !prefix.EndsWith('=') && !prefix.EndsWith('$'))
            {
                continue;
            }

            var command = raw[start..];

            foreach (var terminator in new[] { "|", "#", "2>", ">", "&&", ";" })
            {
                var at = command.IndexOf(terminator, StringComparison.Ordinal);
                if (at >= 0)
                {
                    command = command[..at];
                }
            }

            command = command.Trim();

            if (command.Length > "lakespeak ".Length && !Placeholder.IsMatch(command))
            {
                yield return command;
            }
        }
    }

    /// <summary>Splits a command line on whitespace, keeping quoted arguments whole.</summary>
    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

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
