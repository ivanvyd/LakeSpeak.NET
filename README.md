# LakeSpeak.NET

Talk to governed Databricks data from your terminal and .NET applications.

LakeSpeak.NET is an independent open-source .NET client, terminal application, and automation
toolkit for [Databricks Genie](https://docs.databricks.com/aws/en/genie/) Agents.

> **Not a Databricks product.** LakeSpeak.NET is an independent open-source community project. It
> is not an official Databricks product, is not endorsed by Databricks, and is not supported under
> any Databricks service-level agreement. "Databricks", "Databricks Genie" and "Unity Catalog" are
> trademarks of Databricks, Inc.

---

## Status

**v0.1 development. Not yet released.** See [Verification status](#verification-status) for what has
actually been tested and what has not. Nothing here is stable until `v1.0`; the CLI surface and the
`LakeSpeak.Genie` public API may both change.

## Why this exists

The Genie Conversation API is capable, and the official `databricks genie` CLI exposes it. But using
it means managing Agent ids, conversation ids, message ids, attachment ids, a polling loop, and a
two-step download workflow yourself:

```bash
databricks genie list-spaces
databricks genie start-conversation <space-id> "question"
databricks genie get-message <space-id> <conversation-id> <message-id>
databricks genie get-message-attachment-query-result ...
databricks genie generate-download-full-query-result ...
databricks genie get-download-full-query-result ...
```

LakeSpeak turns that into:

```bash
lakespeak ask --agent sales "How did revenue change last month?"
```

It is deliberately narrow. It is not a Databricks SDK for .NET, not a replacement for the official
CLI, and not another Genie MCP server — Databricks already ships [managed MCP endpoints for
Genie](https://docs.databricks.com/aws/en/generative-ai/mcp/), and duplicating them would add
nothing. What is missing is a good product experience for stateful conversations, and that is the
gap this fills. See [docs/decisions](docs/decisions/) for the reasoning.

## Install

```bash
dotnet tool install --global LakeSpeak.Cli
```

Or add the client to a .NET project:

```bash
dotnet add package LakeSpeak.Genie
```

## Quick start

LakeSpeak uses your existing Databricks CLI login rather than asking for a token of its own:

```bash
databricks auth login --profile company
lakespeak chat --profile company
```

Ask a one-shot question:

```bash
lakespeak ask --agent sales "Who were our five fastest-growing customers?"
```

Get machine-readable output for scripts and coding agents:

```bash
lakespeak ask --agent finance --format json "What was recognized revenue yesterday?"
```

```powershell
$r = lakespeak ask --agent finance --format json "Revenue yesterday?" | ConvertFrom-Json
$r.result.rows
```

## In .NET

```csharp
services.AddLakeSpeak(options => options.Profile = "production");

var response = await genie.AskAsync(
    agentId: salesAgentId,
    question: "Which customers had the largest revenue decline?",
    cancellationToken);

Console.WriteLine(response.Text);

if (response.Query is not null)
{
    Console.WriteLine(response.Query.Sql);
}
```

## Question Packs

A Question Pack turns a set of business questions into a reviewable, version-controlled report.

```yaml
apiVersion: lakespeak.dev/v1alpha1
kind: QuestionPack
metadata:
  name: daily-platform-brief
spec:
  agent: platform-operations
  questions:
    - id: failed-jobs
      title: Failed production jobs
      ask: Which production jobs failed during the last 24 hours?
  output:
    format: markdown
    path: reports/daily-platform-brief.md
```

```bash
lakespeak pack run daily-platform-brief.yaml
```

See the [Question Pack guide](docs/question-packs/) for the schema and failure semantics.

## What it does not do

- It does not bypass Unity Catalog. You see exactly what your Databricks identity is permitted to
  see, and LakeSpeak has no way to widen that.
- It does not guarantee that a Genie answer is correct. Natural-language querying produces
  generated SQL, and generated SQL can be wrong in ways that read as plausible. LakeSpeak preserves
  the SQL, the source metadata and the message ids precisely so you can check.
- It does not execute arbitrary SQL, edit generated SQL, or modify Genie Agent definitions.
- It does not store your questions, answers, or query results anywhere except files you explicitly
  export.

## Security and privacy

LakeSpeak never persists an access token. It brokers short-lived OAuth tokens through the Databricks
CLI and holds them in memory for the life of the process.

Your questions and the answers you get back can contain sensitive business information, and exported
results contain governed data. Once you export a CSV, that file is yours to look after. In CI,
remember that job logs are usually readable by everyone with repository access.

Report vulnerabilities privately — see [SECURITY.md](SECURITY.md). For how the project's controls
map to the SOC 2 Trust Services Criteria, and the several places they deliberately stop, see
[docs/compliance/soc2-mapping.md](docs/compliance/soc2-mapping.md).

## Verification status

This table records what has been run against what. It is not a support matrix and it is not a
promise; it is a record of evidence.

| Area | Status |
|---|---|
| Unit and contract tests (mocked) | See CI |
| Azure Databricks, live workspace | See [docs/compatibility.md](docs/compatibility.md) |
| AWS Databricks | Not tested |
| GCP Databricks | Not tested |
| Windows / Linux | Covered by the CI matrix |
| macOS | Binary is built, not tested |

Paths that have not been exercised against a real workspace are labelled as such in
[docs/compatibility.md](docs/compatibility.md) rather than being quietly presented as working.

## Documentation

- [Concepts](docs/concepts/) — Agents, conversations, attachments
- [Commands](docs/commands/) — every command and flag
- [Authentication](docs/authentication/) — profiles, U2M, M2M, and what is not supported
- [Question Packs](docs/question-packs/)
- [Architecture](docs/architecture/) and [decisions](docs/decisions/)
- [Troubleshooting](docs/troubleshooting/)
- [Limitations](docs/limitations.md)

## Contributing

Issues and pull requests are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md), and see
[GOVERNANCE.md](GOVERNANCE.md) for how decisions get made and what is deliberately out of scope.

Good first issues are labelled [`good first issue`](https://github.com/ivanvyd/lakespeak/labels/good%20first%20issue).
Core authentication and security work is not labelled that way, on purpose.

## License

[Apache License 2.0](LICENSE). See [NOTICE](NOTICE).
