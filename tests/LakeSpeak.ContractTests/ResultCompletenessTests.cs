using LakeSpeak.Genie;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace LakeSpeak.ContractTests;

/// <summary>
/// Two properties that are invisible when they break: a partial result that claims to be
/// complete, and a non-idempotent request that quietly runs twice.
/// </summary>
public sealed class ResultCompletenessTests : IDisposable
{
    private const string Agent = "a";
    private const string Conversation = "c";
    private const string Message = "m";
    private const string Attachment = "att";

    private readonly WireMockServer _server = WireMockServer.Start();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GenieClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        return new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = new Uri("https://example.azuredatabricks.net"),
        }));
    }

    private void StubQueryResult(string resultJson, string manifestExtra = "")
    {
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/conversations/{Conversation}/messages/{Message}/attachments/{Attachment}/query-result")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                $$"""
                {
                  "statement_response": {
                    "manifest": {
                      "truncated": false{{manifestExtra}},
                      "schema": { "columns": [ { "name": "region", "type_text": "STRING" } ] }
                    },
                    "result": {{resultJson}}
                  }
                }
                """));
    }

    private void StubChunk(int index, string body)
    {
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/sql/statements/s1/result/chunks/{index}")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body));
    }

    /// <summary>
    /// A chunk that advertises a successor but carries no link to it leaves the client with no
    /// way to complete the result. <c>manifest.truncated</c> reports statement-level truncation
    /// by Databricks and is false for a merely-chunked result, so relying on it alone hands back
    /// the first chunk labelled complete — a partial export nobody knows is partial.
    /// </summary>
    [Fact]
    public async Task A_chunked_result_with_no_link_to_follow_is_reported_as_truncated()
    {
        // Arrange
        StubQueryResult(
            """{ "row_count": 2, "chunk_index": 0, "next_chunk_index": 1, "data_array": [["Germany"],["France"]] }""");
        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBe(2);
        result.IsTruncated.ShouldBeTrue();
    }

    /// <summary>
    /// The whole point of the change: a result split across chunks comes back whole, in order,
    /// and is not flagged as truncated because nothing is missing.
    /// </summary>
    [Fact]
    public async Task A_chunked_result_is_assembled_across_every_chunk()
    {
        // Arrange — three chunks, each pointing at the next by its documented internal link.
        StubQueryResult(
            """
            {
              "row_count": 1, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"]]
            }
            """,
            manifestExtra: """, "total_row_count": 3""");

        StubChunk(1,
            """
            {
              "row_count": 1, "chunk_index": 1, "next_chunk_index": 2,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/2",
              "data_array": [["France"]]
            }
            """);

        StubChunk(2, """{ "row_count": 1, "chunk_index": 2, "data_array": [["Spain"]] }""");

        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Rows.Select(r => r[0]).ShouldBe(["Germany", "France", "Spain"]);
        result.IsTruncated.ShouldBeFalse();
        result.TotalRowCount.ShouldBe(3);
    }

    /// <summary>
    /// A chunk the caller may not read degrades to the rows already in hand. Genie executes the
    /// statement, so whether the caller can read its remaining chunks is a workspace permission
    /// question — and losing a good answer over a missing tail would be the worse trade.
    /// </summary>
    [Fact]
    public async Task An_unreachable_chunk_returns_the_rows_so_far_and_reports_truncated()
    {
        // Arrange
        StubQueryResult(
            """
            {
              "row_count": 1, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"]]
            }
            """,
            manifestExtra: """, "total_row_count": 2""");

        _server.Given(Request.Create()
                .WithPath("/api/2.0/sql/statements/s1/result/chunks/1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403));

        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBe(1);
        result.IsTruncated.ShouldBeTrue();
    }

    /// <summary>
    /// The chunk link is server-supplied and every workspace request carries the bearer token, so
    /// a link naming another host would disclose that token to it. Both forms below resolve to
    /// <c>evil.example.com</c> — the protocol-relative one despite looking like a path, which is
    /// why the check is on the resolved host rather than the shape of the string.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example.com/api/2.0/sql/statements/s1/result/chunks/1")]
    [InlineData("//evil.example.com/api/2.0/sql/statements/s1/result/chunks/1")]
    public async Task A_next_chunk_link_pointing_off_the_workspace_is_refused(string link)
    {
        // Arrange
        StubQueryResult(
            $$"""
            {
              "row_count": 1, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "{{link}}",
              "data_array": [["Germany"]]
            }
            """,
            manifestExtra: """, "total_row_count": 2""");

        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert — the rows in hand, flagged, and nothing sent off-host.
        result!.Rows.Count.ShouldBe(1);
        result.IsTruncated.ShouldBeTrue();
        var chunkRequests = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path?.Contains("chunks", StringComparison.Ordinal) == true);
        chunkRequests.ShouldBe(0);
    }

    /// <summary>
    /// A chunk that points at itself would spin forever, and a self-pointing chunk carrying no
    /// rows would not even grow toward <c>MaxResultRows</c> — so the row cap is not the backstop
    /// here. This test would hang rather than fail if the guard were removed.
    /// </summary>
    [Fact]
    public async Task A_chunk_link_that_repeats_stops_rather_than_looping()
    {
        // Arrange — chunk one points back at itself and carries no rows.
        StubQueryResult(
            """
            {
              "row_count": 1, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"]]
            }
            """,
            manifestExtra: """, "total_row_count": 9000""");

        StubChunk(1,
            """
            {
              "row_count": 0, "chunk_index": 1, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": []
            }
            """);

        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result!.Rows.Count.ShouldBe(1);
        result.IsTruncated.ShouldBeTrue();
    }

    /// <summary>
    /// Following a chunked result to its end is unbounded work against a billed warehouse's
    /// output. The cap stops it — and says so, rather than reporting a capped result as whole.
    /// </summary>
    [Fact]
    public async Task Reaching_MaxResultRows_stops_the_walk_and_reports_truncated()
    {
        // Arrange — chunk zero already meets the cap, so chunk one is never requested.
        StubQueryResult(
            """
            {
              "row_count": 2, "chunk_index": 0, "next_chunk_index": 1,
              "next_chunk_internal_link": "/api/2.0/sql/statements/s1/result/chunks/1",
              "data_array": [["Germany"],["France"]]
            }
            """,
            manifestExtra: """, "total_row_count": 4""");

        StubChunk(1, """{ "row_count": 2, "chunk_index": 1, "data_array": [["Spain"],["Italy"]] }""");

        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        var client = new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = new Uri("https://example.azuredatabricks.net"),
            MaxResultRows = 2,
        }));

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result!.Rows.Count.ShouldBe(2);
        result.IsTruncated.ShouldBeTrue();
        var chunkRequests = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path?.Contains("chunks", StringComparison.Ordinal) == true);
        chunkRequests.ShouldBe(0);
    }

    [Fact]
    public async Task A_short_read_against_the_manifest_row_count_is_reported_as_truncated()
    {
        // Arrange — the same failure reached a different way: the manifest advertises more rows
        // than arrived.
        StubQueryResult(
            """{ "row_count": 1, "data_array": [["Germany"]] }""",
            manifestExtra: """, "total_row_count": 5000""");
        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result!.IsTruncated.ShouldBeTrue();
        result.TotalRowCount.ShouldBe(5000);
    }

    [Fact]
    public async Task A_complete_single_chunk_result_is_not_reported_as_truncated()
    {
        // Arrange — the conservative truncation check must not false-positive on a whole result.
        StubQueryResult(
            """{ "row_count": 2, "chunk_index": 0, "data_array": [["Germany"],["France"]] }""",
            manifestExtra: """, "total_row_count": 2""");
        var client = CreateClient();

        // Act
        var result = await client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        result!.IsTruncated.ShouldBeFalse();
    }

    /// <summary>
    /// Under EXTERNAL_LINKS disposition the rows sit behind presigned URLs and
    /// <c>data_array</c> is absent. Returning an empty row set as a successful, complete result
    /// would be the worst outcome available — a silently empty export that looks like an answer.
    /// </summary>
    [Fact]
    public async Task An_external_links_result_is_refused_rather_than_returned_empty()
    {
        // Arrange — no data_array, no total_row_count, no next_chunk_index.
        StubQueryResult(
            """{ "external_links": [ { "chunk_index": 0, "row_count": 100000 } ] }""");
        var client = CreateClient();

        // Act
        var act = () => client.GetQueryResultAsync(Agent, Conversation, Message, Attachment, Ct);

        // Assert
        var ex = await Should.ThrowAsync<GenieException>(act);
        ex.Kind.ShouldBe(GenieFailureKind.UnsupportedResult);
        ex.Message.ShouldContain("100000");
    }

    /// <summary>
    /// start-conversation is not idempotent: a retry asks Genie the same question again, running
    /// the SQL warehouse a second time and billing for it, and leaves an orphaned conversation
    /// whose id the caller never receives.
    /// </summary>
    [Fact]
    public async Task A_failed_start_conversation_is_never_retried()
    {
        // Arrange
        _server.Given(Request.Create()
                .WithPath($"/api/2.0/genie/spaces/{Agent}/start-conversation").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503));

        var services = new ServiceCollection();
        services.AddGenieTokenProvider(_ => ValueTask.FromResult("token"));
        services.AddLakeSpeak(o => o.Host = new Uri("https://example.azuredatabricks.net"));
        using var provider = services.BuildServiceProvider();

        // The configured client is pointed at the stub without disturbing the resilience pipeline.
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IGenieClient));
        http.BaseAddress = new Uri(_server.Url!);
        var client = new GenieClient(http, Options.Create(new GenieClientOptions
        {
            Host = new Uri("https://example.azuredatabricks.net"),
        }));

        // Act
        await Should.ThrowAsync<GenieException>(() => client.AskAsync(Agent, "q", cancellationToken: Ct));

        // Assert
        var attempts = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path?.EndsWith("start-conversation", StringComparison.Ordinal) == true);
        attempts.ShouldBe(1);
    }

    public void Dispose() => _server.Dispose();
}
