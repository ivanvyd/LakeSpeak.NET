using LakeSpeak.Genie;
using Microsoft.Extensions.DependencyInjection;

var fixture = args is ["--fixture"];
if (!fixture && args.Length != 3)
{
    Console.Error.WriteLine("usage: dotnet run -- --fixture | <profile> <agent-id> \"<question>\"");
    return 2;
}

var services = new ServiceCollection();
if (fixture)
{
    Console.WriteLine("OFFLINE HTTP fixture: no workspace, credentials, or Databricks CLI calls.");
    services.AddGenieTokenProvider(_ => ValueTask.FromResult("offline-fixture"));
    services.AddLakeSpeak(options => options.Host = new Uri("https://offline.example/"));
    services.AddHttpClient<IGenieClient, GenieClient>()
        .ConfigurePrimaryHttpMessageHandler(() => new FixtureHandler());
}
else
{
    services.AddDatabricksCliAuthentication(args[0]);
    services.AddLakeSpeak(options => options.Profile = args[0]);
}

using var provider = services.BuildServiceProvider();
var genie = provider.GetRequiredService<IGenieClient>();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    var response = await genie.AskAsync(
        fixture ? "fixture-agent" : args[1],
        fixture ? "How many orders were placed yesterday?" : args[2],
        cancellationToken: cancellation.Token);

    Console.WriteLine(response.Text);
    if (response.Query?.Sql is { } sql)
    {
        Console.WriteLine($"SQL: {sql}");
    }

    if (response.Result is { } result)
    {
        Console.WriteLine(string.Join(" | ", result.Columns.Select(column => column.Name)));
        foreach (var row in result.Rows)
        {
            Console.WriteLine(string.Join(" | ", row.Select(cell => cell ?? "NULL")));
        }

        if (result.IsTruncated)
        {
            Console.Error.WriteLine("Incomplete result: narrow the question before using these rows.");
            return 1;
        }
    }

    if (fixture && (response.Query?.Sql != FixtureHandler.Sql
        || response.Result is not { RowCount: 1, IsTruncated: false } table
        || table.Rows[0][0] != "42"))
    {
        Console.Error.WriteLine("OFFLINE fixture failed: expected generated SQL and one complete row containing 42.");
        return 1;
    }

    if (fixture)
    {
        Console.WriteLine("OFFLINE fixture PASS (published LakeSpeak.Genie 0.3.5).");
    }

    return 0;
}
catch (GenieException error)
{
    Console.Error.WriteLine($"{error.Kind}: {error.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 1;
}
