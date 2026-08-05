# Changelog

Notable changes, newest first. This project follows [semantic versioning](https://semver.org), with
the caveat that `0.x` minor versions may break the API — that is what `0.x` means, and it is stated
here rather than left to be discovered.

## Unreleased

### Fixed

- **Chunked query results are now assembled in full.** A result split across chunks came back as
  its first chunk — correctly flagged as truncated, but short. The client now follows the
  `next_chunk_internal_link` Databricks supplies until the result is complete. Every way of
  failing to complete one still reports truncation, so a partial result is still never presented
  as whole. Verified by contract tests; **not yet exercised against a live workspace**. See
  [ADR 0004](docs/decisions/0004-complete-a-chunked-result-by-following-the-link-databricks-supplies.md).

### Added

- `GenieClientOptions.MaxResultRows` (default 100,000) bounds the rows assembled from a chunked
  result. Reaching it reports the result as truncated rather than capping it silently.
- A test that parses every `lakespeak …` example in the documentation against the real command
  tree, so a documented command or flag that no longer exists fails the build.

### Changed

- The README no longer states a test count. The number had drifted from 89 to 175 without anyone
  noticing, which is what prose claims do.

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
