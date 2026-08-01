using LakeSpeak.Cli;
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
    [InlineData(GenieFailureKind.Network, ExitCode.Unexpected)]
    [InlineData(GenieFailureKind.Unexpected, ExitCode.Unexpected)]
    public void Each_failure_kind_maps_to_its_documented_code(GenieFailureKind kind, int expected) =>
        ExitCode.From(kind).ShouldBe(expected);

    /// <summary>
    /// Adding a <see cref="GenieFailureKind"/> without extending the mapping would otherwise
    /// surface as an unhandled exception at the moment a user hits that failure — the worst
    /// possible time to discover it.
    /// </summary>
    [Fact]
    public void Every_failure_kind_is_mapped()
    {
        foreach (var kind in Enum.GetValues<GenieFailureKind>())
        {
            Should.NotThrow(() => ExitCode.From(kind), $"{kind} has no exit code mapping.");
        }
    }

    /// <summary>
    /// The numeric values themselves are the contract. A refactor that renumbered them would
    /// silently break every script branching on them, and nothing else would notice.
    /// </summary>
    [Fact]
    public void The_documented_numbers_have_not_moved()
    {
        ExitCode.Success.ShouldBe(0);
        ExitCode.Unexpected.ShouldBe(1);
        ExitCode.InvalidUsage.ShouldBe(2);
        ExitCode.Authentication.ShouldBe(3);
        ExitCode.Authorization.ShouldBe(4);
        ExitCode.NotFound.ShouldBe(5);
        ExitCode.GenieFailure.ShouldBe(6);
        ExitCode.Timeout.ShouldBe(7);
        ExitCode.PartialPackFailure.ShouldBe(8);
        ExitCode.MalformedResponse.ShouldBe(9);
    }

    // Success must stay 0 and every failure non-zero, or `set -e` and CI stop working.
    [Fact]
    public void Only_success_is_zero()
    {
        foreach (var kind in Enum.GetValues<GenieFailureKind>())
        {
            ExitCode.From(kind).ShouldNotBe(ExitCode.Success);
        }
    }
}
