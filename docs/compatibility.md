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
| Azure Databricks | **Not yet verified against a live workspace** |
| AWS Databricks | Not tested |
| GCP Databricks | Not tested |

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
| **Anything against real Databricks** | **Not done.** No live workspace has been contacted. |

## How to update this file

Add a row when you run something, naming what you ran it against. Remove a row if the evidence
stops being true. An entry with no evidence behind it is worse than a missing entry, because it
gets believed.
