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

The one place the two TFMs would have diverged on behaviour: the unsafe-method
exclusion for the standard resilience handler.

`Microsoft.Extensions.Http.Resilience` 8.10.0 does not expose
`HttpRetryStrategyOptions.DisableForUnsafeHttpMethods()`; that helper landed in 9.x.
The extension does what its name says — it makes the standard resilience handler skip
retries for POST, PATCH, PUT, DELETE, and CONNECT — and the Genie `start-conversation` and
`create-message` calls are POSTs that must not be retried (a retry asks Genie the same
question again, runs the SQL warehouse a second time, and leaves an orphaned conversation
whose id the client never returns).

The fix is the same shape on both TFMs: install the unsafe-method exclusion by hand
at the `ShouldHandle` predicate, using the public `HttpClientResiliencePredicates.IsTransient`
helper for the transient check and comparing the request method against the unsafe set.
The predicate is portable because the underlying `RetryStrategyOptions<HttpResponseMessage>`
type is the same on both 8.10.0 and 9.x+; only the helper that wraps the predicate is
absent on the older line. This keeps the contract test for "exactly one attempt on a
failing `start-conversation`" green on both TFMs.

## Decision

Multi-target the library family at `net8.0;net10.0` and keep the CLI at `net10.0`-only.
The unsafe-method exclusion (POST/PATCH/PUT/DELETE/CONNECT) is installed at the
`ShouldHandle` predicate on both TFMs, so the resilience behaviour is identical across
the two. The mechanical parts:

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
6. `src/LakeSpeak.Genie/ServiceCollectionExtensions.cs` — install the unsafe-method
   exclusion at the `ShouldHandle` predicate (see Context above), so the standard
   resilience handler skips POST/PATCH/PUT/DELETE/CONNECT retries on both TFMs
   without a TFM-specific code path.

## Consequences

**Positive.** Library consumers on the LTS track (`net8.0`) can take a
`<PackageReference Include="LakeSpeak.Genie" />` without target-redirecting their app. The
package ships `lib/net8.0/` and `lib/net10.0/`. The two test jobs in CI exercise both
TFMs, so a TFM-specific regression cannot merge silently. The unsafe-method exclusion
behaviour is identical on both TFMs (the contract test that pins "exactly one attempt on
a failing `start-conversation`" runs and passes on both).

**Negative.** The lock file now has a per-TFM entry for every multi-targeted project,
which makes the lockfile diff in this PR large. Expected.

**Local dev cost.** Two .NET SDKs on the build machine. The `global.json` pin selects the
.NET 10 SDK for MSBuild; `setup-dotnet` in CI installs the .NET 8 SDK additionally for
the net8.0 cell. The dual install costs ~30 seconds in CI per net8.0 cell.

**Not in this PR.** Lowering the floor to .NET 6 or 7 — explicitly out of scope.
Multi-targeting the CLI — out of scope by the tradeoff above.
