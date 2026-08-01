# LakeSpeak.NET

[![CI](https://github.com/ivanvyd/lakespeak/actions/workflows/ci.yml/badge.svg)](https://github.com/ivanvyd/lakespeak/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/ivanvyd/lakespeak/badge)](https://scorecard.dev/viewer/?uri=github.com/ivanvyd/lakespeak)

Talk to governed Databricks data from your terminal and .NET applications.

LakeSpeak.NET is an independent open-source .NET client, terminal application, and automation
toolkit for [Databricks Genie](https://docs.databricks.com/aws/en/genie/) Agents.

**New here?** [Getting started](docs/getting-started.md) walks from nothing to a working answer,
including what a Genie Agent is and how to tell whether you have one.

> **Not a Databricks product.** LakeSpeak.NET is an independent open-source community project. It
> is not an official Databricks product, is not endorsed by Databricks, and is not supported under
> any Databricks service-level agreement. "Databricks", "Databricks Genie" and "Unity Catalog" are
> trademarks of Databricks, Inc.

---

## Status

**v0.1.0-preview.1 — first published build.** See [Verification status](#verification-status) for
what has actually been tested and what has not. Nothing here is stable until `v1.0`; the CLI
surface and the `LakeSpeak.Genie` public API may both change, and before `v1.0` a minor version is
allowed to break them.

```bash
dotnet tool install --global LakeSpeak.Cli --prerelease
```

## What this is, in one paragraph

[Databricks Genie](https://docs.databricks.com/aws/en/genie/) answers questions about your data in
plain English: you ask, it writes SQL against tables someone has curated, runs it on a SQL
warehouse, and answers. A **Genie Agent** is one such configured surface. Genie normally lives in
the Databricks web UI — LakeSpeak puts it in your terminal, your scripts, and your .NET code, while
keeping the generated SQL visible so you can check the answer.

You need a Genie Agent to already exist in your workspace and be shared with you. LakeSpeak cannot
create one, and sees only what your own Databricks identity can see.

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

<img src="docs/assets/ask.svg" alt="lakespeak ask printing an answer, a result table, and the SQL Genie generated" width="790">

The generated SQL is shown because the answer is only as trustworthy as the query behind it.

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

Abbreviated for orientation. The complete version — DI registration, `using` directives, reading
the rows, and typed error handling on `GenieFailureKind` — is in
[Getting started → Using it from .NET](docs/getting-started.md#using-it-from-net), and it is
compiled against the library rather than written by hand.

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

See the [Question Pack guide](docs/question-packs.md) for the schema and failure semantics.

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
| Unit and contract tests | 89 tests, run on Windows and Linux in CI |
| Azure Databricks, live workspace | `agents list`, `ask`, `pack run` and every output format verified against a real Genie Agent on 2026-08-01 |
| `chat`, feedback, full-result download, visualizations | Contract tests only — **not** exercised live |
| AWS Databricks | Not tested |
| GCP Databricks | Not tested |
| Windows / Linux | Covered by the CI matrix |
| macOS | Binary is built, not tested |

Paths that have not been exercised against a real workspace are labelled as such in
[docs/compatibility.md](docs/compatibility.md) rather than being quietly presented as working.

## Documentation

- [Getting started](docs/getting-started.md) — zero to a working answer
- [Commands](docs/commands.md) — every command, flag and exit code
- [Configuration](docs/configuration.md) — the config file, aliases and defaults
- [`examples/`](examples/) — a runnable .NET console sample and a complete Question Pack
- [Container image](docs/containers.md) — running a Question Pack on a schedule
- [Authentication](docs/authentication.md) — profiles, environment tokens, and what is not supported
- [Question Packs](docs/question-packs.md) — the schema and its failure semantics
- [Troubleshooting](docs/troubleshooting.md)
- [Limitations](docs/limitations.md) — read this one
- [Decisions](docs/decisions/) — ADRs for the load-bearing choices
- [Genie API surface](docs/planning/genie-api-surface.md) — every wire claim, labelled verified or not
- [SOC 2 control mapping](docs/compliance/soc2-mapping.md)
- [Releasing](RELEASING.md) — how a version is cut, and how to rehearse one

## Contributing

Issues and pull requests are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md), and see
[GOVERNANCE.md](GOVERNANCE.md) for how decisions get made and what is deliberately out of scope.

Good first issues are labelled [`good first issue`](https://github.com/ivanvyd/lakespeak/labels/good%20first%20issue).
Core authentication and security work is not labelled that way, on purpose.

## License

[Apache License 2.0](LICENSE). See [NOTICE](NOTICE).
