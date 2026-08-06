# Changelog

Notable changes, newest first. This project follows [semantic versioning](https://semver.org), with
the caveat that `0.x` minor versions may break the API — that is what `0.x` means, and it is stated
here rather than left to be discovered.

## 0.1.0 — 2026-08-06

Results come back whole, the documentation's own claims are under test, and the one image that was
reconstructed is now a real session. What has been verified and what has not is recorded in
[docs/compatibility.md](docs/compatibility.md) rather than implied.

`0.x` still means a minor version may break the API.

### Fixed

- **Chunked query results are now assembled in full.** A result split across chunks came back as
  its first chunk — correctly flagged as truncated, but short. The client now follows the
  `next_chunk_internal_link` Databricks supplies until the result is complete. Every way of
  failing to complete one still reports truncation, so a partial result is still never presented
  as whole. The mechanism was verified against a live Azure workspace on 2026-08-05 — including
  the permission question ADR 0004 recorded as unknown, which the answer to is yes — though Genie
  emitting a multi-chunk result was not reproduced. See
  [ADR 0004](docs/decisions/0004-complete-a-chunked-result-by-following-the-link-databricks-supplies.md)
  and [compatibility.md](docs/compatibility.md).

### Security

- **The service-principal recipe no longer puts the client secret in a command-line argument.**
  `curl --user "$ID:$SECRET"` — the form Databricks' own documentation shows — makes the secret
  readable from the process table by anything else on the machine while the request runs. On a
  shared or self-hosted CI runner, which is what the recipe is for, that loses a credential
  masking cannot protect. The credentials are now piped in on stdin. Found by an adversarial
  security review.

### Added

- `GenieClientOptions.MaxResultRows` (default 100,000) bounds the rows assembled from a chunked
  result. Reaching it reports the result as truncated rather than capping it silently. It bounds
  the *walk*, not the rows returned: a single chunk larger than the limit comes back whole, since
  discarding data Databricks already sent would be the worse trade.
- A hard bound of 1,000 chunk requests per result. Neither the row cap nor the repeated-link guard
  stops a response that supplies a fresh link every time while carrying no rows — an adversarial
  review drove 680,307 authenticated requests through that gap before a timeout cut it off.
- A test that parses every `lakespeak …` example in the documentation against the real command
  tree, so a documented command or flag that no longer exists fails the build.
- **A service-principal recipe for unattended runs**, in `docs/authentication.md`. Databricks'
  documented M2M call mints a token that the existing `DATABRICKS_TOKEN` path accepts, so CI works
  today without LakeSpeak becoming a credential broker. Documented, not exercised live.
- **A scheduled GitHub Actions example**, at `examples/github-actions/daily-brief.yml` — a sample
  to copy, not a live workflow in this repository.

### Changed

- **The chat image in the documentation is a real session.** It was reconstructed from string
  literals in `ChatCommand.cs`, because the REPL cannot be piped to a file — a fact disclosed only
  inside the generator script, where nobody reads it. It was captured by hand against a live
  workspace on 2026-08-06, which also verified the REPL, a context-keeping follow-up and `/sql`
  for the first time. `ask.svg`'s SQL box, misaligned since an earlier identifier substitution
  dropped its padding, is fixed at the same time.
- The README no longer states a test count. The number had drifted from 89 to 175 without anyone
  noticing, which is what prose claims do.
- **Positioning corrected.** Databricks CLI v1.10.0 ships `databricks genie ask`, which holds a
  conversation across calls, shows SQL and prints JSON. The README previously argued that this
  project exists because the official CLI makes you manage six identifiers by hand; that argument
  no longer holds. It now leads with the two things that do — there is no Databricks SDK for .NET,
  and Question Packs have no equivalent — and points readers at the official command for a plain
  terminal answer. `ROADMAP.md` named this exact event as one that would narrow the differentiator.
- **ADR 0001 corrected**, without changing its decision. It rejected an MCP server partly on the
  grounds that one would duplicate Databricks' managed endpoints. Those endpoints are stateless, so
  a stateful implementation would not duplicate them. The decision stands on scope instead, which
  was always the real reason. `GOVERNANCE.md` and `ROADMAP.md` updated to match.
- NuGet version badges for both packages, so the README shows what is actually published.

## 0.1.0-preview.1 — 2026-08-01

First published build. A preview: the CLI surface and the `LakeSpeak.Genie` public API may both
change before `0.1.0`, and `0.x` minor versions may break the API.

### Added

- **`LakeSpeak.Genie`** — a client for the Databricks Genie Conversation API: Agent listing with
  pagination, stateful conversations and follow-ups, polling with backoff and cancellation,
  attachment normalization, query results, feedback, and typed failures.
- **Authentication** by brokering short-lived OAuth tokens through the Databricks CLI. No credential
  is persisted, logged, or passed as a process argument.
- **`lakespeak` CLI** — `agents list`, `ask`, `chat`, `auth check`, `config show`, and
  `pack init | validate | run`.
- **Output formats** — text, table, markdown, json, jsonl and csv, with a versioned machine schema,
  results on stdout and diagnostics on stderr.
- **Question Packs** — a published JSON Schema, a strict loader, a sequential runner, and
  deterministic Markdown reports.
- **CI** covering build, cross-platform tests, packing, tool install, CodeQL, Scorecard, secret
  scanning, dependency review, SBOM and build provenance attestation.

### Fixed

- `auth check` constructed the Databricks CLI token provider directly instead of using the one the
  client actually resolves. With `DATABRICKS_TOKEN` set and no Databricks CLI installed — the
  unattended setup the environment provider exists for — it reported failure for a working
  configuration. It now tests the resolved provider and names which one answered.
- A manual release run with `publish` ticked and `version` left blank would have pushed a
  placeholder `0.0.0-dev.<n>` to nuget.org permanently, despite `RELEASING.md` stating that
  version is never published. The workflow now refuses that combination.
- Resolving an Agent by id paged the whole Agent listing to find it, costing one round trip per
  page for the form scripts and Question Packs are told to prefer. Ids are now fetched directly,
  gated on the id shape so resolving by name is unaffected.
- The published Question Pack schema offered `output.format: json`, which the loader rejects.
  The schema now matches the loader.
- The `/quit` chat alias worked but appeared in neither `/help` nor the docs.
- Live integration tests reported **failure** rather than **skip** when no workspace was
  configured, so a bare `dotnet test` gave a contributor without Databricks access 8 red tests.
  They are now gated with `[Fact(SkipUnless = …)]`, which xunit evaluates before constructing the
  class; throwing from the constructor never skipped.
- The release workflow declared a `dry-run` input it never read, and gated publishing on a tag
  push — so a manual run could not publish and the toggle did nothing. Replaced with `version` and
  `publish` inputs that are actually consulted.

### Documentation

- **`docs/getting-started.md`** — a zero-to-answer walkthrough covering what Databricks Genie is,
  that a Genie Agent must already exist and be shared with you, scripting, and the .NET library.
- **`docs/configuration.md`** — the config file's location per platform, its full schema, alias
  precedence and resolution order. Previously the file was referenced across several documents but
  described in none, so its path and YAML shape could only be learned from the source.
- **`RELEASING.md`** — how a release is cut, how to rehearse one without publishing, and what
  cannot be undone.

### Notes

Verified against a live Azure Databricks workspace on 2026-08-01: `agents list`, `ask`, every
output format, `pack run`, `export last` and `feedback last`. `chat`, chunked and external-link
results, visualizations and `QUERY_RESULT_EXPIRED` recovery were **not** exercised live and remain
covered by contract tests only. [docs/compatibility.md](docs/compatibility.md) records exactly what
was run against what.
