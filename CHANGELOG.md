# Changelog

Notable changes, newest first. This project follows [semantic versioning](https://semver.org), with
the caveat that `0.x` minor versions may break the API — that is what `0.x` means, and it is stated
here rather than left to be discovered.

## Unreleased

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

### Notes

Verified against a live Azure Databricks workspace on 2026-08-01: `agents list`, `ask`, every
output format, `pack run`, `export last` and `feedback last`. `chat`, chunked and external-link
results, visualizations and `QUERY_RESULT_EXPIRED` recovery were **not** exercised live and remain
covered by contract tests only. [docs/compatibility.md](docs/compatibility.md) records exactly what
was run against what.
