# Use Genie from a .NET 8 application

This standalone console application restores **published `LakeSpeak.Genie` 0.4.0** from NuGet.
It asks a question through `IGenieClient`, displays generated SQL and rows, and handles typed
failures and cancellation. It has no project reference. The local `Directory.*.props` files stop
repository build and package settings from entering the consumer; `.editorconfig` stops repository
style settings and `global.json` selects .NET 8.

Copy this whole directory into your own workspace, or run it here. Start commands from this
directory so the .NET 8 SDK selection applies. The project is intentionally outside the repository
solution: [`../dotnet-quickstart`](../dotnet-quickstart/) is the source-reference sample that CI
builds against API changes.

## Offline smoke run

Prerequisite: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
Restore needs access to NuGet. Running the fixture needs no Databricks account, CLI or credentials.

```bash
dotnet restore --locked-mode --source https://api.nuget.org/v3/index.json
dotnet build --no-restore
dotnet run --no-build -- --fixture
```

Expected output includes:

```text
OFFLINE HTTP fixture: no workspace, credentials, or Databricks CLI calls.
There were 42 orders yesterday.
SQL: SELECT count(*) AS order_count FROM orders WHERE order_date = current_date() - 1
order_count
42
OFFLINE fixture PASS (published LakeSpeak.Genie 0.4.0).
```

`FixtureHandler` supplies HTTP responses in memory for conversation creation, message polling and
result retrieval. The published client parses them. Unknown requests fail inside the handler;
there is no network fallback. This verifies local integration with a package, not a live workspace
or the correctness of generated SQL.

The checked-in lock file records dependency versions and content hashes for this application.
CI checks this baseline, then repeats the fixture with fresh dependency resolution and a separate
temporary lock file. The two checks cover reproducibility and a normal consumer restore.

## Use .NET 10

The sample retains a .NET 8 consumer floor. To run the same published package on .NET 10,
copy this directory and change `<TargetFramework>` to `net10.0` in the project file. In the
copy's `global.json`, change `sdk.version` to `10.0.100` and keep `rollForward: latestFeature`
and `allowPrerelease: false`. This selects an installed stable .NET 10 SDK; it does not install one.
Keep the package reference at `[0.4.0]`, run `dotnet restore --force-evaluate` once to regenerate the
lock for the changed framework, then repeat the locked restore, build and `--fixture` commands
from the copied directory. Both target routes use the same client API and offline fixture.

## Ask a real Genie Agent

You also need the [Databricks CLI](https://docs.databricks.com/dev-tools/cli/install.html), a
workspace, an existing Genie Agent shared with your identity, and access to its warehouse and
underlying data. LakeSpeak cannot create the Agent or grant access. Use its id from the Genie UI,
not a LakeSpeak CLI alias.

```bash
databricks auth login --host https://your-workspace.cloud.databricks.com --profile my-workspace
dotnet run --no-build -- my-workspace <agent-id> "How many orders were placed yesterday?"
```

These commands send the question to Databricks and can use warehouse compute. The example uses
the package's `AddDatabricksCliAuthentication` and `AddLakeSpeak` APIs; the CLI owns login and token
refresh. Clear `DATABRICKS_HOST` if it points at a different workspace, because environment host
configuration can override the profile's host. See [authentication](../../docs/authentication.md)
for other supported credential sources and unattended OAuth M2M.

Inspect the SQL before relying on an answer. A completed Genie message can contain a clarification
instead of a query result; this application prints that response faithfully. An incomplete result
prints a warning and exits with code 1. The separate Question Pack `requireQueryResult` option,
introduced in **0.4.0**, belongs to the Question Pack CLI; this application uses the Genie SDK.
