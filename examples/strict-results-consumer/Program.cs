using System.Net;
using LakeSpeak.Genie;
using Microsoft.Extensions.Options;

using var handler = new FixtureHandler();
using var http = new HttpClient(handler) { BaseAddress = new Uri("https://offline.example/") };
var client = new GenieClient(http, Options.Create(new GenieClientOptions
{
    Host = http.BaseAddress,
    StrictResults = new GenieResultLimits(MaxRows: 2, MaxBytes: 4096),
}));
var response = await client.AskAsync("agent", "fixture question").ConfigureAwait(false);
if (response.QueryAttachments.Count != 2 || response.QueryAttachments.Any(a => a.Result?.RowCount != 1)
    || !ReferenceEquals(response.Result, response.QueryAttachments[0].Result) || handler.Requests != 4)
{
    throw new InvalidOperationException("Strict candidate fixture failed.");
}
Console.WriteLine($"Strict candidate fixture PASS ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}): 2 attachments, 2 rows, 4 HTTP requests; no live services.");

sealed class FixtureHandler : HttpMessageHandler
{
    internal int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var path = request.RequestUri!.AbsolutePath;
        var body = path switch
        {
            "/api/2.0/genie/spaces/agent/start-conversation" when request.Method == HttpMethod.Post =>
                """{"conversation_id":"conversation","message_id":"message"}""",
            "/api/2.0/genie/spaces/agent/conversations/conversation/messages/message" when request.Method == HttpMethod.Get =>
                """{"status":"COMPLETED","attachments":[{"attachment_id":"a","query":{"query":"select 1"}},{"attachment_id":"b","query":{"query":"select 2"}}]}""",
            "/api/2.0/genie/spaces/agent/conversations/conversation/messages/message/attachments/a/query-result" or
            "/api/2.0/genie/spaces/agent/conversations/conversation/messages/message/attachments/b/query-result" when request.Method == HttpMethod.Get =>
                """{"statement_response":{"status":{"state":"SUCCEEDED"},"manifest":{"schema":{"columns":[{"name":"n"}]},"total_row_count":1},"result":{"data_array":[["1"]]}}}""",
            _ => throw new InvalidOperationException("Unexpected fixture request."),
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
