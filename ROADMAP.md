# Roadmap

Intent, not commitment. Dates are deliberately absent — this is a solo-maintained project and a
date would be a guess dressed as a plan.

Anything not listed here and not in [GOVERNANCE.md](GOVERNANCE.md) as out of scope is simply
undecided; open an issue and make the case.

Current source milestone: v0.3.1. The NuGet badges in the README identify the latest published
packages.

## v0.1 — a good terminal and a focused client

Status: shipped; one live-verification gap remains below.

- [x] `LakeSpeak.Genie` client: conversations, follow-ups, polling, attachments, results, feedback
- [x] Token brokering through the Databricks CLI, with no credential persistence
- [x] `agents list`, `ask`, `auth check`, `config show`
- [x] Interactive `chat` with slash commands
- [x] Question Packs: schema, validation, runner, Markdown reports
- [x] Output formats and stable exit codes
- [x] CI, release pipeline, SBOM, provenance
- [x] Verification against a live Azure Databricks workspace
- [x] CLI output tests across terminal widths, unicode, nulls and escape-sequence injection
- [ ] Live coverage for paths that resist automation — each blocked for a different reason,
      recorded here rather than left as one vague gap:
  - [x] `chat` core flow — **done, 2026-08-06.** The REPL refuses to start without an interactive
    terminal,
    so it needed a human at one. A live session covering a question, a follow-up that kept its
    context, and `/sql` is recorded in [compatibility.md](docs/compatibility.md) and is now the
    transcript behind the documentation's chat image. Its other slash commands remain uncovered.
  - [x] Chunked results — **done, 2026-09-01.** Assembled by following the link Databricks supplies
    ([ADR 0004](docs/decisions/0004-complete-a-chunked-result-by-following-the-link-databricks-supplies.md)).
    An AWS Genie Agent emitted a four-chunk result; LakeSpeak returned all 1,000 rows without
    truncation. The run also exposed and fixed Genie's omission of the next-chunk link.
  - [ ] `QUERY_RESULT_EXPIRED` recovery — **the recovery call itself is now verified live**
    (2026-08-06), which found that `execute-query` only *starts* the re-execution and the client
    was returning its `PENDING` acknowledgement instead of the rows. What is still unreached is the
    *expiry* that triggers it: the cache expires on Databricks' schedule, hours later. No
    way to force it; covered by contract tests only.
- [x] Command reference and authentication guide

## v0.2 — automation

- [x] OAuth M2M, so unattended use does not depend on a personal access token — the
  `DATABRICKS_CLIENT_ID` + `DATABRICKS_CLIENT_SECRET` path is wired, contract-tested, and verified
  with valid credentials against AWS on 2026-09-01. [#55](https://github.com/ivanvyd/LakeSpeak.NET/issues/55)
  is closed.
- [x] macOS validation — `macos-latest` is in the CI test matrix. The path that closes
  [#53](https://github.com/ivanvyd/LakeSpeak.NET/issues/53) was completed on 2026-08-29; the tool
  and both supported library targets are exercised on the platform
- [x] A GitHub Actions example running a Question Pack on a schedule — the `Live smoke`
  workflow runs the live suite behind a secret, weekly and on demand
- [ ] `conversations list` and `resume`
- [ ] Shell completion for PowerShell, Bash and Zsh
- [ ] A Claude Code skill wrapping the JSON output
- [x] AWS Databricks validation — completed on 2026-08-29 and expanded on 2026-09-01 with
  native M2M and four-chunk composition. [#51](https://github.com/ivanvyd/LakeSpeak.NET/issues/51)
  is closed.

## v0.3 — packs that do more

- [ ] Variables and parameters in Question Packs
- [ ] Conditional sections
- [ ] Report comparison between runs
- [ ] Pluggable exporters
- [ ] Conversation aliases
- [x] **Migrate the test infrastructure to Microsoft Testing Platform** — completed and shipped
  in v0.3.0. `global.json` selects MTP, CI uses its filter/reporting syntax, and ADR 0005 records
  the decision.

## Deliberately deferred

An MCP server mode. Databricks' managed Genie MCP endpoints are stateless, so a stateful LakeSpeak
one — or one exposing Question Packs — would offer something they do not. It is deferred on scope
rather than on duplication: a second product with its own transport and support burden, for one
maintainer. Reconsidered when a real user asks. See
[ADR 0001](docs/decisions/0001-a-cli-and-a-library-rather-than-another-mcp-server.md).

## One of these already happened

**The official CLI became conversational.** Databricks CLI v1.10.0 ships `databricks genie ask`,
which holds a conversation across calls with `-s`, shows SQL with `--include-sql`, and prints JSON.
This page previously listed that as a hypothetical that would narrow the differentiator to Question
Packs and the .NET library. It has occurred, and the plan followed: the README now leads with those
two and points readers at the official command for a plain terminal answer.

Recorded here rather than quietly deleted, because a project that predicts something, is right, and
then says nothing has stopped paying attention.

## Things that would change the plan

- **An official Databricks .NET SDK.** `LakeSpeak.Genie` would migrate onto it rather than compete
  with it, and this project would keep only the product layer. This is the remaining existential
  one.
- **Real users.** Everything above is a guess about what people want. Issues beat guesses.
