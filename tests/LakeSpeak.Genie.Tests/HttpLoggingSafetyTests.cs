using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LakeSpeak.Genie.Tests;

public class HttpLoggingSafetyTests
{
    private const string BearerCanary = "synthetic-bearer-canary";
    private const string SignatureCanary = "synthetic-signature-canary";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_logging_never_records_bearer_headers_or_signed_continuations(bool strict)
    {
        using var capture = new CaptureProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        services.AddGenieTokenProvider(_ => ValueTask.FromResult(BearerCanary));
        services.AddLakeSpeak(options =>
        {
            options.Host = new Uri("https://fixture.example/");
            options.StrictResults = strict ? new GenieResultLimits(2, 4096) : null;
        });
        services.AddHttpClient<IGenieClient, GenieClient>().ConfigurePrimaryHttpMessageHandler(() => new FixtureHandler());
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IGenieClient>();
        await client.WaitForResponseAsync("agent", "conversation", "message", cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.GetQueryResultAsync("agent", "conversation", "message", "a", TestContext.Current.CancellationToken);
        result!.RowCount.ShouldBe(2);
        capture.Entries.ShouldContain(entry => entry.Contains("entered state", StringComparison.Ordinal));
        var logs = string.Join('\n', capture.Entries);
        new[] { BearerCanary, SignatureCanary }.Where(canary => logs.Contains(canary, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization!.Parameter.ShouldBe(BearerCanary);
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/next" => """{"data_array":[["2"]]}""",
                var path when path.EndsWith("/query-result", StringComparison.Ordinal) =>
                    """{"statement_response":{"manifest":{"schema":{"columns":[{"name":"n"}]},"total_row_count":2},"result":{"data_array":[["1"]],"next_chunk_index":1,"next_chunk_internal_link":"/next?download_id_signature=CANARY"}}}"""
                        .Replace("CANARY", SignatureCanary, StringComparison.Ordinal),
                _ => """{"status":"COMPLETED"}""",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class CaptureProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        internal ConcurrentQueue<string> Entries { get; } = new();
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void Dispose() { }

        private sealed class CaptureLogger(CaptureProvider owner) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                owner.Entries.Enqueue(formatter(state, exception));
                owner._scopes.ForEachScope((scope, entries) =>
                {
                    entries.Enqueue(scope?.ToString() ?? string.Empty);
                    if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                    {
                        foreach (var pair in values)
                        {
                            entries.Enqueue($"{pair.Key}: {pair.Value}");
                        }
                    }
                }, owner.Entries);
            }
        }
    }
}
