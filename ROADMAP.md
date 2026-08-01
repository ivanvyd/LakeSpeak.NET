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
- [ ] Live coverage for `chat`, chunked results and `QUERY_RESULT_EXPIRED` recovery
- [x] Command reference and authentication guide

## v0.2 — automation

- OAuth M2M, so unattended use does not depend on a personal access token
- Full-result downloads beyond the first page of rows
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

An MCP server mode. Databricks already ships managed MCP endpoints for Genie, and re-exposing `ask`
over MCP would add nothing. It becomes worth reconsidering only if LakeSpeak has something they do
not — Question Packs being the obvious candidate.

## Things that would change the plan

- **An official Databricks .NET SDK.** `LakeSpeak.Genie` would migrate onto it rather than compete
  with it, and this project would keep only the product layer.
- **The official CLI becoming conversational.** The differentiator would narrow to Question Packs
  and the .NET library, and the roadmap would follow.
- **Real users.** Everything above is a guess about what people want. Issues beat guesses.
