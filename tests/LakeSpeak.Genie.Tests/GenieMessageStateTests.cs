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
    public void Maps_every_documented_platform_status(string wire, GenieMessageState expected)
    {
        // Arrange — the platform status arrives as the theory parameter.

        // Act
        var state = GenieMessageStateExtensions.FromWire(wire);

        // Assert
        state.ShouldBe(expected);
    }

    [Theory]
    [InlineData("SOMETHING_DATABRICKS_ADDED_LATER")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("completed")] // the API is uppercase; casing is not normalised for us
    public void Maps_anything_unrecognised_to_Unknown(string? wire)
    {
        // Arrange — an unrecognised status arrives as the theory parameter.

        // Act
        var state = GenieMessageStateExtensions.FromWire(wire);

        // Assert
        state.ShouldBe(GenieMessageState.Unknown);
    }

    [Theory]
    [InlineData(GenieMessageState.Completed)]
    [InlineData(GenieMessageState.Failed)]
    [InlineData(GenieMessageState.Cancelled)]
    [InlineData(GenieMessageState.QueryResultExpired)]
    public void Terminal_states_stop_polling(GenieMessageState state)
    {
        // Arrange — the state arrives as the theory parameter.

        // Act
        var terminal = state.IsTerminal();

        // Assert
        terminal.ShouldBeTrue();
    }

    [Theory]
    [InlineData(GenieMessageState.Submitted)]
    [InlineData(GenieMessageState.Thinking)]
    [InlineData(GenieMessageState.PendingWarehouse)]
    [InlineData(GenieMessageState.ExecutingQuery)]
    public void Non_terminal_states_continue_polling(GenieMessageState state)
    {
        // Arrange — the state arrives as the theory parameter.

        // Act
        var terminal = state.IsTerminal();

        // Assert
        terminal.ShouldBeFalse();
    }

    [Fact]
    public void Unknown_is_not_terminal()
    {
        // Arrange — an unrecognised status is far more likely a new intermediate step than a new
        // terminal one; treating it as terminal would truncate a working conversation and return
        // an empty answer as if it were complete.
        const GenieMessageState state = GenieMessageState.Unknown;

        // Act
        var terminal = state.IsTerminal();

        // Assert
        terminal.ShouldBeFalse();
    }

    [Fact]
    public void Does_not_accept_the_SQL_APIs_single_L_spelling()
    {
        // Arrange — Genie spells it CANCELLED; the SQL Statement Execution API spells its own
        // state CANCELED. Feeding the SQL spelling here must not end a poll early.
        const string sqlSpelling = "CANCELED";
        const string genieSpelling = "CANCELLED";

        // Act
        var fromSql = GenieMessageStateExtensions.FromWire(sqlSpelling);
        var fromGenie = GenieMessageStateExtensions.FromWire(genieSpelling);

        // Assert
        fromSql.ShouldBe(GenieMessageState.Unknown);
        fromGenie.ShouldBe(GenieMessageState.Cancelled);
    }

    [Fact]
    public void Tolerates_a_status_that_appears_only_in_documentation()
    {
        // Arrange — published Databricks documentation shows IN_PROGRESS, which exists in no SDK.
        // A client that threw on unrecognised values would compile, pass its mocks, and fail
        // against the real service.
        const string documentedButAbsentFromEverySdk = "IN_PROGRESS";

        // Act
        var state = GenieMessageStateExtensions.FromWire(documentedButAbsentFromEverySdk);

        // Assert
        state.ShouldBe(GenieMessageState.Unknown);
        state.IsTerminal().ShouldBeFalse();
    }

    [Fact]
    public void Every_state_has_progress_text()
    {
        // Arrange
        var states = Enum.GetValues<GenieMessageState>();

        // Act
        var descriptions = states.Select(s => s.ToProgressDescription()).ToList();

        // Assert
        descriptions.ShouldAllBe(d => !string.IsNullOrWhiteSpace(d));
    }
}
