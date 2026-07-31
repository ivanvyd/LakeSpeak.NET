using LakeSpeak.Genie;
using Shouldly;

namespace LakeSpeak.Genie.Tests;

public class GenieMessageStateTests
{
    // The ten values read from the generated Databricks SDK. If Databricks adds an eleventh,
    // this test still passes and the new value maps to Unknown, which is the intended
    // behaviour rather than an oversight.
    [Theory]
    [InlineData("SUBMITTED", GenieMessageState.Submitted)]
    [InlineData("FILTERING_CONTEXT", GenieMessageState.Thinking)]
    [InlineData("FETCHING_METADATA", GenieMessageState.Thinking)]
    [InlineData("ASKING_AI", GenieMessageState.Thinking)]
    [InlineData("PENDING_WAREHOUSE", GenieMessageState.PendingWarehouse)]
    [InlineData("EXECUTING_QUERY", GenieMessageState.ExecutingQuery)]
    [InlineData("COMPLETED", GenieMessageState.Completed)]
    [InlineData("FAILED", GenieMessageState.Failed)]
    [InlineData("CANCELLED", GenieMessageState.Cancelled)]
    [InlineData("QUERY_RESULT_EXPIRED", GenieMessageState.QueryResultExpired)]
    public void Maps_every_documented_platform_status(string wire, GenieMessageState expected) =>
        GenieMessageStateExtensions.FromWire(wire).ShouldBe(expected);

    [Theory]
    [InlineData("SOMETHING_DATABRICKS_ADDED_LATER")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("completed")] // the API is uppercase; casing is not normalised for us
    public void Maps_anything_unrecognised_to_Unknown(string? wire) =>
        GenieMessageStateExtensions.FromWire(wire).ShouldBe(GenieMessageState.Unknown);

    [Theory]
    [InlineData(GenieMessageState.Completed)]
    [InlineData(GenieMessageState.Failed)]
    [InlineData(GenieMessageState.Cancelled)]
    [InlineData(GenieMessageState.QueryResultExpired)]
    public void Terminal_states_stop_polling(GenieMessageState state) =>
        state.IsTerminal().ShouldBeTrue();

    [Theory]
    [InlineData(GenieMessageState.Submitted)]
    [InlineData(GenieMessageState.Thinking)]
    [InlineData(GenieMessageState.PendingWarehouse)]
    [InlineData(GenieMessageState.ExecutingQuery)]
    public void Non_terminal_states_continue_polling(GenieMessageState state) =>
        state.IsTerminal().ShouldBeFalse();

    // An unrecognised status is far more likely to be a new intermediate step than a new
    // terminal one. Treating it as terminal would silently truncate a working conversation
    // and return an empty answer as if it were complete.
    [Fact]
    public void Unknown_is_not_terminal() =>
        GenieMessageState.Unknown.IsTerminal().ShouldBeFalse();

    [Fact]
    public void Every_state_has_progress_text()
    {
        foreach (var state in Enum.GetValues<GenieMessageState>())
        {
            state.ToProgressDescription().ShouldNotBeNullOrWhiteSpace();
        }
    }
}
