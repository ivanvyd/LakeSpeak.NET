using LakeSpeak.Genie;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace LakeSpeak.ContractTests;

/// <summary>
/// Drives the client against a real HTTP server returning real JSON, so the wire contract is
/// exercised rather than a hand-stubbed interface. Fixtures use the field names read from the
/// generated Databricks SDK; if a name here is wrong, the assertion fails rather than the code
/// silently deserialising an object of nulls.
/// </summary>
public sealed class GenieLifecycleTests : IDisposable
{
    private const string Agent = "01ef-agent";
    private const string Conversation = "01ef-conversation";
    private const string Message = "01ef-message";
    private const string Attachment = "01ef-attachment";

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly FakeTimeProvider _clock = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GenieClient CreateClient(TimeSpan? timeout = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        var options = new GenieClientOptions
        {
            // The server is http, and Validate() rightly refuses a non-https host. Host is only
            // used to build the base address, which is set directly above.
            Host = new Uri("https://example.azuredatabricks.net"),
            InitialPollInterval = TimeSpan.FromSeconds(1),
            MaxPollInterval = TimeSpan.FromSeconds(5),
            PollingTimeout = timeout ?? TimeSpan.FromMinutes(10),
        };

        return new GenieClient(http, Options.Create(options), _clock);
    }

    private void StubStartConversation() =>
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/start-conversation")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                $$"""
                {
                  "conversation_id": "{{Conversation}}",
                  "message_id": "{{Message}}",
                  "conversation": { "space_id": "{{Agent}}", "conversation_id": "{{Conversation}}", "title": "Revenue" }
                }
                """));

    private void StubMessageSequence(params string[] statuses)
    {
        const string scenario = "poll";
        var path = $"/api/2.0/genie/spaces/{Agent}/conversations/{Conversation}/messages/{Message}";

        for (var i = 0; i < statuses.Length; i++)
        {
            var isLast = i == statuses.Length - 1;
            var body = statuses[i] == "COMPLETED" ? CompletedMessage() : PendingMessage(statuses[i]);

            var stub = _server.Given(Request.Create().WithPath(path).UsingGet()).InScenario(scenario);

            // The first stub matches the scenario's initial (unset) state, so it must not call
            // WhenStateIs at all.
            if (i > 0)
            {
                stub = stub.WhenStateIs($"s{i}");
            }

            // The last stub must not advance the state, or it stops matching itself after one
            // response and WireMock starts 404ing. That turned the polling-timeout test into a
            // 404 test without either one failing for the right reason.
            if (!isLast)
            {
                stub = stub.WillSetStateTo($"s{i + 1}");
            }

            stub.RespondWith(Response.Create().WithStatusCode(200).WithBody(body));
        }
    }

    private static string PendingMessage(string status) =>
        $$"""
        {
          "space_id": "{{Agent}}", "conversation_id": "{{Conversation}}",
          "message_id": "{{Message}}", "content": "How did revenue change?",
          "status": "{{status}}"
        }
        """;

    // Two text attachments on purpose: a THINKING phase working note and the real answer. A
    // client that concatenates every text attachment shows the user Genie's scratchpad.
    private static string CompletedMessage() =>
        $$"""
        {
          "space_id": "{{Agent}}", "conversation_id": "{{Conversation}}",
          "message_id": "{{Message}}", "content": "How did revenue change?",
          "status": "COMPLETED",
          "attachments": [
            { "attachment_id": "att-thinking",
              "text": { "content": "Considering which tables to use", "phase": "RESPONSE_PHASE_THINKING" } },
            { "attachment_id": "{{Attachment}}",
              "query": {
                "query": "SELECT country, growth FROM revenue",
                "title": "Revenue by country",
                "statement_id": "stmt-1",
                "query_result_metadata": { "is_truncated": false, "row_count": 2 }
              } },
            { "attachment_id": "att-text",
              "text": { "content": "European revenue increased by 14.2%." } },
            { "attachment_id": "att-suggested",
              "suggested_questions": { "questions": ["Break it down by product"] } }
          ]
        }
        """;

    private void StubQueryResult() =>
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/conversations/{Conversation}/messages/{Message}/attachments/{Attachment}/query-result")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """
                {
                  "statement_response": {
                    "statement_id": "stmt-1",
                    "manifest": {
                      "truncated": false,
                      "total_row_count": 2,
                      "schema": { "columns": [
                        { "name": "country", "type_text": "STRING", "type_name": "STRING", "position": 0 },
                        { "name": "growth", "type_text": "DECIMAL(10,3)", "type_name": "DECIMAL", "position": 1 }
                      ] }
                    },
                    "result": { "row_count": 2, "data_array": [["Germany", "0.142"], [null, null]] }
                  }
                }
                """));

    [Fact]
    public async Task Ask_walks_the_full_lifecycle_and_returns_a_normalized_response()
    {
        StubStartConversation();
        StubMessageSequence("SUBMITTED", "FILTERING_CONTEXT", "EXECUTING_QUERY", "COMPLETED");
        StubQueryResult();

        var client = CreateClient();
        var task = client.AskAsync(Agent, "How did revenue change?", cancellationToken: Ct);
        await AdvanceUntilSettledAsync(task);
        var response = await task;

        response.State.ShouldBe(GenieMessageState.Completed);
        response.ConversationId.ShouldBe(Conversation);

        // The answer, not the working note and not the echoed question.
        response.Text.ShouldBe("European revenue increased by 14.2%.");
        response.Query!.Sql.ShouldBe("SELECT country, growth FROM revenue");
        response.SuggestedQuestions.ShouldBe(["Break it down by product"]);

        response.Result!.Columns.Select(c => c.Name).ShouldBe(["country", "growth"]);
        response.Result.Columns[1].DataType.ShouldBe("DECIMAL(10,3)");
        response.Result.Columns[1].BaseType.ShouldBe("DECIMAL");

        // The decimal stays a string. Parsing it to print it is how a client silently changes
        // someone's revenue figure.
        response.Result.Rows[0].ShouldBe(["Germany", "0.142"]);
        response.Result.Rows[1].ShouldBe([null, null]);
        response.Result.IsTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Reports_every_state_transition_in_order()
    {
        StubStartConversation();
        StubMessageSequence("SUBMITTED", "PENDING_WAREHOUSE", "EXECUTING_QUERY", "COMPLETED");
        StubQueryResult();

        var seen = new List<GenieMessageState>();
        var client = CreateClient();
        var task = client.AskAsync(Agent, "q", new GenieAskOptions
        {
            OnStateChanged = s => seen.Add(s),
        }, Ct);
        await AdvanceUntilSettledAsync(task);
        await task;

        seen.ShouldBe([
            GenieMessageState.Submitted,
            GenieMessageState.PendingWarehouse,
            GenieMessageState.ExecutingQuery,
            GenieMessageState.Completed,
        ]);
    }

    [Fact]
    public async Task A_failed_message_raises_MessageFailed_carrying_the_platform_reason()
    {
        StubStartConversation();
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/conversations/{Conversation}/messages/{Message}")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                $$"""
                {
                  "space_id": "{{Agent}}", "conversation_id": "{{Conversation}}",
                  "message_id": "{{Message}}", "content": "q", "status": "FAILED",
                  "error": { "error": "Table orders does not exist", "type": "SQL_EXECUTION_EXCEPTION" }
                }
                """));

        var client = CreateClient();
        var ex = await Should.ThrowAsync<GenieException>(() => client.AskAsync(Agent, "q", cancellationToken: Ct));

        ex.Kind.ShouldBe(GenieFailureKind.MessageFailed);
        ex.Message.ShouldContain("Table orders does not exist");
        ex.ErrorCode.ShouldBe("SQL_EXECUTION_EXCEPTION");
        ex.IsRetryable.ShouldBeFalse();
    }

    // QUERY_RESULT_EXPIRED is terminal but is not a failure: the answer and SQL are still
    // valid. Throwing here would discard a good answer over a stale cache entry.
    [Fact]
    public async Task An_expired_result_returns_the_answer_rather_than_throwing()
    {
        StubStartConversation();
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/conversations/{Conversation}/messages/{Message}")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                $$"""
                {
                  "space_id": "{{Agent}}", "conversation_id": "{{Conversation}}",
                  "message_id": "{{Message}}", "content": "q", "status": "QUERY_RESULT_EXPIRED",
                  "attachments": [
                    { "attachment_id": "{{Attachment}}", "text": { "content": "Revenue rose 14.2%." } }
                  ]
                }
                """));

        var client = CreateClient();
        var response = await client.AskAsync(Agent, "q", cancellationToken: Ct);

        response.State.ShouldBe(GenieMessageState.QueryResultExpired);
        response.Text.ShouldBe("Revenue rose 14.2%.");
    }

    [Theory]
    [InlineData(401, GenieFailureKind.Authentication, false)]
    [InlineData(403, GenieFailureKind.Authorization, false)]
    [InlineData(404, GenieFailureKind.AgentNotFound, false)]
    [InlineData(429, GenieFailureKind.RateLimited, true)]
    [InlineData(500, GenieFailureKind.Unexpected, false)]
    public async Task Maps_http_failures_to_typed_kinds(int status, GenieFailureKind kind, bool retryable)
    {
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/start-conversation").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(
                """{"error_code":"PERMISSION_DENIED","message":"nope"}"""));

        var client = CreateClient();
        var ex = await Should.ThrowAsync<GenieException>(() => client.AskAsync(Agent, "q", cancellationToken: Ct));

        ex.Kind.ShouldBe(kind);
        ex.StatusCode.ShouldBe(status);
        ex.IsRetryable.ShouldBe(retryable);
    }

    [Fact]
    public async Task A_body_that_is_not_json_fails_as_MalformedResponse_without_echoing_it()
    {
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/start-conversation").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("<html>gateway</html>"));

        var client = CreateClient();
        var ex = await Should.ThrowAsync<GenieException>(() => client.AskAsync(Agent, "q", cancellationToken: Ct));

        ex.Kind.ShouldBe(GenieFailureKind.MalformedResponse);
        // The body can contain query results, which are governed data, and this message may
        // reach a log or a bug report.
        ex.Message.ShouldNotContain("gateway");
    }

    [Fact]
    public async Task Polling_stops_at_the_timeout_and_keeps_the_last_seen_state()
    {
        StubStartConversation();
        StubMessageSequence("EXECUTING_QUERY");

        var client = CreateClient(timeout: TimeSpan.FromSeconds(30));
        var task = client.AskAsync(Agent, "q", cancellationToken: Ct);

        await AdvanceUntilSettledAsync(task);

        var ex = await Should.ThrowAsync<GenieException>(() => task);
        ex.Kind.ShouldBe(GenieFailureKind.PollingTimeout);
        ex.LastKnownResponse!.State.ShouldBe(GenieMessageState.ExecutingQuery);
    }

    [Fact]
    public async Task Cancellation_stops_polling_promptly()
    {
        StubStartConversation();
        StubMessageSequence("EXECUTING_QUERY");

        using var cts = new CancellationTokenSource();
        var client = CreateClient();
        var task = client.AskAsync(Agent, "q", cancellationToken: cts.Token);

        await Task.Yield();
        await cts.CancelAsync();
        _clock.Advance(TimeSpan.FromSeconds(5));

        await Should.ThrowAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Feedback_posts_the_rating_and_tolerates_an_empty_body()
    {
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/conversations/{Conversation}/messages/{Message}/feedback")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(string.Empty));

        var client = CreateClient();
        await client.SendFeedbackAsync(Agent, Conversation, Message, GenieFeedbackRating.Negative, "wrong filter", Ct);

        var request = _server.LogEntries.Single().RequestMessage;
        Assert.NotNull(request);
        Assert.NotNull(request.Body);
        request.Body.ShouldContain("NEGATIVE");
        request.Body.ShouldContain("wrong filter");
    }

    [Fact]
    public async Task Agent_listing_follows_pagination()
    {
        _server.Given(Request.Create().WithPath("/api/2.0/genie/spaces").UsingGet()
                .WithParam("page_token", "p2"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"spaces":[{"space_id":"b","title":"Finance"}]}"""));

        _server.Given(Request.Create().WithPath("/api/2.0/genie/spaces").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"spaces":[{"space_id":"a","title":"Sales"}],"next_page_token":"p2"}"""));

        var client = CreateClient();
        var agents = new List<GenieAgent>();
        await foreach (var agent in client.ListAllAgentsAsync(Ct))
        {
            agents.Add(agent);
        }

        agents.Select(a => a.Title).ShouldBe(["Sales", "Finance"]);
    }

    // WireMock hands back the same token forever here. Without the repeated-token guard this
    // enumerates until the process is killed.
    [Fact]
    public async Task Agent_listing_stops_when_the_server_repeats_a_page_token()
    {
        _server.Given(Request.Create().WithPath("/api/2.0/genie/spaces").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"spaces":[{"space_id":"a","title":"Sales"}],"next_page_token":"same"}"""));

        var client = CreateClient();
        var count = 0;
        await foreach (var _ in client.ListAllAgentsAsync(Ct))
        {
            if (++count > 50)
            {
                throw new InvalidOperationException("Pagination did not terminate.");
            }
        }

        count.ShouldBe(2);
    }

    /// <summary>
    /// Drives the fake clock forward until the polling loop settles, whether it completes or
    /// throws.
    /// </summary>
    /// <remarks>
    /// Bounded by real wall time, not by a fixed iteration count. A counted loop can exhaust
    /// its iterations while an HTTP round trip is still in flight, after which nothing advances
    /// the clock again and the awaiting test hangs forever rather than failing. Falling out of
    /// this loop without the task settling is itself a failure, and is reported as one.
    /// </remarks>
    private async Task AdvanceUntilSettledAsync(Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));

            // Real delay, on the real clock, so the in-flight request to WireMock can land
            // before the next advance.
            await Task.Delay(5, Ct);
        }

        if (!task.IsCompleted)
        {
            throw new TimeoutException(
                "The client did not settle within the real-time budget. Either a stub did not " +
                "match or the polling loop is not observing the fake clock.");
        }
    }

    public void Dispose() => _server.Dispose();
}
