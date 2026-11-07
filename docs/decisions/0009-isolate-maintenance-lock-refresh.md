# 0009: Isolate maintenance lock refresh

**Status:** Accepted
**Date:** 2026-11-07

## Context

NuGet Dependabot bumps require regenerated lock files under central package management.
Restore and build execute code from the maintenance branch, including imported package targets.
Removing persisted checkout credentials does not isolate that execution from a write-capable
job token. Lock refresh therefore needs a separate credential and execution boundary.

## Decision

Use three fresh hosted runners: resolve, refresh and write. Dispatch the workflow from the
default branch, passing the Dependabot branch as input. The read-only resolver rejects other
workflow refs, allows only `dependabot/nuget/*` branches and resolves the explicit branch ref
to an immutable source SHA. Trusted helper checkouts use `github.workflow_sha`.

The read-only refresh runner checks out the resolved candidate SHA, regenerates locks,
verifies locked restore and builds. Its only transfer is a bounded JSON artifact containing
lock bytes. Neither candidate execution nor candidate outputs supply the writer's trusted SHA.

Set workflow-level `cache-mode: none` without job overrides to deny cache restores and saves.
Runtime cache tokens are independent of repository permissions; GitHub enforces their scope.

The writer checks out only the trusted workflow source. It validates the artifact envelope,
branch, source SHA, paths, regular-file modes and NuGet lock structures before writing.
The payload must contain exactly the existing tracked lock files; unrelated files, symlinks,
duplicate properties and oversized data are rejected. No candidate code or build output runs.

A new bare Git repository fetches the exact branch and requires its head to match the resolved
SHA. Git objects and an index produce a lock-only tree and commit without a candidate checkout,
configuration, attributes or hooks. An unchanged tree skips commit and push. A normal nonforce
push rejects branch advancement after fetch. The writer has only `contents: write` and does
not dispatch workflows or introduce another privileged Security workflow entry point.

## Consequences

Write credentials exist only on the fresh writer. Refresh execution remains read-only.
Concurrent updates require another refresh instead of silently replacing newer commits.
Token pushes suppress push workflows; PR workflows can await 'Approve workflows to run'.
Approve pending PR workflows for CI and Security after a changed push. If expected checks are
absent, close and reopen the PR with your own identity. Rerunning older runs does not test the
new SHA. Dispatching `ci.yml` alone cannot establish all required checks for that commit.

Validation checks the supported lock formats and data shapes; it does not solve NuGet ranges,
judge dependency suitability or attest that candidate code honestly performed a build.
The protocol trusts default-branch code, pinned Actions, hosted runner isolation, job-token scoping and run-scoped artifacts.
Runtime cache denial relies on GitHub's [scoped cache tokens](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#cache-mode); hosted enforcement remains unverified.
Payload size checks occur after download; artifact transfer and extraction remain platform assumptions.
