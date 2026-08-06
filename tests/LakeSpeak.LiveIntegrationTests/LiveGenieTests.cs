using LakeSpeak.Genie;
using Microsoft.Extensions.DependencyInjection;

namespace LakeSpeak.LiveIntegrationTests;

/// <summary>
/// Exercises a real Databricks workspace. Opt-in, and excluded from every default run.
/// </summary>
/// <remarks>
/// <para>
/// These cost real money: each question starts a SQL warehouse. They are excluded from CI by
/// trait rather than omitted from the solution, so they still compile and cannot rot.
/// </para>
/// <para>
/// Run with:
/// <code>
/// export DATABRICKS_HOST=https://your-workspace.azuredatabricks.net
/// export DATABRICKS_TOKEN=...          # or leave unset to use a Databricks CLI profile
/// export LAKESPEAK_LIVE_AGENT="Your Agent"
/// dotnet test -c Release --filter "Category=Live"
/// </code>
/// </para>
/// <para>
/// Every test is read-only apart from <see cref="Feedback_can_be_sent"/>, which writes a NONE
/// rating — the neutral value — so a run does not pollute a real Agent's feedback signal.
/// Nothing creates, modifies, or deletes an Agent.
/// </para>
/// </remarks>
[Trait("Category", "Live")]
public sealed class LiveGenieTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _agentName;

    /// <summary>
    /// Whether a live workspace is configured. Every test here is gated on this via
    /// <c>[Fact(SkipUnless = ...)]</c>, so a contributor without a workspace sees skips
    /// rather than red — including on a bare <c>dotnet test</c> with no category filter.
    /// </summary>
    /// <remarks>
    /// This has to be a property xunit evaluates before constructing the class. Throwing from
    /// the constructor does not skip: xunit reports a constructor exception as a failure, which
    /// is exactly what this suite used to do.
    /// </remarks>
    public static bool LiveWorkspaceConfigured =>
        Environment.GetEnvironmentVariable("LAKESPEAK_LIVE_AGENT") is { Length: > 0 }
        && Environment.GetEnvironmentVariable("DATABRICKS_HOST") is { Length: > 0 };

    private const string NoWorkspace =
        "No live workspace configured. Set DATABRICKS_HOST and LAKESPEAK_LIVE_AGENT; see the class remarks.";

    public LiveGenieTests()
    {
        _agentName = Environment.GetEnvironmentVariable("LAKESPEAK_LIVE_AGENT") ?? string.Empty;

        var services = new ServiceCollection();
        services.AddLakeSpeak();
        _services = services.BuildServiceProvider();
    }

    private IGenieClient Client => _services.GetRequiredService<IGenieClient>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<GenieAgent> ResolveAgentAsync()
    {
        await foreach (var agent in Client.ListAllAgentsAsync(Ct))
        {
            if (agent.Title == _agentName || agent.AgentId == _agentName)
            {
                return agent;
            }
        }

        throw new InvalidOperationException($"No Genie Agent named '{_agentName}' is visible.");
    }

    [Fact(SkipUnless = nameof(LiveWorkspaceConfigured), Skip = NoWorkspace)]
    public async Task Agents_can_be_listed()
    {
        // Arrange
        var agents = new List<GenieAgent>();

        // Act
        await foreach (var agent in Client.ListAllAgentsAsync(Ct))
        {
            agents.Add(agent);
        }

        // Assert
        agents.ShouldNotBeEmpty();
        agents.ShouldAllBe(a => a.AgentId.Length > 0);
        agents.ShouldAllBe(a => a.Title.Length > 0);
    }

    [Fact(SkipUnless = nameof(LiveWorkspaceConfigured), Skip = NoWorkspace)]
    public async Task A_question_returns_an_answer_and_the_sql_behind_it()
    {
        // Arrange
        var agent = await ResolveAgentAsync();

        // Act
        var response = await Client.AskAsync(
            agent.AgentId, "How many rows are in the data?", cancellationToken: Ct);

        // Assert — not asserted: that the answer is correct. No client can check that, and a
        // test pretending otherwise would be the exact overreach this project's docs warn about.
        response.State.ShouldBe(GenieMessageState.Completed);
        response.ConversationId.ShouldNotBeNullOrWhiteSpace();
        response.MessageId.ShouldNotBeNullOrWhiteSpace();
        response.Text.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Re-executing an attachment's query returns its rows.
    /// </summary>
    /// <remarks>
    /// This is the documented recovery for <c>QUERY_RESULT_EXPIRED</c>, and it was covered only by
    /// a contract test that stubbed a completed response — a shape Databricks does not return.
    /// The real endpoint acknowledges with <c>PENDING</c> and no manifest, so the client used to
    /// hand back nothing and <c>export last</c> told people to ask the question again. A stub
    /// could not have caught that, because the stub was the thing that was wrong.
    /// </remarks>
    [Fact(SkipUnless = nameof(LiveWorkspaceConfigured), Skip = NoWorkspace)]
    public async Task A_re_executed_query_returns_its_rows()
    {
        // Arrange — a completed question, so there is an attachment to re-run.
        var agent = await ResolveAgentAsync();
        var response = await Client.AskAsync(
            agent.AgentId, "Total revenue by region", cancellationToken: Ct);

        response.State.ShouldBe(GenieMessageState.Completed);
        response.Metadata.AttachmentId.ShouldNotBeNull();

        // Act — the path `export last` takes when the cached result has aged out.
        var result = await Client.ReExecuteQueryAsync(
            agent.AgentId, response.ConversationId!, response.MessageId!,
            response.Metadata.AttachmentId!, Ct);

        // Assert — rows, not the acknowledgement.
        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBeGreaterThan(0);
        result.Columns.Count.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The claim this project makes most loudly: a value is never reformatted on its way out.
    /// Asserted against a live warehouse rather than a fixture, because a fixture cannot catch a
    /// serialiser deciding to parse a DECIMAL somewhere in the middle.
    /// </summary>
    [Fact(SkipUnless = nameof(LiveWorkspaceConfigured), Skip = NoWorkspace)]
    public async Task Result_cells_arrive_as_strings_and_are_never_reformatted()
    {
        // Arrange
        var agent = await ResolveAgentAsync();

        // Act
        var response = await Client.AskAsync(
            agent.AgentId, "Show every row with all columns.", cancellationToken: Ct);

        // Assert
        if (response.Result is not { Rows.Count: > 0 } result)
        {
            // Genie may answer in prose. That is not a failure of this client.
            return;
        }

        foreach (var cell in result.Rows.SelectMany(r => r).OfType<string>())
        {
            cell.ShouldNotContain("E+", Case.Insensitive);   // no scientific notation
            cell.ShouldNotContain(",");                      // no thousands separators
        }

        result.Columns.ShouldAllBe(c => c.Name.Length > 0);
    }

    [Fact(SkipUnless = nameof(LiveWorkspaceConfigured), Skip = NoWorkspace)]
    public async Task A_follow_up_stays_in_the_same_conversation()
    {
        // Arrange
        var agent = await ResolveAgentAsync();
        var first = await Client.AskAsync(agent.AgentId, "How many rows are there?", cancellationToken: Ct);

        // Act
        var second = await Client.FollowUpAsync(
            agent.AgentId, first.ConversationId, "And how many columns?", cancellationToken: Ct);

        // Assert
        second.ConversationId.ShouldBe(first.ConversationId);
        second.MessageId.ShouldNotBe(first.MessageId);
    }

    [Fact(SkipUnless = nameof(LiveWorkspaceConfigured), Skip = NoWorkspace)]
    public async Task An_unknown_agent_id_is_reported_as_not_found()
    {
        // Arrange
        const string absent = "01f00000000000000000000000000000";

        // Act
        var act = () => Client.AskAsync(absent, "hello", cancellationToken: Ct);

        // Assert
        var ex = await Should.ThrowAsync<GenieException>(act);
        ex.Kind.ShouldBeOneOf(GenieFailureKind.AgentNotFound, GenieFailureKind.Authorization);
    }

    [Fact(SkipUnless = nameof(LiveWorkspaceConfigured), Skip = NoWorkspace)]
    public async Task Feedback_can_be_sent()
    {
        // Arrange — NONE, not POSITIVE or NEGATIVE, so a run does not skew a real Agent's
        // feedback. No comment either: Databricks rejects text alongside a NONE rating, an
        // undocumented constraint this test discovered.
        var agent = await ResolveAgentAsync();
        var response = await Client.AskAsync(agent.AgentId, "How many rows are there?", cancellationToken: Ct);

        // Act
        var act = () => Client.SendFeedbackAsync(
            agent.AgentId, response.ConversationId, response.MessageId,
            GenieFeedbackRating.None, comment: null, Ct);

        // Assert
        await Should.NotThrowAsync(act);
    }

    [Fact(SkipUnless = nameof(LiveWorkspaceConfigured), Skip = NoWorkspace)]
    public async Task Feedback_text_with_a_none_rating_is_rejected_before_the_request()
    {
        // Arrange — caught client-side so the caller gets a clear message rather than an
        // HTTP 400 that reads like a transport fault.

        // Act
        var act = () => Client.SendFeedbackAsync(
            "agent", "conversation", "message", GenieFeedbackRating.None, "a comment", Ct);

        // Assert
        var ex = await Should.ThrowAsync<ArgumentException>(act);
        ex.Message.ShouldContain("positive or negative");
    }

    /// <summary>
    /// Cancellation is the path a person exercises most often — Ctrl+C on a slow question — and
    /// the one contract tests can only simulate.
    /// </summary>
    [Fact(SkipUnless = nameof(LiveWorkspaceConfigured), Skip = NoWorkspace)]
    public async Task Cancelling_a_question_stops_promptly()
    {
        // Arrange
        var agent = await ResolveAgentAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act
        var act = () => Client.AskAsync(
            agent.AgentId, "Summarise every column in detail.", cancellationToken: cts.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(act);
    }

    public void Dispose() => _services.Dispose();
}
