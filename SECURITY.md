# Security policy

## Reporting a vulnerability

Report privately through
[GitHub Security Advisories](https://github.com/ivanvyd/lakespeak/security/advisories/new). Do not
open a public issue for a vulnerability.

This is a solo-maintained project. Expect an acknowledgement within 5 working days and an initial
assessment within 10. If a fix is warranted, you will be credited in the advisory unless you ask not
to be. If you have not heard back in 10 working days, please assume the message was missed and
escalate by opening a public issue that says only that you are waiting on a security response —
with no details.

## Supported versions

Until `v1.0`, only the latest released minor version receives fixes.

## What LakeSpeak protects

**Access tokens are never persisted.** LakeSpeak brokers short-lived OAuth tokens through the
Databricks CLI and holds them in memory for the life of the process. It writes no credential store,
and no token is written to its configuration file.

**Tokens are never passed as process arguments.** Command lines are readable by other users on the
same host on most platforms.

**Authorization headers are redacted** in all diagnostic output, including `--debug`, and in
exception messages.

**Presigned result URLs are fetched without the `Authorization` header.** Sending a Databricks
bearer token to blob storage would leak it to a third party. Databricks rejects such requests with
HTTP 400, so the mistake fails loudly rather than silently — but the client must not rely on that.

**Arguments to the Databricks CLI are passed as an argument vector, never through a shell.** Agent
names, profile names and questions are attacker-influenced in the sense that they may come from a
Question Pack or a script; none of them reaches a shell interpreter.

**Question Packs are data, not code.** They are validated against a published JSON Schema. A pack
cannot execute a command, read an arbitrary file, or widen the permissions of the identity running
it.

**Terminal output is sanitized.** Genie returns model-generated text and query results drawn from
your data. Both are untrusted for rendering purposes: ANSI escape sequences and control characters
are stripped before anything reaches your terminal, so a crafted cell value cannot rewrite your
screen or spoof a prompt.

**Export paths are checked.** Writes outside the target directory are rejected, and an existing file
is not overwritten without confirmation.

## What LakeSpeak does not protect against

**It cannot make Genie answers correct.** Generated SQL can be wrong in ways that look right.

**It cannot widen or narrow your Unity Catalog permissions.** You see what your identity can see. If
that is more than you expected, that is a workspace governance question, not a LakeSpeak one.

**It cannot protect data after you export it.** An exported CSV is an ordinary file with no
protection of its own.

**It cannot keep results out of CI logs.** If you run `lakespeak ask` in a pipeline, the answer goes
wherever that pipeline's output goes. Treat job logs as readable by everyone with repository access.

**It does not defend against a malicious Databricks workspace.** A workspace you authenticate to can
return whatever it likes. LakeSpeak validates response shape but trusts response content.

## Threat model

Recorded in [docs/security/threat-model.md](docs/security/threat-model.md).

## Supply chain

- Dependencies are centrally pinned with lock files; CI restores in `--locked-mode`.
- A moderate-or-higher advisory in any dependency, including transitive, fails the build.
- GitHub Actions are pinned by commit SHA.
- Releases carry an SBOM, SHA-256 checksums, and build provenance attestation.
- Publishing to NuGet requires a protected environment, so it is a decision rather than a side
  effect of pushing a tag.
- Live integration tests never run for pull requests from forks, because they need workspace
  credentials.
