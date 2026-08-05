# Roadmap

Intent, not commitment. Dates are deliberately absent — this is a solo-maintained project and a
date would be a guess dressed as a plan.

Anything not listed here and not in [GOVERNANCE.md](GOVERNANCE.md) as out of scope is simply
undecided; open an issue and make the case.

## v0.1 — a good terminal and a focused client

Status: in development.

- [x] `LakeSpeak.Genie` client: conversations, follow-ups, polling, attachments, results, feedback
- [x] Token brokering through the Databricks CLI, with no credential persistence
- [x] `agents list`, `ask`, `auth check`, `config show`
- [x] Interactive `chat` with slash commands
- [x] Question Packs: schema, validation, runner, Markdown reports
- [x] Output formats and stable exit codes
- [x] CI, release pipeline, SBOM, provenance
- [x] Verification against a live Azure Databricks workspace
- [x] CLI output tests across terminal widths, unicode, nulls and escape-sequence injection
- [ ] Live coverage for three paths that resist automation — each blocked for a different reason,
      recorded here rather than left as one vague gap:
  - `chat` — the REPL refuses to start without an interactive terminal, by design, so it cannot be
    driven from CI or an agent session. Its underlying follow-up call *is* covered live; the loop
    around it is not. Needs a human at a terminal.
  - Chunked results — assembled by following the link Databricks supplies
    ([ADR 0004](docs/decisions/0004-complete-a-chunked-result-by-following-the-link-databricks-supplies.md)).
    The mechanism was verified live on 2026-08-05, including the permission question that was the
    stated unknown. What remains unreached is Genie *itself* emitting a multi-chunk result: it
    writes its own SQL and generally bounds it, so this needs a Genie Agent over a large table and
    still cannot be forced. Covered by contract tests meanwhile.
  - `QUERY_RESULT_EXPIRED` recovery — the cache expires on Databricks' schedule, hours later. No
    way to force it; covered by contract tests only.
- [x] Command reference and authentication guide

## v0.2 — automation

- OAuth M2M, so unattended use does not depend on a personal access token
- `conversations list` and `resume`
- Shell completion for PowerShell, Bash and Zsh
- A Claude Code skill wrapping the JSON output
- A GitHub Actions example running a Question Pack on a schedule
- AWS Databricks and macOS validation

## v0.3 — packs that do more

- Variables and parameters in Question Packs
- Conditional sections
- Report comparison between runs
- Pluggable exporters
- Conversation aliases

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
