using LakeSpeak.Genie.Authentication;

namespace LakeSpeak.Genie.Tests;

/// <summary>
/// The CLI broker must never run a <c>databricks</c> that merely sits in the current directory.
/// </summary>
/// <remarks>
/// On Linux and macOS, Process.Start checks the current directory before PATH, so a cloned
/// repository could otherwise plant the binary that receives the user's OAuth session.
/// </remarks>
public sealed class CliExecutableResolutionTests : IDisposable
{
    private static readonly string FileName = OperatingSystem.IsWindows() ? "databricks.exe" : "databricks";

    // Under the working directory rather than the temp directory, so a relative path to it exists
    // even where the two are on different Windows drives.
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Directory.GetCurrentDirectory(), "lakespeak-cli-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Plant(string directory, bool executable = true)
    {
        var path = Path.Combine(_root, directory);
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, FileName);
        File.WriteAllText(file, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, executable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    [Fact]
    public void A_non_executable_match_is_skipped_for_a_later_path_entry()
    {
        // Arrange
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no execute bit.");
        var shadow = Plant("shadow", executable: false);
        var bin = Plant("bin");

        // Act
        var resolved = DatabricksCliTokenProvider.ResolveExecutable("databricks", shadow + Path.PathSeparator + bin);

        // Assert
        resolved.ShouldBe(Path.Combine(bin, FileName));
    }

    [Fact]
    public void Bare_name_resolves_from_an_absolute_path_entry()
    {
        // Arrange
        var bin = Plant("bin");

        // Act
        var resolved = DatabricksCliTokenProvider.ResolveExecutable("databricks", Path.Combine(_root, "empty") + Path.PathSeparator + bin);

        // Assert
        resolved.ShouldBe(Path.Combine(bin, FileName));
    }

    [Fact]
    public void Relative_and_empty_path_entries_are_never_searched()
    {
        // Arrange — the binary exists only where a relative entry would find it. An empty entry
        // means the current directory on Unix shells.
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), Plant("checkout"));

        // Act
        var resolved = DatabricksCliTokenProvider.ResolveExecutable(
            "databricks", relative + Path.PathSeparator + string.Empty + Path.PathSeparator + ".");

        // Assert
        resolved.ShouldBeNull();
    }

    [Fact]
    public void An_explicit_path_is_used_as_given()
    {
        // Arrange
        var explicitPath = Path.Combine(_root, "tools", FileName);

        // Act
        var resolved = DatabricksCliTokenProvider.ResolveExecutable(explicitPath, pathVariable: null);

        // Assert
        resolved.ShouldBe(explicitPath);
    }

    [Fact]
    public async Task A_cli_missing_from_path_is_an_authentication_failure()
    {
        // Arrange
        using var provider = new DatabricksCliTokenProvider(executable: "lakespeak-no-such-cli-" + Guid.NewGuid().ToString("N"));

        // Act
        var failure = await Should.ThrowAsync<GenieException>(
            () => provider.GetTokenAsync(TestContext.Current.CancellationToken).AsTask());

        // Assert
        failure.Kind.ShouldBe(GenieFailureKind.Authentication);
    }
}
