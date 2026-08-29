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
| macOS arm64 | Unit and contract tests run in CI (since the `macos-latest` matrix entry was added) |

## Clouds

| Cloud | Status |
|---|---|
| Azure Databricks | **Verified against a live workspace on 2026-08-01** — see below |
| AWS Databricks | Not tested — [#51](https://github.com/ivanvyd/LakeSpeak.NET/issues/51). The OAuth M2M and PAT paths are contract-tested against a stubbed token endpoint and a stubbed Genie Conversation API, and the wire shape is shared across clouds. **Filing a PR that runs the live suite on an AWS workspace and records the result here is the contribution that closes #51.** |
| GCP Databricks | Not tested — [#52](https://github.com/ivanvyd/LakeSpeak.NET/issues/52). Same shape as AWS. **The contribution that closes #52 is a PR that runs the live suite on a GCP workspace and records the result here.** |

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

What this still does **not** exercise: the Genie full-result download endpoints and visualizations.
Those remain covered by contract tests only.

## Re-executing an expired result — 2026-08-06

Exercising `execute-query` against the live workspace corrected a wire assumption that a contract
test had encoded wrongly, and with it a real defect.

| Call | Response |
|---|---|
| `POST …/attachments/{id}/execute-query` | HTTP 200, `state: PENDING`, **no manifest, no rows** |
| `GET …/attachments/{id}/query-result` moments later | `state: SUCCEEDED`, rows present |

`execute-query` only *starts* the re-execution. The client returned that first acknowledgement, so
`ReExecuteQueryAsync` produced `null` and `export last` told the user to ask the question again —
while the warehouse work they had just paid for completed and was discarded. It now polls for the
rows.

The contract test covering this stubbed a *completed* response, which Databricks does not return.
A stub cannot catch an error in the stub, which is the general lesson and the reason
`A_re_executed_query_returns_its_rows` is a **live** test rather than another fixture.

Still unreached: the `QUERY_RESULT_EXPIRED` state that triggers recovery. Databricks expires the
cache on its own schedule, hours later, and there is no way to force it — so the recovery is
verified, and the condition it recovers from is simulated.

## `chat`, verified live — 2026-08-06

The one path that resists automation entirely: the REPL refuses to start without an interactive
terminal, by design, so no CI job or agent session can drive it. It was run by hand instead.

| Path | Result |
|---|---|
| REPL start against a live Agent | Banner, Agent name, prompt |
| A question | Answer text plus a rendered result table |
| **A follow-up** — "and break that down by quarter" | Kept its context and re-grouped the same figures by quarter. This is the behaviour the whole project is built around, and it had never been observed end to end before |
| `/sql` | Printed the generated SQL for the follow-up, not the first question |
| `/exit` | Clean exit |

`docs/assets/transcripts/chat.txt` is that session, with the workspace's identifiers replaced by
the synthetic ones the other transcripts use. It was previously reconstructed from string
literals; it is now captured output like every other transcript here.

Not exercised: `/new`, `/agents`, `/use`, `/result`, `/export`, `/thumbs-up`, `/thumbs-down`.

## Local verification — 2026-08-05

Not a live workspace. Recorded because the packaged surface is the only surface a user meets, and
"it built" is not evidence that it packaged.

| Check | Result |
|---|---|
| Full suite, `Category!=Live` | 224 tests, 0 failed, across five projects |
| Build under warnings-as-errors | Clean |
| `dotnet format --verify-no-changes` | Clean |
| `dotnet restore --locked-mode` | Succeeds; no lock file drift |
| Packaged tool installed to an isolated `--tool-path` | `lakespeak --version` → `0.1.0+b934d6b…`, matching the merge commit |
| Throwaway consumer against `LakeSpeak.Genie.0.1.0.nupkg` | Compiles and reads the new `MaxResultRows`, default `100000` — proving the new public member is in the *package*, not just the build output |

## Chunked result assembly — mechanism verified live, 2026-08-05

The client follows `next_chunk_internal_link` to assemble a chunked result
([ADR 0004](decisions/0004-complete-a-chunked-result-by-following-the-link-databricks-supplies.md)).
Every assumption that design rests on was checked against the live Azure workspace.

**The permission question is answered: yes.** This was the named unknown — whether a caller may
read the remaining chunks of a statement Genie executed on their behalf, given the link resolves to
`/api/2.0/sql/statements/…`, which is not a Genie path.

| Probe | Result |
|---|---|
| Genie `start-conversation` → completed message → `query.statement_id` | `01f19102-25bf-…` |
| `GET /api/2.0/sql/statements/{that id}` with the caller's own token | **HTTP 200**, manifest, `SUCCEEDED` |
| `GET .../result/chunks/0` for that Genie-executed statement | **HTTP 200**, rows returned |

**The wire contract behaves as the client assumes.** A deliberately chunked statement
(`SELECT id, repeat('x',120) FROM range(60000)`, run through the Statement Execution API so no
table was created):

| Observation | Value |
|---|---|
| `manifest.total_chunk_count` | `2` |
| `manifest.truncated` on a merely-chunked result | **`false`** — the original defect's premise, confirmed rather than assumed |
| Chunk 0 `row_count` | `41250` of `60000` |
| `next_chunk_internal_link` | `/api/2.0/sql/statements/{id}/result/chunks/1` — workspace-relative, the shape the client's host validation accepts |
| Following that link | HTTP 200, `row_offset: 41250`, `row_count: 18750`, no `next_chunk_index` |
| Assembled total | `41250 + 18750 = 60000`, matching `total_row_count` exactly |

That covers the link's shape, the chunk response's shape, the loop's termination condition, and the
row arithmetic the truncation flag depends on.

**What is still not proven:** Genie itself emitting a result large enough to span chunks. The demo
Agent's table has six rows, and Genie generally bounds its own SQL, so the two halves above are each
verified while their composition is not. If Genie never emits a multi-chunk result, this code simply
never engages; if it does, every mechanism it needs has now been shown to work. Tracked as
[#54](https://github.com/ivanvyd/LakeSpeak.NET/issues/54).

Contract tests continue to cover the paths a live run cannot reach on demand: an unreachable chunk,
a repeated link, a link resolving off-workspace, and the row cap.

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
| Chunked result assembly, and every way it can stop short | Contract tests against a stubbed multi-chunk server. The wire contract and the chunk-read permission were verified live on 2026-08-05 — see above; Genie emitting a multi-chunk result was not |
| Documented CLI commands still existing | A test parses every fenced `lakespeak …` example in this repository against the real command tree |
| The packaged public API, as opposed to the build output | A consumer project compiled against the `.nupkg` from `./artifacts`, 2026-08-05 |
| Unattended service-principal auth (OAuth M2M) | Contract tests against a stubbed token endpoint cover: Basic auth, form parameters, response shape, expiry, refresh, `invalid_client` mapping, non-JSON error bodies, and concurrent first-callers sharing one fetch. The exchange with **valid** service-principal credentials against a real workspace has not been run — [#55](https://github.com/ivanvyd/LakeSpeak.NET/issues/55) |
| Against real Databricks | See "Live verification" above. `agents list`, `ask`, every output format, `pack run`, `export last` and `feedback last` were run against a live workspace; `chat`, chunked/external-link results and `QUERY_RESULT_EXPIRED` recovery were not. The live suite is now re-runnable from `Actions → Live smoke (Databricks Genie)` (workflow_dispatch) and weekly (cron) behind `secrets.DATABRICKS_TOKEN`, `vars.LAKESPEAK_LIVE_HOST` and `vars.LAKESPEAK_LIVE_AGENT`; a fork without any of them sees a notice and exits 0. As of 2026-08-29 the workflow has not yet been run against the live workspace — it is documented as code, not as evidence |

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
