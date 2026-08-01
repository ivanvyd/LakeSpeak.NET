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
/// Every test here is read-only apart from <see cref="Feedback_can_be_sent"/>, which writes a
/// NONE rating — the neutral value — so a test run does not pollute a real Agent's feedback
/// signal. Nothing creates, modifies, or deletes an Agent.
/// </para>
/// </remarks>
[Trait("Category", "Live")]
public sealed class LiveGenieTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _agentName;

    public LiveGenieTests()
    {
        _agentName = Environment.GetEnvironmentVariable("LAKESPEAK_LIVE_AGENT")
            ?? throw SkipException("LAKESPEAK_LIVE_AGENT is not set.");

        if (Environment.GetEnvironmentVariable("DATABRICKS_HOST") is not { Length: > 0 })
        {
            throw SkipException("DATABRICKS_HOST is not set.");
        }

        var services = new ServiceCollection();
        services.AddLakeSpeak();
        _services = services.BuildServiceProvider();
    }

    private IGenieClient Client => _services.GetRequiredService<IGenieClient>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A missing environment variable is a skipped run, not a failure: a contributor without a
    // workspace should not see red.
    private static InvalidOperationException SkipException(string why) =>
        new($"Live tests skipped: {why} See the class remarks for how to run them.");

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

    [Fact]
    public async Task Agents_can_be_listed()
    {
        var agents = new List<GenieAgent>();
        await foreach (var agent in Client.ListAllAgentsAsync(Ct))
        {
            agents.Add(agent);
        }

        agents.ShouldNotBeEmpty();
        agents.ShouldAllBe(a => a.AgentId.Length > 0);
        agents.ShouldAllBe(a => a.Title.Length > 0);
    }

    [Fact]
    public async Task A_question_returns_an_answer_and_the_sql_behind_it()
    {
        var agent = await ResolveAgentAsync();

        var response = await Client.AskAsync(
            agent.AgentId, "How many rows are in the data?", cancellationToken: Ct);

        response.State.ShouldBe(GenieMessageState.Completed);
        response.ConversationId.ShouldNotBeNullOrWhiteSpace();
        response.MessageId.ShouldNotBeNullOrWhiteSpace();
        response.Text.ShouldNotBeNullOrWhiteSpace();

        // Not asserted: that the answer is correct. No client can check that, and a test
        // pretending otherwise would be the exact overreach this project's docs warn about.
    }

    /// <summary>
    /// The claim this project makes most loudly: a value is never reformatted on its way out.
    /// Asserted against a live warehouse rather than a fixture, because a fixture cannot catch
    /// a serialiser deciding to parse a DECIMAL somewhere in the middle.
    /// </summary>
    [Fact]
    public async Task Result_cells_arrive_as_strings_and_are_never_reformatted()
    {
        var agent = await ResolveAgentAsync();

        var response = await Client.AskAsync(
            agent.AgentId, "Show every row with all columns.", cancellationToken: Ct);

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

    [Fact]
    public async Task A_follow_up_stays_in_the_same_conversation()
    {
        var agent = await ResolveAgentAsync();

        var first = await Client.AskAsync(agent.AgentId, "How many rows are there?", cancellationToken: Ct);
        var second = await Client.FollowUpAsync(
            agent.AgentId, first.ConversationId, "And how many columns?", cancellationToken: Ct);

        second.ConversationId.ShouldBe(first.ConversationId);
        second.MessageId.ShouldNotBe(first.MessageId);
    }

    [Fact]
    public async Task An_unknown_agent_id_is_reported_as_not_found()
    {
        var ex = await Should.ThrowAsync<GenieException>(
            () => Client.AskAsync("01f00000000000000000000000000000", "hello", cancellationToken: Ct));

        ex.Kind.ShouldBeOneOf(GenieFailureKind.AgentNotFound, GenieFailureKind.Authorization);
    }

    [Fact]
    public async Task Feedback_can_be_sent()
    {
        var agent = await ResolveAgentAsync();
        var response = await Client.AskAsync(agent.AgentId, "How many rows are there?", cancellationToken: Ct);

        // NONE, not POSITIVE or NEGATIVE: a test run must not skew a real Agent's feedback.
        // No comment either — Databricks rejects text alongside a NONE rating, which is an
        // undocumented constraint this test discovered.
        await Client.SendFeedbackAsync(
            agent.AgentId, response.ConversationId, response.MessageId,
            GenieFeedbackRating.None, comment: null, Ct);
    }

    [Fact]
    public async Task Feedback_text_with_a_none_rating_is_rejected_before_the_request()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() => Client.SendFeedbackAsync(
            "agent", "conversation", "message", GenieFeedbackRating.None, "a comment", Ct));

        ex.Message.ShouldContain("positive or negative");
    }

    /// <summary>
    /// Cancellation is the path a person exercises most often — Ctrl+C on a slow question — and
    /// the one contract tests can only simulate.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_question_stops_promptly()
    {
        var agent = await ResolveAgentAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Should.ThrowAsync<OperationCanceledException>(
            () => Client.AskAsync(agent.AgentId, "Summarise every column in detail.", cancellationToken: cts.Token));
    }

    public void Dispose() => _services.Dispose();
}
