using LakeSpeak.Genie;

namespace LakeSpeak.Cli.Tests;

/// <summary>
/// The exit codes are documented as contractual: scripts branch on them, so an existing code must
/// never change meaning. That promise had no test behind it until this file.
/// </summary>
public class ExitCodeTests
{
    [Theory]
    [InlineData(GenieFailureKind.Authentication, ExitCode.Authentication)]
    [InlineData(GenieFailureKind.Authorization, ExitCode.Authorization)]
    [InlineData(GenieFailureKind.AgentNotFound, ExitCode.NotFound)]
    [InlineData(GenieFailureKind.ConversationNotFound, ExitCode.NotFound)]
    [InlineData(GenieFailureKind.MessageFailed, ExitCode.GenieFailure)]
    [InlineData(GenieFailureKind.MessageCancelled, ExitCode.GenieFailure)]
    [InlineData(GenieFailureKind.QueryExecutionFailed, ExitCode.GenieFailure)]
    [InlineData(GenieFailureKind.QueryResultExpired, ExitCode.GenieFailure)]
    [InlineData(GenieFailureKind.RateLimited, ExitCode.GenieFailure)]
    [InlineData(GenieFailureKind.PollingTimeout, ExitCode.Timeout)]
    [InlineData(GenieFailureKind.MalformedResponse, ExitCode.MalformedResponse)]
    [InlineData(GenieFailureKind.UnsupportedResult, ExitCode.MalformedResponse)]
    [InlineData(GenieFailureKind.Network, ExitCode.Unexpected)]
    [InlineData(GenieFailureKind.Unexpected, ExitCode.Unexpected)]
    public void Each_failure_kind_maps_to_its_documented_code(GenieFailureKind kind, int expected)
    {
        // Arrange — the kind under test arrives as the theory parameter.

        // Act
        var code = ExitCode.From(kind);

        // Assert
        code.ShouldBe(expected);
    }

    /// <summary>
    /// Adding a <see cref="GenieFailureKind"/> without extending the mapping would otherwise
    /// surface as an unhandled exception at the moment a user hits that failure — the worst
    /// possible time to discover it.
    /// </summary>
    [Fact]
    public void Every_failure_kind_is_mapped()
    {
        // Arrange
        var kinds = Enum.GetValues<GenieFailureKind>();

        // Act
        var unmapped = kinds.Where(k => !TryMap(k)).ToList();

        // Assert
        unmapped.ShouldBeEmpty();
    }

    /// <summary>
    /// The numeric values themselves are the contract. A refactor that renumbered them would
    /// silently break every script branching on them, and nothing else would notice.
    /// </summary>
    [Fact]
    public void The_documented_numbers_have_not_moved()
    {
        // Arrange — the documented table from docs/commands.md.
        var documented = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };

        // Act
        var actual = new[]
        {
            ExitCode.Success,
            ExitCode.Unexpected,
            ExitCode.InvalidUsage,
            ExitCode.Authentication,
            ExitCode.Authorization,
            ExitCode.NotFound,
            ExitCode.GenieFailure,
            ExitCode.Timeout,
            ExitCode.PartialPackFailure,
            ExitCode.MalformedResponse,
        };

        // Assert
        actual.ShouldBe(documented);
    }

    [Fact]
    public void Only_success_is_zero()
    {
        // Arrange — success must stay 0 and every failure non-zero, or `set -e` and CI break.
        var kinds = Enum.GetValues<GenieFailureKind>();

        // Act
        var codes = kinds.Select(ExitCode.From).ToList();

        // Assert
        codes.ShouldAllBe(c => c != ExitCode.Success);
    }

    private static bool TryMap(GenieFailureKind kind)
    {
        try
        {
            ExitCode.From(kind);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
