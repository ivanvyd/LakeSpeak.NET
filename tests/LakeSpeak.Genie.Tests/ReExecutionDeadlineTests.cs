using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LakeSpeak.Genie.Tests;

public sealed class ReExecutionDeadlineTests
{
    private const string Rows = """{"statement_response":{"manifest":{"schema":{"columns":[{"name":"value","type_name":"STRING"}]},"total_row_count":1},"result":{"data_array":[["row"]]}}}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_deadline_cancels_an_in_flight_execution_or_chunk_request(bool chunk)
    {
        var clock = new FakeTimeProvider();
        var handler = new BlockingHandler(chunk);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var task = Client(handler, clock).ReExecuteQueryAsync("agent", "conversation", "message", "attachment", cancellation.Token);
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        try
        {
            clock.Advance(TimeSpan.FromSeconds(10));

            (await task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).ShouldBeNull();
            handler.ObservedCancellation.ShouldBeTrue();
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await task; }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    public async Task A_result_is_accepted_only_before_the_execution_deadline(int seconds, bool accepted)
    {
        var clock = new FakeTimeProvider();
        var handler = new AdvancingHandler(clock, TimeSpan.FromSeconds(seconds));

        var result = await Client(handler, clock).ReExecuteQueryAsync(
            "agent", "conversation", "message", "attachment", TestContext.Current.CancellationToken);

        (result is not null).ShouldBe(accepted);
    }

    [Fact]
    public async Task A_pending_acknowledgement_cannot_extend_the_execution_deadline()
    {
        var clock = new FakeTimeProvider();
        var handler = new PendingHandler(clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var task = Client(handler, clock, pollInterval: TimeSpan.FromSeconds(5)).ReExecuteQueryAsync(
            "agent", "conversation", "message", "attachment", cancellation.Token);
        await handler.FirstResponse.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Yield();

        try
        {
            clock.Advance(TimeSpan.FromSeconds(2));

            (await task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).ShouldBeNull();
            handler.RequestCount.ShouldBe(1);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await task; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Caller_cancellation_remains_cancellation()
    {
        var handler = new BlockingHandler(chunk: false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var task = Client(handler, new FakeTimeProvider()).ReExecuteQueryAsync(
            "agent", "conversation", "message", "attachment", cancellation.Token);
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task The_deadline_cancels_an_in_flight_result_poll()
    {
        var clock = new PollingClock();
        var handler = new BlockingHandler(chunk: false, pendingFirst: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var task = Client(handler, clock).ReExecuteQueryAsync(
            "agent", "conversation", "message", "attachment", cancellation.Token);
        await clock.PollScheduled.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Clock.Advance(TimeSpan.FromSeconds(1));
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        try
        {
            clock.Clock.Advance(TimeSpan.FromSeconds(9));

            (await task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).ShouldBeNull();
            handler.ObservedCancellation.ShouldBeTrue();
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await task; }
            catch (OperationCanceledException) { }
        }
    }

    private static GenieClient Client(HttpMessageHandler handler, TimeProvider clock, TimeSpan? pollInterval = null)
    {
        var host = new Uri("https://example.azuredatabricks.net");
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        return new GenieClient(new HttpClient(handler) { BaseAddress = host }, Options.Create(new GenieClientOptions
        {
            Host = host,
            PollingTimeout = TimeSpan.FromSeconds(10),
            InitialPollInterval = interval,
            MaxPollInterval = interval,
        }), clock);
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class BlockingHandler(bool chunk, bool pendingFirst = false) : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ObservedCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (pendingFirst && request.Method == HttpMethod.Post)
            {
                return Response("""{"statement_response":{"status":{"state":"PENDING"}}}""");
            }
            if (chunk && request.Method == HttpMethod.Post)
            {
                return Response("""{"statement_response":{"manifest":{"schema":{"columns":[{"name":"value","type_name":"STRING"}]},"total_row_count":2},"result":{"data_array":[["first"]],"next_chunk_index":1,"next_chunk_internal_link":"/api/2.0/sql/statements/statement/result/chunks/1"}}}""");
            }

            var wait = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Started.TrySetResult();
            try
            {
                await wait;
                throw new InvalidOperationException("The infinite delay unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
        }
    }

    private sealed class AdvancingHandler(FakeTimeProvider clock, TimeSpan advance) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            clock.Advance(advance);
            return Task.FromResult(Response(Rows));
        }
    }

    private sealed class PendingHandler(FakeTimeProvider clock) : HttpMessageHandler
    {
        internal TaskCompletionSource FirstResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            clock.Advance(TimeSpan.FromSeconds(8));
            FirstResponse.TrySetResult();
            return Task.FromResult(Response("""{"statement_response":{"status":{"state":"PENDING"}}}"""));
        }
    }

    private sealed class PollingClock : TimeProvider
    {
        internal FakeTimeProvider Clock { get; } = new();
        internal TaskCompletionSource PollScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => Clock.GetUtcNow();
        public override long GetTimestamp() => Clock.GetTimestamp();
        public override long TimestampFrequency => Clock.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(1))
            {
                PollScheduled.TrySetResult();
            }
            return timer;
        }
    }
}
