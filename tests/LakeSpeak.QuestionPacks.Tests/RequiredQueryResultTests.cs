using LakeSpeak.Genie;
using NSubstitute;

namespace LakeSpeak.QuestionPacks.Tests;

public sealed class RequiredQueryResultTests
{
    [Theory]
    [InlineData("missing", "behavior.requireQueryResult requires a query result; Genie returned none.")]
    [InlineData("clarification", "behavior.requireQueryResult requires a query result; Genie returned none.")]
    [InlineData("truncated", "behavior.requireQueryResult requires a complete query result; the result is incomplete or truncated.")]
    public async Task Required_results_reject_missing_and_incomplete_answers(string shape, string reason)
    {
        var client = Client(Response(shape));

        var result = await new PackRunner(client).RunAsync(
            Pack(), "agent-1", null, cancellationToken: TestContext.Current.CancellationToken);

        result.FailureCount.ShouldBe(2);
        result.Outcomes.ShouldAllBe(o => !o.Succeeded && o.Failure == reason);
        var markdown = PackReportWriter.WriteMarkdown(result, "unreleased");
        markdown.ShouldContain(reason);
        markdown.ShouldContain("2 of 2 questions failed");
        markdown.ShouldContain("Check the generated SQL");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("clarification")]
    [InlineData("truncated")]
    public async Task Default_behavior_still_accepts_completed_answers(string shape)
    {
        var client = Client(Response(shape));
        var pack = Pack(requireResult: false);

        var result = await new PackRunner(client).RunAsync(
            pack, "agent-1", null, cancellationToken: TestContext.Current.CancellationToken);

        result.AnyFailed.ShouldBeFalse();
        result.Outcomes.ShouldAllBe(o => o.Response != null);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("rows")]
    public async Task Complete_results_succeed_including_zero_rows(string shape)
    {
        var result = await new PackRunner(Client(Response(shape))).RunAsync(
            Pack(), "agent-1", null, cancellationToken: TestContext.Current.CancellationToken);

        result.AnyFailed.ShouldBeFalse();
    }

    [Fact]
    public async Task Continue_records_policy_failure_then_runs_the_next_question()
    {
        var client = Client(Response("missing"));
        client.AskAsync("agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response("empty"));
        var emitted = new List<QuestionOutcome>();

        var summary = await new PackRunner(client).RunStreamingAsync(
            Pack(), "agent-1", null,
            (outcome, _) =>
            {
                emitted.Add(outcome);
                return ValueTask.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        summary.FailureCount.ShouldBe(1);
        emitted.Select(o => o.Question.Id).ShouldBe(["first", "second"]);
        emitted[0].Succeeded.ShouldBeFalse();
        emitted[1].Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    public async Task Stop_on_policy_failure_does_not_emit_or_ask_later_questions(string shape)
    {
        var client = Client(Response(shape));
        var emitted = new List<QuestionOutcome>();

        var error = await Should.ThrowAsync<GenieException>(() => new PackRunner(client).RunStreamingAsync(
            Pack(continueOnFailure: false), "agent-1", null,
            (outcome, _) =>
            {
                emitted.Add(outcome);
                return ValueTask.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken));

        error.Kind.ShouldBe(GenieFailureKind.MessageFailed);
        error.ErrorCode.ShouldBe(shape == "missing" ? "QUERY_RESULT_REQUIRED" : "QUERY_RESULT_INCOMPLETE");
        emitted.ShouldBeEmpty();
        await client.DidNotReceive().AskAsync(
            "agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Strict_questions_remain_sequential_and_request_results()
    {
        var client = Client(Response("empty"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GenieResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AskAsync("agent-1", "first", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                started.SetResult();
                return release.Task;
            });

        var run = new PackRunner(client).RunAsync(
            Pack(), "agent-1", null, cancellationToken: TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await client.DidNotReceive().AskAsync(
            "agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>());
        release.SetResult(Response("empty"));
        (await run).AnyFailed.ShouldBeFalse();
        await client.Received(2).AskAsync(
            "agent-1", Arg.Any<string>(), Arg.Is<GenieAskOptions>(options => options.IncludeQueryResult),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Caller_cancellation_is_not_a_policy_failure(bool continueOnFailure)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var client = Client(Response("missing"));
        client.AskAsync("agent-1", "first", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Response("missing");
            });

        await Should.ThrowAsync<OperationCanceledException>(() => new PackRunner(client).RunAsync(
            Pack(continueOnFailure: continueOnFailure), "agent-1", null,
            cancellationToken: cancellation.Token));

        await client.DidNotReceive().AskAsync(
            "agent-1", "second", Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>());
    }

    private static IGenieClient Client(GenieResponse response)
    {
        var client = Substitute.For<IGenieClient>();
        client.AskAsync("agent-1", Arg.Any<string>(), Arg.Any<GenieAskOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);
        return client;
    }

    private static QuestionPack Pack(bool requireResult = true, bool continueOnFailure = true) =>
        QuestionPackLoader.Parse(
            $$"""
            apiVersion: lakespeak.net/v1alpha1
            kind: QuestionPack
            metadata:
              name: required-results
            spec:
              agent: agent-1
              questions:
                - id: first
                  ask: first
                - id: second
                  ask: second
              behavior:
                continueOnQuestionFailure: {{continueOnFailure.ToString().ToLowerInvariant()}}
                {{(requireResult ? "requireQueryResult: true" : "")}}
            """, ".");

    private static GenieResponse Response(string shape) => new(
        "agent-1", "conversation", "message", GenieMessageState.Completed,
        shape == "clarification" ? "Which quarter do you mean?" : "Answer",
        shape == "missing" ? new GenieQuery("SELECT 1") : null,
        shape is "missing" or "clarification" ? null : new GenieQueryResult(
            [new GenieColumn("count", "INT")],
            shape == "empty" ? [] : [["1"]],
            shape == "truncated", shape == "empty" ? 0 : 1),
        [], new GenieResponseMetadata(TimeSpan.Zero, 1));
}
