# Changelog

Notable changes, newest first. This project follows [semantic versioning](https://semver.org), with
the caveat that `0.x` minor versions may break the API — that is what `0.x` means, and it is stated
here rather than left to be discovered.

## Unreleased

The next patch release fixes complete-result assembly for a Genie response shape observed in the
live AWS workspace and moves the scheduled live smoke test from a stored bearer token to
LakeSpeak's native OAuth M2M provider. The public API is unchanged.

### Fixed

- **Chunked results are assembled when Genie omits the next-chunk link.** Genie's query-result
  response can advertise `next_chunk_index` while omitting `next_chunk_internal_link`, even though
  the underlying SQL Statement response contains the link. LakeSpeak now constructs the
  workspace-relative chunk endpoint from the statement id, keeps the existing host validation,
  and continues until the result is complete. A live four-chunk result returned all 1,000 rows
  with `Truncated = false`. This closes [#54](https://github.com/ivanvyd/LakeSpeak.NET/issues/54).
- Dot-only statement ids are rejected before URL construction. `System.Uri` normalizes `.` and
  `..` path segments even after escaping; treating those malformed ids as incomplete prevents a
  request to the wrong SQL endpoint.

### Changed

- **The scheduled live smoke workflow uses native OAuth M2M.** It now receives only the service
  principal client id and secret; LakeSpeak acquires and refreshes the short-lived access token in
  memory. The Question Pack workflow example uses the same direct configuration instead of a
  separate `curl` token exchange.

### Verified

- The M2M-backed GitHub Actions run completed all 9 live tests against the AWS workspace from the
  merged runtime-bearing `main` revision `6b27140`: [run 33516431819](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33516431819).
- The retry regression was replayed red/green: without the request-method context fallback the
  non-idempotent test made four attempts and failed; with the fallback restored it passed on both
  `net8.0` and `net10.0`.
- The full local suite passed on both target frameworks, and release builds were warning-free.

## 0.3.0 — 2026-08-31

The test infrastructure migrates to Microsoft Testing Platform, and the library family multi-targets
`net8.0` and `net10.0` so consumers on the LTS track no longer have to target-redirect their app.
Both were forced by the .NET 10 SDK's VSTest removal (Dependabot could no longer bump
`Microsoft.NET.Test.Sdk`) and by adopter demand. A post-ship review caught a security defect on the
new resilience branch and a coverage gap that masked it: both are fixed in the same release.

`0.x` still means a minor version may break the API. The public surface is unchanged — the
multi-targeting is a packaging change, not a binary one.

### Added

- **Multi-target `net8.0` and `net10.0` for the library family** — `LakeSpeak.Genie`,
  `LakeSpeak.Application`, `LakeSpeak.Configuration`, `LakeSpeak.QuestionPacks` and
  `LakeSpeak.Rendering` now ship `lib/net8.0/` and `lib/net10.0/`. A consumer on the LTS track
  can take a `<PackageReference Include="LakeSpeak.Genie" />` without target-redirecting their
  app. The `LakeSpeak.Cli` tool stays `net10.0`-only: .NET 10 is where MTP runs cleanly without a
  per-TFM test-harness split, and the maintainer's local machine is on the .NET 10 SDK. The
  reasoning is recorded in [ADR 0006](docs/decisions/0006-multi-target-net8-and-net10.md).
- **Microsoft Testing Platform across the test suite** — `xunit.v3` with the `Microsoft.Testing.Platform`
  runner, `global.json`'s `test.runner: Microsoft.Testing.Platform`, and a slnx-level
  `dotnet test` invocation. The .NET 10 SDK drops VSTest in `Microsoft.NET.Test.Sdk` 18.9+, so
  the major-version test-group Dependabot bumps (the ones in PR #77) could not land without
  this. MTP discovery is wired end-to-end on `ubuntu-latest`, `windows-latest` and
  `macos-latest`. The v1 protocol runs on `net8.0` (`xunit.v3`); the v2 protocol runs on
  `net10.0` (`xunit.v3.mtp-v2`). The CLI is excluded from the net8.0 cell via a second
  slnx, `tests/libraries-only.slnx`, so the slnx-level runner can proceed with `-f net8.0`
  against a multi-targeted project set.

### Changed

- **CI matrix splits the build per TFM** — `matrix.tfm: [net8.0, net10.0]` on the `build` job, so
  each TFM compiles once in parallel rather than the matrix walking every project for every
  TFM. The `pack` step runs on a single non-matrix job because the libraries are multi-targeted:
  `dotnet pack` walks every TFM of every project, and a per-TFM cell leaves the other TFM's
  artefacts missing. `tool-smoke` now depends on `pack`. Net effect: same coverage, roughly
  half the wall time on cold-cache runners, and the test job is no longer behind the
  per-TFM compile.
- The `build (net8.0)` cell installs the .NET 8 SDK from `setup-dotnet@v6.0.0`; the previous
  shape assumed the .NET 10 SDK from `global.json` was enough.
- `Directory.Build.props` `<VersionPrefix>` is now `0.3.0` for the local development default.
  The release workflow continues to override it from the tag.

### Security

- **The `ShouldHandle` predicate for the standard resilience handler no longer retries unsafe
  HTTP methods on transient exceptions** — a `POST` to `start-conversation` whose underlying
  socket reset will no longer be retried. The standard handler's `ShouldHandle` only sees
  the request method via `args.Context` on the exception path; the previous `GenieRetryPolicy`
  helper read it from `args.Outcome.Result?.RequestMessage`, which is `null` when the call
  has not produced a response yet. Without the context fallback a socket reset on
  `start-conversation` re-issued the POST, ran the SQL warehouse a second time and left an
  orphaned conversation whose id the client never returns. The fix uses
  `HttpResilienceContextExtensions.GetRequestMessage(ResilienceContext)`, which is marked
  `[Experimental]` in the resilience package; the warning is suppressed at the Genie project
  level (`<NoWarn>$(NoWarn);EXTEXP0001</NoWarn>`) with a comment explaining why. The net9
  and net10 paths keep the official `HttpRetryStrategyOptions.DisableForUnsafeHttpMethods()`
  helper, which makes the same context fallback internally.

### Fixed

- **`A_connection_refused_start_conversation_is_never_retried`** — a sibling to
  `A_failed_start_conversation_is_never_retried` that covers the exception path the
  previous test did not. The previous test stubbed a 503 response, so it covered the path
  the standard handler classifies through `args.Outcome.Result`. The new test points the
  client at a closed port (a TCP listener bound to port 0 and immediately released, so the
  OS will refuse subsequent connects) and counts attempts through the resilience pipeline
  via a counter `DelegatingHandler` wired in by an `IHttpMessageHandlerBuilderFilter`.
  Without the security fix above this test counts 4 attempts (1 + 3 retries) in well under
  the Genie timeout; with the fix it counts 1.
- **CI: build and test jobs use `tests/libraries-only.slnx` on the net8.0 cell** — the
  default slnx includes the net10.0-only `LakeSpeak.Cli` and `LakeSpeak.Cli.Tests`
  projects, and `dotnet build -f net8.0` over the full slnx fails with NETSDK1005 ("Assets
  file doesn't have a target for 'net8.0'"). The libraries-only slnx excludes those
  projects and is multi-targetable end to end.
- **CI: pack runs in its own non-matrix job** — the per-TFM build cells leave the
  multi-targeted libraries' artefacts asymmetric (the net8.0 cell builds only net8.0
  outputs; the net10.0 cell builds only net10.0), and `dotnet pack` over a project
  whose only net8.0 outputs are missing fails with NU5026. The single-shot `pack` job
  restores the full slnx, builds every TFM of every project, and packs them in one shot.
- **CI: per-step `shell: bash` on the multi-line `run:` blocks** — Windows runners
  default to PowerShell 7, which does not parse the bash-only `slnx=$([ ... ] && echo ...
  || echo ...)` command substitution. The four steps that use it now pin `shell: bash`
  so the matrix runs the same script on all three OSes.

### Verified

- `dotnet restore --locked-mode` clean on `ubuntu-latest`, `windows-latest` and
  `macos-latest`.
- `dotnet build -c Release` clean, warnings-as-errors, for both `net8.0` and `net10.0` on
  all three OSes, in CI.
- `dotnet format --verify-no-changes` clean on all six cells.
- `dotnet list package --vulnerable --include-transitive`: no moderate-or-higher advisories.
- The full non-live test suite, both TFMs, all three OSes: green. The two new
  resilience-path tests (response and exception) both pass on both TFMs; the exception-path
  test is the one that would have failed on the pre-fix code, and was demonstrated to do
  so before the fix landed.
- `dotnet pack` produces `LakeSpeak.Genie.0.3.0.nupkg` (with `lib/net8.0/` and
  `lib/net10.0/`), `LakeSpeak.Cli.0.3.0.nupkg` (with `tools/net10.0/any/`), and matching
  `.snupkg` symbol packages.
- The packaged tool installs from `./artifacts` and `lakespeak --version` returns `0.3.0`,
  in CI on every PR via the `tool-smoke` job.
- Multi-targeting against a real consumer — the `examples/dotnet-quickstart` project
  builds and runs against the freshly packed `LakeSpeak.Genie.0.3.0.nupkg` with
  `<TargetFramework>net8.0</TargetFramework>`.
- Live (`Category=Live`) coverage unchanged from 0.2.0: Azure verified 2026-08-01,
  AWS verified 2026-08-29, GCP untested. The `Live smoke` workflow is still
  `workflow_dispatch` + weekly cron behind `secrets.DATABRICKS_TOKEN` and the live
  variables. As of 2026-08-31 the workflow has not yet been re-run against the
  v0.3.0 binary.

## 0.2.0 — 2026-08-29

The two gaps the README's verification table already named as "not tested" or "not yet done" —
the macOS arm64 matrix entry and OAuth M2M, the first item on the v0.2 roadmap — are addressed.
A re-runnable live smoke workflow now runs the `Category=Live` suite behind a repo secret on
`workflow_dispatch` and weekly, so the live path does not rot between uses.

`0.x` still means a minor version may break the API. Nothing in the public surface has changed;
this is additive.

### Added

- **OAuth M2M token provider** — `LakeSpeak.Genie.Authentication.M2mTokenProvider` exchanges
  `DATABRICKS_CLIENT_ID` and `DATABRICKS_CLIENT_SECRET` for short-lived access tokens at
  `{host}/oidc/v1/token` with HTTP Basic auth, caches them in memory, and refreshes proactively
  on a 60s grace window. The DI registration order is `DATABRICKS_TOKEN` (PAT, the local-debug
  path) → OAuth M2M (the unattended path) → Databricks CLI broker (the interactive default).
  Setting only one of the two M2M variables throws a clear error at first DI resolve. A
  Question Pack on a schedule no longer needs to hold a long-lived personal access token. The
  exchange with valid service-principal credentials against a real workspace is the contribution
  that closes #55.
- **macOS arm64 in the CI test matrix.** `macos-latest` is now exercised alongside `ubuntu-latest`
  and `windows-latest`. The compat table's row for macOS moves from "Release binary is built;
  untested" to "Unit and contract tests run in CI". Closes #53.
- **A re-runnable Live smoke workflow** — `.github/workflows/live-smoke.yml` runs the
  `Category=Live` suite behind `secrets.DATABRICKS_TOKEN`, `vars.LAKESPEAK_LIVE_HOST`, and
  `vars.LAKESPEAK_LIVE_AGENT` on `workflow_dispatch` and a weekly cron. A fork or unconfigured
  maintainer run sees a `::notice::` and exits 0, so PR review is unaffected.

### Changed

- `auth check` now names the OAuth M2M provider in its source-of-credentials output, alongside
  the existing Databricks CLI broker and `DATABRICKS_TOKEN` entries. The PAT path still prints
  a redacted token length; the M2M path prints the configured env-var names, not the values.
- `docs/authentication.md` documents native OAuth M2M as the recommended unattended path. The
  old `curl`-based recipe is retained as a "legacy" reference for readers pinned to an older
  LakeSpeak version, with a note that it is no longer needed.
- `docs/compatibility.md` spells out the contribution that closes the AWS and GCP rows
  (a PR that runs the live suite on the relevant cloud and records the result here) rather
  than leaving them as bare "not tested" entries.

### Verified

- `dotnet build -c Release` clean, warnings-as-errors, on `ubuntu-latest`, `windows-latest`,
  and `macos-latest` in CI.
- `dotnet test -c Release --filter "Category!=Live"`: 282 passed, 0 failed.
- 9 M2M contract tests against a stubbed token endpoint cover Basic auth, form parameters,
  response parsing, refresh, error mapping, non-JSON error bodies, and concurrent first-callers
  sharing a single fetch.
- `Live smoke` has not been run against a real workspace on this release. The contract tests
  cover the wire shape; the live exchange is the next contribution that closes #55.

## 0.1.1 — 2026-08-14

Two fixes that did not make it into 0.1.0. Both were caught by verifying the published release
rather than the build output, and both are correctness defects with the same shape: a behaviour
that the build accepted silently because the test or the verification command was checking the
wrong artefact.

### Fixed

- **Re-executing an expired result now returns the rows.** `execute-query` only *starts* the
  re-execution: against a live workspace it answers `PENDING` with no manifest, and the rows appear
  on the ordinary query-result endpoint a moment later. The client returned that acknowledgement,
  so `ReExecuteQueryAsync` produced `null` and `lakespeak export last` told the user to ask the
  question again — while the warehouse work they had just paid for completed and was thrown away.
  It now waits for the rows.

  The contract test covering this stubbed a *completed* response, a shape Databricks does not
  return, so the test agreed with the bug. The replacement is a **live** test, because a stub
  cannot catch an error in the stub.

- **Release binaries are now attested.** The provenance attestation covered only the `.nupkg`
  files, so `gh attestation verify` on a downloaded release binary failed — the exact command
  `SECURITY.md` tells an adopter to run. The zips are now attestation subjects too.
- **`SHA256SUMS.txt` now lists only what is actually on the release.** It was generated across the
  whole build directory, so it included the four `.nupkg`/`.snupkg` files that go to NuGet rather
  than to the release; `sha256sum -c` therefore failed on four of seven entries for anyone who
  downloaded 0.1.0.

  Both were found by verifying the published 0.1.0 release rather than the build output. The
  verification instructions had been checked against a rehearsal artifacts directory, which
  contains the packages — so both commands passed there and failed in reality. `SECURITY.md` now
  states what applies to 0.1.0 and what changes from 0.1.1, including the
  `sha256sum -c --ignore-missing` form that does work on 0.1.0.

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

- **Release tags must now be signed.** The workflow verifies the tag's SSH signature against
  `.github/allowed_signers` before it builds anything. The provenance attestation proves *what*
  built an artifact; it cannot prove *who* authorised the release, because anyone able to push a
  tag starts the workflow and the resulting attestation would be valid. The signing key is the one
  credential GitHub does not hold, so this is what makes a stolen GitHub account insufficient on
  its own. See [RELEASING.md](RELEASING.md#signed-tags), including what it does not protect
  against.

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
