# Compatibility

A record of what was tested, against what, and when. Not a support matrix, and not a promise.

An entry appears here only once someone has actually run it. Paths that have not been exercised are
listed as untested rather than assumed to work, and identical API paths across clouds are **not**
evidence that a cloud works.

## Runtime

| LakeSpeak | .NET | Databricks CLI | Genie API | Status |
|---|---|---|---|---|
| 0.1.x | 10.0 | 1.10.0 | `/api/2.0/genie`, Public Preview | In development |

The Genie Conversation API is treated as **Public Preview**. Public Preview was announced
2025-03-11, and no GA announcement was found in the 2026 release notes; if you have a source saying
otherwise, please open an issue. Visualization retrieval is explicitly Beta and is not used.

## Platforms

| Platform | Status |
|---|---|
| Linux x64 | Unit and contract tests run in CI |
| Windows x64 | Unit and contract tests run in CI |
| macOS arm64 | Release binary is built; **untested** |

## Clouds

| Cloud | Status |
|---|---|
| Azure Databricks | **Verified against a live workspace on 2026-08-01** — see below |
| AWS Databricks | Not tested |
| GCP Databricks | Not tested |

## Live verification, 2026-08-01

Run against an Azure Databricks premium workspace in `eastus2`, on a serverless SQL warehouse,
against a Genie Agent over a synthetic revenue table created for the purpose. Authentication was an
Entra ID access token for resource `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d`, supplied through
`DATABRICKS_TOKEN`.

| Path | Result |
|---|---|
| `agents list` (table and json) | Listed the live Agent with its real id |
| `ask` with `--show-sql` | Real answer, real result table, real generated SQL, exit 0 |
| `ask --format json` | Valid UTF-8, versioned schema, on stdout only |
| `ask --format csv` | Clean CSV on stdout with diagnostics on stderr, verified via `2>/dev/null` |
| `ask` against an unknown Agent | Real listing lookup, exit 2 |
| `pack run` | Two questions, Markdown report written, exit 0. One of the two came back as a Genie clarifying question rather than an answer — see [limitations](limitations.md) |
| Decimal fidelity | `4500000.00`, `3350000.50`, `1780000.25` reached CSV, JSON and Markdown byte-identical to what Databricks returned |
| Column types | `DECIMAL(22,2)` — precision and scale preserved, which `type_name` alone would have lost |
| Non-ASCII | `€` intact in a file-written report |
| `export last` | Re-fetched the result from Databricks and wrote correct CSV |
| `feedback last` | Positive rating with a comment accepted |
| Opt-in live suite | 8 tests green, including follow-up conversations, feedback and cancellation |

The live suite in `tests/LakeSpeak.LiveIntegrationTests` reproduces all of this. Run it with
`DATABRICKS_HOST`, `DATABRICKS_TOKEN` and `LAKESPEAK_LIVE_AGENT` set:
`dotnet test -c Release --filter "Category=Live"`.

What this still does **not** exercise: `chat` (needs an interactive terminal), full-result downloads
beyond the first chunk, visualizations, and `QUERY_RESULT_EXPIRED` recovery. Those remain covered by
contract tests only.

## What has been verified, and how

| Area | Evidence |
|---|---|
| Message status mapping, all ten values | Unit tests against the enum read from the generated SDK |
| Conversation lifecycle, polling, backoff, cancellation | Contract tests against a local HTTP server |
| Query result shape; decimals and nulls surviving unchanged | Contract tests |
| Truncation reported distinctly from local row limits | Contract tests and rendering code |
| HTTP failure mapping to typed kinds and exit codes | Contract tests, plus the CLI run by hand |
| Credential redaction, both signature fields | Unit tests using realistic JSON payloads |
| Question Pack validation, including path traversal | Unit tests, plus the CLI run by hand |
| CLI parsing, help, exit codes, `config show` leaking nothing | The built binary run by hand |
| Against real Databricks | See "Live verification" above. `agents list`, `ask`, every output format, `pack run`, `export last` and `feedback last` were run against a live workspace; `chat`, chunked/external-link results and `QUERY_RESULT_EXPIRED` recovery were not. |

## How to update this file

Add a row when you run something, naming what you ran it against. Remove a row if the evidence
stops being true. An entry with no evidence behind it is worse than a missing entry, because it
gets believed.

## Live run — 2026-08-01, second pass

Re-run of the full live suite (8 tests) against the same Azure Databricks workspace after the
Agent-resolution change that fetches an id directly instead of paging the listing. All 8 passed,
which is the point of re-running it: that change alters how every `ask`, `chat` and `pack run`
finds its Agent, and a contract test cannot tell you whether a real workspace agrees.

Still not exercised live, with the reason each resists it, are the three paths listed under v0.1 in
[ROADMAP.md](../ROADMAP.md).
