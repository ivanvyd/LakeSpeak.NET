# 0006 — Multi-target .NET 8 and .NET 10

**Status:** Accepted
**Date:** 2026-08-30

## Context

The library family (`LakeSpeak.Genie`, `LakeSpeak.Application`, `LakeSpeak.Configuration`,
`LakeSpeak.QuestionPacks`, `LakeSpeak.Rendering`) targets only `net10.0`. A consumer on the
LTS track — and the maintainer's own production services — wants `net8.0` support without
forking the API. The CLI (`LakeSpeak.Cli`) is a packaged `dotnet tool`, and the MTP test
infrastructure has a per-TFM shape (ADR 0005).

Two design choices were on the table:

1. **Lower the floor to .NET 8** by multi-targeting the entire repository. Each library
   publishes `lib/net8.0/` and `lib/net10.0/`. Tests run on both TFMs. The CLI keeps the
   single TFM the .NET 10 SDK's MTP mode prefers.
2. **Stay on .NET 10 only**, document that .NET 8 consumers must wait. The cheapest path
   but it leaves a real consumer blocked.

Path 1 is what the maintainer asked for, and the technical work to do it is mechanical:

* `<TargetFramework>` → `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` in the root
  `Directory.Build.props`.
* `Microsoft.Extensions.*` packages follow the .NET runtime cadence. The net10 line is
  `10.0.11`; the net8 line is `8.0.x`. The central version stays as the default and the
  net8 line is a `<PackageVersion Update="…" Condition="'$(TargetFramework)' == 'net8.0'"/>`
  per package. The `<Update>` attribute on a `PackageVersion` element is the standard
  central-package-management way to override per-TFM; the `Include` entry above remains the
  default for every TFM not listed in the override block.
* `Microsoft.NET.Test.Sdk` 18.x and `coverlet.collector` 10.x both require the .NET 9
  runtime. On net8 they have to drop to the 17.11.x / 6.0.x line.
* The test infrastructure differs by TFM (see ADR 0005):
  - `net10.0` keeps `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>`
    and references `xunit.v3.mtp-v2` (the v2 MTP protocol).
  - `net8.0` uses `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>`
    and references `xunit.v3` (the v1 MTP protocol, which `xunit.v3` 3.x ships by default).
  - The two protocol packages cannot both be on the classpath at once — both define a
    `TestPlatformTestFramework` type — so the `PackageReference` is conditioned on the TFM.
* `LakeSpeak.Cli` is `net10.0`-only by deliberate scope. Multi-targeting the CLI doubles
  the test surface (a 4th shell, with no Windows-only behaviour to test) and complicates the
  `PackAsTool` shape. Library consumers who need the net8 path take a `LakeSpeak.Genie`
  reference; the CLI is for human and CI shells.

The one place the two TFMs diverge on resilience: the unsafe-method exclusion
for the standard resilience handler.

`Microsoft.Extensions.Http.Resilience` 8.10.0 does not expose
`HttpRetryStrategyOptions.DisableForUnsafeHttpMethods()`; that helper landed in 9.x.
The helper makes the standard resilience handler skip retries for POST, PATCH, PUT,
DELETE, and CONNECT — the Genie `start-conversation` and `create-message` calls are
POSTs that must not be retried, since a retry asks Genie the same question again, runs
the SQL warehouse a second time, and leaves an orphaned conversation whose id the
client never returns.

The net9.0+ line keeps the official `DisableForUnsafeHttpMethods()` call. The net8
line mirrors the helper at the `ShouldHandle` predicate: the public
`HttpClientResiliencePredicates.IsTransient` check first, then the unsafe-method
exclusion against the request method. On a transient exception the outcome's `Result`
is null, so the request has to come from `args.Context` — that fallback is what the
official helper does too (the standard resilience handler sets the request on the
context before the pipeline runs), and the net8 mirror has to do the same. Without
the context fallback, a socket reset on `start-conversation` would still be retried
on net8 and the contract would be broken on the exception path even though it holds
on the response path. The mirror uses the
`HttpResilienceContextExtensions.GetRequestMessage(ResilienceContext)` extension
marked `[Experimental]` in the resilience package; the warning is suppressed at the
Genie project level with a scoped `NoWarn=EXTEXP0001` and a comment explaining why.

The contract test `A_failed_start_conversation_is_never_retried` covers the response
path (the stub returns 503). A sibling test on the exception path (transient
`HttpRequestException` on a closed port) is a known coverage gap — the bug fix
above closes the defect, but a future PR should add a counter handler that observes
attempts through the resilience pipeline. The contract test is not a complete
demonstration of the contract on its own.

## Decision

Multi-target the library family at `net8.0;net10.0` and keep the CLI at `net10.0`-only.
The unsafe-method exclusion (POST/PATCH/PUT/DELETE/CONNECT) is installed at the
`ShouldHandle` predicate on both TFMs, with the same context-fallback shape on both.
The mechanical parts:

1. `Directory.Build.props` — `<TargetFramework>net10.0</TargetFramework>` becomes
   `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>`.
2. `Directory.Packages.props` — net8-line overrides for `Microsoft.Extensions.*`
   (8.0.x), `Microsoft.Extensions.Http.Resilience` (8.10.0), `Microsoft.NET.Test.Sdk`
   (17.11.1), `coverlet.collector` (6.0.4), and `Microsoft.Extensions.TimeProvider.Testing`
   (8.10.0). The default versions above remain the net10.0 values.
3. `tests/Directory.Build.props` — split the MTP shape: `UseMicrosoftTestingPlatformRunner`
   for net10, `TestingPlatformDotnetTestSupport` for net8. The `xunit.v3.mtp-v2` /
   `xunit.v3` package reference is conditioned on the TFM.
4. `src/LakeSpeak.Cli/LakeSpeak.Cli.csproj` — explicit `<TargetFrameworks>net10.0</TargetFrameworks>`,
   overriding the root. The project that exercises it (`tests/LakeSpeak.Cli.Tests`) takes
   the same explicit `net10.0` target so the net8.0 CI cell does not try to restore against
   a project that does not support it.
5. CI matrix — `tfm: [net8.0, net10.0]` on the `test` job. The net8.0 cell runs the
   multi-targeted library test projects individually (the slnx-level runner refuses
   to proceed under MTP discovery when one of the solution's test projects is
   net10.0-only and the cell asks for `net8.0`); the net10.0 cell runs the full
   slnx-level test, which includes the CLI tests.
6. `src/LakeSpeak.Genie/ServiceCollectionExtensions.cs` — `DisableForUnsafeHttpMethods()`
   on net9.0+; a hand-rolled `ShouldHandle` predicate on net8 that mirrors the helper
   and uses the same `ResilienceContext` fallback for the exception path. See
   Context above.

## Consequences

**Positive.** Library consumers on the LTS track (`net8.0`) can take a
`<PackageReference Include="LakeSpeak.Genie" />` without target-redirecting their app. The
package ships `lib/net8.0/` and `lib/net10.0/`. The CI matrix exercises both TFMs, so a
TFM-specific regression cannot merge silently. The unsafe-method exclusion behaviour is
identical on both TFMs (the contract test that pins "exactly one attempt on a failing
`start-conversation`" runs and passes on both).

**Negative.** The lock file now has a per-TFM entry for every multi-targeted project,
which makes the lockfile diff in this PR large. Expected.

**Local dev cost.** Two .NET SDKs on the build machine. The `global.json` pin selects the
.NET 10 SDK for MSBuild; `setup-dotnet` in CI installs the .NET 8 SDK additionally for
the net8.0 cell. The dual install costs ~30 seconds in CI per net8.0 cell.

**Not in this PR.** Lowering the floor to .NET 6 or 7 — explicitly out of scope.
Multi-targeting the CLI — out of scope by the tradeoff documented above.
