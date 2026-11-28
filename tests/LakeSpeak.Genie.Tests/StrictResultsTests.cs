using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LakeSpeak.Genie.Tests;

public class StrictResultsTests
{
    private const string Accepted = """{"conversation_id":"conversation","message_id":"message"}""";
    private const string Message = """{"space_id":"other","conversation_id":"other","message_id":"other","status":"COMPLETED","attachments":[{"attachment_id":"a","query":{"query":"select 1"}},{"attachment_id":"b","query":{"query":"select 2"}}]}""";
    private const string TwoQueries = """{"status":"COMPLETED","attachments":[{"attachment_id":"a","query":{}},{"attachment_id":"b","query":{}}]}""";
    private const string Table = """{"statement_response":{"manifest":{"schema":{"columns":[{"name":"n"}]},"total_row_count":2},"result":{"data_array":[["1"],["2"]]}}}""";
    private const string FirstChunk = """{"statement_response":{"manifest":{"schema":{"columns":[{"name":"n"}]},"total_row_count":2},"result":{"data_array":[["1"]],"next_chunk_index":1,"next_chunk_internal_link":"/next"}}}""";
    private const string NextChunk = """{"data_array":[["2"]]}""";
    private const string Limit = GenieErrorCodes.StrictResultLimit;
    private const string Incomplete = GenieErrorCodes.StrictResultIncomplete;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Published_error_codes_keep_their_wire_values()
    {
        GenieErrorCodes.StrictResultLimit.ShouldBe("STRICT_RESULT_LIMIT");
        GenieErrorCodes.StrictResultIncomplete.ShouldBe("STRICT_RESULT_INCOMPLETE");
    }

    [Fact]
    public async Task Legacy_soft_cap_returns_whole_oversized_chunk()
    {
        using var handler = new FixtureHandler(Table);
        var client = Create(handler);
        var result = await client.GetQueryResultAsync("agent", "conversation", "message", "a", Ct);
        result!.RowCount.ShouldBe(2);
        result.IsTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Legacy_keeps_ignoring_previously_unknown_wire_fields()
    {
        using var handler = new FixtureHandler("""{"space_id":123,"conversation_id":"conversation","message_id":"message"}""",
            """{"status":"COMPLETED","attachments":[{"attachment_id":"a","query":{}}]}""",
            Table.Replace("\"statement_response\":{", "\"statement_response\":{\"status\":\"previously ignored\",", StringComparison.Ordinal));
        var response = await Create(handler).AskAsync("agent", "question", cancellationToken: Ct);
        response.Result!.RowCount.ShouldBe(2);
    }

    [Fact]
    public async Task Legacy_masks_identity_and_fetches_only_first_query()
    {
        using var handler = new FixtureHandler(Accepted, Message, Table);
        var response = await Create(handler).AskAsync("agent", "question", cancellationToken: Ct);
        response.AgentId.ShouldBe("agent");
        response.ConversationId.ShouldBe("conversation");
        response.MessageId.ShouldBe("message");
        response.Query!.Sql.ShouldBe("select 1");
        handler.Paths.Count.ShouldBe(3);
        handler.Paths[^1].ShouldContain("/attachments/a/");
    }

    [Theory]
    [InlineData("https://user:secret@fixture.example/next")]
    [InlineData("https://fixture.example/elsewhere/../next")]
    public async Task Legacy_chunk_walk_refuses_userinfo_and_deduplicates_resolved_links(string secondLink)
    {
        var second = "{\"data_array\":[[\"2\"]],\"next_chunk_index\":2,\"next_chunk_internal_link\":\"" + secondLink + "\"}";
        using var handler = new FixtureHandler(FirstChunk.Replace("\"total_row_count\":2", "\"total_row_count\":3", StringComparison.Ordinal), second);
        var result = await Create(handler, maxResultRows: 10).GetQueryResultAsync("agent", "conversation", "message", "a", Ct);
        result!.IsTruncated.ShouldBeTrue();
        result.RowCount.ShouldBe(2);
        handler.Paths.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Strict_fetches_every_attachment_with_first_result_compatibility()
    {
        using var handler = new FixtureHandler(Accepted,
            Message.Replace("other", "agent", StringComparison.Ordinal)
                .Replace("\"conversation_id\":\"agent\"", "\"conversation_id\":\"conversation\"", StringComparison.Ordinal)
                .Replace("\"message_id\":\"agent\"", "\"message_id\":\"message\"", StringComparison.Ordinal), Table, Table);
        var response = await Create(handler, new(4, 4096)).AskAsync("agent", "question", cancellationToken: Ct);
        response.QueryAttachments.Count.ShouldBe(2);
        response.QueryAttachments.Select(a => a.AttachmentId).ShouldBe(["a", "b"]);
        response.QueryAttachments.All(a => a.Result?.RowCount == 2).ShouldBeTrue();
        response.Result.ShouldBeSameAs(response.QueryAttachments[0].Result);
        response.Query.ShouldBeSameAs(response.QueryAttachments[0].Query);
        response.Metadata.AttachmentId.ShouldBe("a");
        handler.Paths.Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("\"space_id\":\"other\"")]
    [InlineData("\"conversation_id\":\"other\"")]
    [InlineData("\"message_id\":\"other\"")]
    [InlineData("\"message_id\":\"message\",\"id\":\"other\"")]
    [InlineData("\"message_id\":\"other\",\"MESSAGE_ID\":\"message\"")]
    [InlineData("\"message_id\":null")]
    [InlineData("\"message_id\":42")]
    [InlineData("\"attachments\":[{\"attachment_id\":\"a\"},{\"attachment_id\":\"a\"}]")]
    [InlineData("\"attachments\":[{\"attachment_id\":7}]")]
    [InlineData("\"attachments\":[null]")]
    [InlineData("\"user_id\":1,\"USER_ID\":2")]
    public async Task Strict_rejects_conflicting_or_duplicate_identity(string properties)
    {
        using var handler = new FixtureHandler("{" + properties + ",\"status\":\"COMPLETED\"}");
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .GetMessageAsync("agent", "conversation", "message", Ct));
        error.Kind.ShouldBe(GenieFailureKind.MalformedResponse);
        error.ToString().ShouldNotContain("other");
    }

    [Fact]
    public async Task Strict_ask_accepts_numeric_non_identity_ids()
    {
        using var handler = new FixtureHandler(
            """{"conversation_id":"conversation","message_id":"message","user_id":1234567890,"conversation":{"user_id":1234567890},"message":{"user_id":1234567890}}""",
            """{"message_id":"message","user_id":1234567890,"warehouse_id":42,"status":"COMPLETED","attachments":[{"attachment_id":"a","query":{}}]}""",
            Table);
        var response = await Create(handler, new(5, 4096)).AskAsync("agent", "question", cancellationToken: Ct);
        response.Result!.RowCount.ShouldBe(2);
    }

    [Fact]
    public async Task Strict_followup_accepts_numeric_non_identity_ids()
    {
        using var handler = new FixtureHandler(
            """{"message_id":"message","user_id":1234567890}""",
            """{"message_id":"message","user_id":1234567890,"status":"COMPLETED"}""");
        var response = await Create(handler, new(5, 4096))
            .FollowUpAsync("agent", "conversation", "question", cancellationToken: Ct);
        response.State.ShouldBe(GenieMessageState.Completed);
    }

    [Fact]
    public async Task Strict_metadata_only_allows_omitted_ids_and_does_not_fetch_results()
    {
        using var handler = new FixtureHandler(Accepted, TwoQueries);
        var response = await Create(handler, new(1, 4096)).AskAsync("agent", "question",
            new GenieAskOptions { IncludeQueryResult = false }, Ct);
        response.QueryAttachments.Count.ShouldBe(2);
        response.QueryAttachments.All(a => a.Result is null).ShouldBeTrue();
        handler.Paths.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Strict_query_attachment_without_id_is_incomplete_not_malformed()
    {
        using var handler = new FixtureHandler(Accepted, """{"status":"COMPLETED","attachments":[{"query":{"query":"select 1"}}]}""");
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .AskAsync("agent", "question", cancellationToken: Ct));
        error.Kind.ShouldBe(GenieFailureKind.UnsupportedResult);
        error.ErrorCode.ShouldBe(Incomplete);
        handler.Paths.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(1, 0)]
    [InlineData(-1, 1024)]
    public void Invalid_strict_options_fail_without_submission(int rows, int bytes)
    {
        using var handler = new FixtureHandler("{}");
        Should.Throw<InvalidOperationException>(() => Create(handler, new(rows, bytes)));
        handler.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task Strict_single_chunk_row_limit_is_hard()
    {
        using var handler = new FixtureHandler(Table);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(1, 4096))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        error.ErrorCode.ShouldBe(Limit);
    }

    [Fact]
    public async Task Strict_rows_aggregate_across_attachments()
    {
        using var handler = new FixtureHandler(Accepted, TwoQueries, Table, Table);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(3, 4096))
            .AskAsync("agent", "question", cancellationToken: Ct));
        error.ErrorCode.ShouldBe(Limit);
        handler.Paths.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Strict_counts_rows_and_bytes_across_chunks()
    {
        using var handler = new FixtureHandler(FirstChunk, NextChunk);
        var result = await Create(handler, new(2, Encoding.UTF8.GetByteCount(FirstChunk + NextChunk)))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct);
        result!.RowCount.ShouldBe(2);
        result.IsTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Strict_rejects_cumulative_bytes_before_next_body_buffering()
    {
        using var handler = new FixtureHandler(FirstChunk, NextChunk);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, Encoding.UTF8.GetByteCount(FirstChunk + NextChunk) - 1))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        error.ErrorCode.ShouldBe(Limit);
    }

    [Theory]
    [InlineData("{\"data_array\":[[\"2\"],[\"3\"]]}", Limit)]
    [InlineData("{\"external_links\":[{\"row_count\":1,\"external_link\":\"https://secret.example/token\"}]}", Incomplete)]
    [InlineData("{\"data_array\":[],\"row_count\":1}", Incomplete)]
    [InlineData("{\"row_count\":1}", Incomplete)]
    [InlineData("{}", Incomplete)]
    [InlineData("broken", null)]
    [InlineData("{\"data_array\":[null]}", null)]
    public async Task Strict_next_chunk_failure_never_returns_partial_success(string chunk, string? code)
    {
        using var handler = new FixtureHandler(FirstChunk, chunk);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(2, 4096))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        error.ErrorCode.ShouldBe(code);
        error.ToString().ShouldNotContain("secret.example");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Strict_rejects_next_chunk_index_that_skips_or_repeats(int nextIndex)
    {
        using var handler = new FixtureHandler(
            FirstChunk.Replace("\"next_chunk_index\":1", "\"next_chunk_index\":" + nextIndex, StringComparison.Ordinal), NextChunk);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        error.Kind.ShouldBe(GenieFailureKind.MalformedResponse);
        handler.Paths.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("{\"data_array\":[]}")]
    [InlineData("{\"row_count\":0}")]
    public async Task Strict_empty_trailing_chunk_completes_an_exhausted_row_budget(string trailing)
    {
        using var handler = new FixtureHandler(
            FirstChunk.Replace("\"total_row_count\":2", "\"total_row_count\":1", StringComparison.Ordinal), trailing);
        var result = await Create(handler, new(1, 4096))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct);
        result!.RowCount.ShouldBe(1);
        result.IsTruncated.ShouldBeFalse();
        handler.Paths.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Strict_non_empty_trailing_chunk_past_an_exhausted_row_budget_is_a_limit()
    {
        using var handler = new FixtureHandler(FirstChunk, NextChunk);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(1, 4096))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        error.ErrorCode.ShouldBe(Limit);
        handler.Paths.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("\"truncated\":true,", "[[\"1\"],[\"2\"]]")]
    [InlineData("", "[[\"1\"]]")]
    public async Task Strict_rejects_server_truncation_and_manifest_shortfalls(string manifest, string rows)
    {
        var body = "{\"statement_response\":{\"manifest\":{" + manifest
            + "\"schema\":{\"columns\":[{\"name\":\"n\"}]},\"total_row_count\":2},\"result\":{\"data_array\":" + rows + "}}}";
        using var handler = new FixtureHandler(body);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        error.ErrorCode.ShouldBe(Incomplete);
    }

    [Fact]
    public async Task Strict_rejects_untrusted_continuation_without_request_or_url_in_error()
    {
        using var handler = new FixtureHandler(FirstChunk.Replace("/next", "https://secret.example/?credential=private", StringComparison.Ordinal));
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        handler.Paths.Count.ShouldBe(1);
        error.ToString().ShouldNotContain("secret.example");
        error.ToString().ShouldNotContain("private");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Strict_bounds_declared_and_unknown_body_lengths_before_deserialization(bool declared)
    {
        using var stream = new CountingStream(Encoding.UTF8.GetBytes(new string('x', 2048)));
        using var content = new StreamContent(stream);
        if (declared)
        {
            content.Headers.ContentLength = 2048;
        }

        using var handler = new ContentHandler(content);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 100))
            .GetMessageAsync("agent", "conversation", "message", Ct));
        error.ErrorCode.ShouldBe(Limit);
        stream.BytesRead.ShouldBe(declared ? 0 : 101);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GenieFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, GenieFailureKind.Authorization)]
    [InlineData(HttpStatusCode.NotFound, GenieFailureKind.AgentNotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, GenieFailureKind.RateLimited)]
    public async Task Strict_http_error_is_classified_by_status_without_reading_an_oversized_body(
        HttpStatusCode status, GenieFailureKind kind)
    {
        using var stream = new CountingStream(Encoding.UTF8.GetBytes(new string('x', 2048)));
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = 2048;
        using var handler = new ContentHandler(content, status);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 100))
            .GetMessageAsync("agent", "conversation", "message", Ct));
        error.Kind.ShouldBe(kind);
        error.StatusCode.ShouldBe((int)status);
        error.ErrorCode.ShouldBeNull();
        stream.BytesRead.ShouldBe(0);
    }

    [Fact]
    public async Task Strict_http_error_is_classified_by_status_when_its_body_cannot_be_read()
    {
        using var stream = new FailingStream();
        using var content = new StreamContent(stream);
        using var handler = new ContentHandler(content, HttpStatusCode.Unauthorized);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .GetMessageAsync("agent", "conversation", "message", Ct));
        error.Kind.ShouldBe(GenieFailureKind.Authentication);
    }

    [Fact]
    public async Task Strict_cancels_during_body_read()
    {
        using var stream = new BlockingStream();
        using var content = new StreamContent(stream);
        using var handler = new ContentHandler(content);
        using var cancellation = new CancellationTokenSource();
        var task = Create(handler, new(5, 4096)).GetMessageAsync("agent", "conversation", "message", cancellation.Token);
        await stream.Started.Task.WaitAsync(Ct);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Strict_reexecute_does_not_swallow_limit_rejection_or_repeat_post()
    {
        using var handler = new FixtureHandler(Table);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(1, 4096))
            .ReExecuteQueryAsync("agent", "conversation", "message", "a", Ct));
        error.ErrorCode.ShouldBe(Limit);
        handler.Paths.Count.ShouldBe(1);
    }

    [Fact]
    public void Duplicate_identity_is_rejected_before_typed_deserialization_without_http()
    {
        using var json = JsonDocument.Parse("""{"message_id":"other","MESSAGE_ID":"message"}""");
        Should.Throw<GenieException>(() => GenieClient.ValidateJsonProperties(json.RootElement))
            .Kind.ShouldBe(GenieFailureKind.MalformedResponse);
    }

    [Fact]
    public async Task Strict_empty_chunks_still_have_a_finite_request_bound()
    {
        using var handler = new EmptyChunkHandler();
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 1_000_000))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        error.ErrorCode.ShouldBe(Limit);
        handler.Requests.ShouldBe(GenieClient.MaxChunkFetches + 1);
    }

    [Fact]
    public async Task Strict_failed_chunk_propagates_http_failure_without_url_or_partial_result()
    {
        using var handler = new FailedChunkHandler();
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        error.Kind.ShouldBe(GenieFailureKind.Authorization);
        error.ToString().ShouldNotContain("credential=private");
    }

    [Theory]
    [InlineData("\"result\":{\"data_array\":[]}")]
    [InlineData("\"result\":{\"row_count\":0}")]
    [InlineData("\"result\":{\"data_array\":[],\"row_count\":0}")]
    [InlineData("\"other\":{}")]
    public async Task Strict_complete_zero_row_result_succeeds(string resultProperty)
    {
        using var handler = new FixtureHandler(
            "{\"statement_response\":{\"manifest\":{\"schema\":{\"columns\":[{\"name\":\"n\"}]},\"total_row_count\":0}," + resultProperty + "}}");
        var result = await Create(handler, new(1, 4096)).GetQueryResultAsync("agent", "conversation", "message", "a", Ct);
        result!.RowCount.ShouldBe(0);
        result.TotalRowCount.ShouldBe(0);
        result.IsTruncated.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"total_row_count\":2")]
    public async Task Strict_missing_result_without_an_explicit_zero_total_is_incomplete(string total)
    {
        using var handler = new FixtureHandler(
            "{\"statement_response\":{\"manifest\":{\"schema\":{\"columns\":[{\"name\":\"n\"}]}" + total + "}}}");
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        error.ErrorCode.ShouldBe(Incomplete);
    }

    [Theory]
    [InlineData("{\"statement_response\":{\"status\":{\"state\":\"FAILED\"}}}")]
    [InlineData("{\"statement_response\":{\"manifest\":{\"schema\":{\"columns\":[]}},\"result\":{\"external_links\":[{}]}}}")]
    public async Task Strict_rejects_failed_statement_or_initial_external_links(string body)
    {
        using var handler = new FixtureHandler(body);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .GetQueryResultAsync("agent", "conversation", "message", "a", Ct));
        error.ErrorCode.ShouldBe(Incomplete);
    }

    [Fact]
    public async Task Strict_bytes_aggregate_across_attachments()
    {
        using var handler = new FixtureHandler(Accepted, TwoQueries, Table, Table);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, Encoding.UTF8.GetByteCount(Table) * 2 - 1))
            .AskAsync("agent", "question", cancellationToken: Ct));
        error.ErrorCode.ShouldBe(Limit);
        handler.Paths.Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("{\"conversation_id\":\"conversation\",\"message_id\":\"message\",\"conversation\":{\"space_id\":\"other\"}}")]
    [InlineData("{\"conversation_id\":\"conversation\",\"message_id\":\"message\",\"message\":{\"id\":\"other\"}}")]
    public async Task Strict_submission_validates_nested_accepted_identity_without_reposting(string body)
    {
        using var handler = new FixtureHandler(body);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .AskAsync("agent", "question", cancellationToken: Ct));
        error.Kind.ShouldBe(GenieFailureKind.MalformedResponse);
        handler.Paths.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Strict_followup_validates_accepted_identity_without_reposting()
    {
        using var handler = new FixtureHandler("""{"conversation_id":"other","message_id":"message"}""");
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .FollowUpAsync("agent", "conversation", "question", cancellationToken: Ct));
        error.Kind.ShouldBe(GenieFailureKind.MalformedResponse);
        handler.Paths.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("COMPLETED", "{\"query_result_metadata\":{\"is_truncated\":true}}", "{}")]
    [InlineData("COMPLETED", "{}", "{}")]
    [InlineData("QUERY_RESULT_EXPIRED", "{}", "{}")]
    public async Task Strict_highlevel_rejects_incomplete_or_unavailable_results(string status, string query, string table)
    {
        using var handler = new FixtureHandler(Accepted,
            "{\"status\":\"" + status + "\",\"attachments\":[{\"attachment_id\":\"a\",\"query\":" + query + "}]}", table);
        var error = await Should.ThrowAsync<GenieException>(() => Create(handler, new(5, 4096))
            .AskAsync("agent", "question", cancellationToken: Ct));
        error.ErrorCode.ShouldBe(Incomplete);
    }

    [Fact]
    public async Task Strict_body_read_has_a_request_deadline()
    {
        using var stream = new BlockingStream();
        using var content = new StreamContent(stream);
        using var handler = new ContentHandler(content);
        var clock = new FakeTimeProvider();
        var task = Create(handler, new(5, 4096), clock).GetMessageAsync("agent", "conversation", "message", Ct);
        await stream.Started.Task.WaitAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(100));
        var error = await Should.ThrowAsync<GenieException>(() => task);
        error.Kind.ShouldBe(GenieFailureKind.Network);
    }

    private sealed class EmptyChunkHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var chunk = $$"""{"data_array":[],"next_chunk_index":{{Requests}},"next_chunk_internal_link":"/chunks/{{Requests}}"}""";
            var body = Requests == 1 ? "{\"statement_response\":{\"manifest\":{\"schema\":{\"columns\":[]}},\"result\":" + chunk + "}}" : chunk;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class FailedChunkHandler : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(++_requests == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FirstChunk) }
                : new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"message":"https://fixture.example/next?credential=private","error_code":"credential=private"}"""),
                });
    }

    private sealed class ContentHandler(HttpContent content, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    private class CountingStream(byte[] bytes) : Stream
    {
        internal int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class BlockingStream() : CountingStream([])
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FailingStream() : CountingStream([])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
    }

    private static GenieClient Create(
        HttpMessageHandler handler, GenieResultLimits? strict = null, TimeProvider? clock = null, int maxResultRows = 1) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://fixture.example/") },
        Options.Create(new GenieClientOptions { Host = new Uri("https://fixture.example/"), MaxResultRows = maxResultRows, StrictResults = strict }), clock);

    private sealed class FixtureHandler(params string[] bodies) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bodies[Math.Min(Paths.Count - 1, bodies.Length - 1)], Encoding.UTF8, "application/json"),
            });
        }
    }
}
