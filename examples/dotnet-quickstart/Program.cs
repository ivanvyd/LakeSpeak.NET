// A minimal, runnable consumer of LakeSpeak.Genie.
//
//   export DATABRICKS_HOST=https://your-workspace.azuredatabricks.net
//   export DATABRICKS_TOKEN=...              # or omit, to use a Databricks CLI profile
//   dotnet run --project examples/dotnet-quickstart -- <agent-id> "How many rows are there?"
//
// This project is part of the solution so CI builds it: an example that no longer compiles
// against the library is worse than no example, and nothing else would catch the drift.

using LakeSpeak.Genie;
using Microsoft.Extensions.DependencyInjection;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run -- <agent-id> \"<question>\"");
    return 2;
}

var (agentId, question) = (args[0], args[1]);

var services = new ServiceCollection();

// Profile is optional. With none, the host and credentials come from DATABRICKS_HOST /
// DATABRICKS_TOKEN, then .databrickscfg.
services.AddLakeSpeak();

using var provider = services.BuildServiceProvider();

// Configuration is validated here, not on the first call: with no host reachable from the
// options, the environment or .databrickscfg, this throws OptionsValidationException.
var genie = provider.GetRequiredService<IGenieClient>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var response = await genie.AskAsync(agentId, question, cancellationToken: cts.Token);

    Console.WriteLine(response.Text);

    // Always show the SQL. A generated query can be wrong in ways the prose answer hides.
    if (response.Query is { Sql: { } sql })
    {
        Console.WriteLine();
        Console.WriteLine(sql);
    }

    if (response.Result is { } result)
    {
        Console.WriteLine();
        Console.WriteLine(string.Join(" | ", result.Columns.Select(c => c.Name)));

        // Cells are strings exactly as Databricks returned them — a DECIMAL is never
        // round-tripped through a double on its way here. Convert deliberately if you need to.
        foreach (var row in result.Rows)
        {
            Console.WriteLine(string.Join(" | ", row.Select(c => c ?? "NULL")));
        }

        if (result.IsTruncated)
        {
            Console.Error.WriteLine("This result is incomplete — narrow the question.");
        }
    }

    return 0;
}
catch (GenieException ex) when (ex.Kind is GenieFailureKind.Authorization)
{
    // Your identity cannot use that Agent, or cannot read a table behind it. Fixed with a
    // Databricks grant, not in code.
    Console.Error.WriteLine($"not permitted: {ex.Message}");
    return 4;
}
catch (GenieException ex) when (ex.Kind is GenieFailureKind.AgentNotFound)
{
    Console.Error.WriteLine($"no such Agent: {ex.Message}");
    return 5;
}
catch (GenieException ex) when (ex.Kind is GenieFailureKind.PollingTimeout)
{
    // Usually a cold SQL warehouse. LastKnownResponse shows how far it got.
    Console.Error.WriteLine($"timed out in state {ex.LastKnownResponse?.State}");
    return 7;
}
catch (GenieException ex)
{
    Console.Error.WriteLine($"{ex.Kind}: {ex.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled");
    return 7;
}
