using System.Net;
using System.Text;

internal sealed class FixtureHandler : HttpMessageHandler
{
    internal const string Sql = "SELECT count(*) AS order_count FROM orders WHERE order_date = current_date() - 1";
    private const string Root = "/api/2.0/genie/spaces/fixture-agent";
    private const string Message = Root + "/conversations/fixture-conversation/messages/fixture-message";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri?.AbsolutePath;
        string json;
        if (request.Method == HttpMethod.Post && path == Root + "/start-conversation")
        {
            json = """
                {"conversation_id":"fixture-conversation","message_id":"fixture-message"}
                """;
        }
        else if (request.Method == HttpMethod.Get && path == Message)
        {
            json = $$$"""
                {
                  "space_id":"fixture-agent",
                  "conversation_id":"fixture-conversation",
                  "message_id":"fixture-message",
                  "status":"COMPLETED",
                  "attachments":[
                    {"attachment_id":"answer","text":{"content":"There were 42 orders yesterday."}},
                    {"attachment_id":"query","query":{"query":"{{{Sql}}}","statement_id":"fixture-statement"}}
                  ]
                }
                """;
        }
        else if (request.Method == HttpMethod.Get && path == Message + "/attachments/query/query-result")
        {
            json = """
                {
                  "statement_response":{
                    "statement_id":"fixture-statement",
                    "status":{"state":"SUCCEEDED"},
                    "manifest":{
                      "truncated":false,
                      "total_row_count":1,
                      "schema":{"columns":[{"name":"order_count","type_text":"BIGINT","type_name":"LONG","position":0}]}
                    },
                    "result":{"row_count":1,"data_array":[["42"]]}
                  }
                }
                """;
        }
        else
        {
            throw new InvalidOperationException($"Unexpected offline fixture request: {request.Method} {path}");
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }
}
