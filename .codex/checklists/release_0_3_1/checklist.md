# Testing Checklist: Release v0.3.1

**Source:** Release request and `RELEASING.md`

**Status:** Completed on 2026-09-01. This file preserves the v0.3.1 acceptance checks and is not
the procedure for a future release; `RELEASING.md` remains canonical.

**Evidence:** [rehearsal run 33535149754](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33535149754),
[tag release run 33535667215](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33535667215),
[GitHub release v0.3.1](https://github.com/ivanvyd/LakeSpeak.NET/releases/tag/v0.3.1), and the public
[`LakeSpeak.Genie`](https://www.nuget.org/packages/LakeSpeak.Genie/0.3.1) and
[`LakeSpeak.Cli`](https://www.nuget.org/packages/LakeSpeak.Cli/0.3.1) packages.

**Post-release audit:** The second independent correctness, structure, security, and requirements
review found no production-code defect. It did find four release-evidence and process defects:

- The recorded `net10.0` count was 258 instead of the reproduced 264.
- The privileged release job installed CycloneDX without an exact version.
- `SECURITY.md` told consumers to verify the public NuGet package against the pre-signing build
  attestation, although NuGet.org adds a repository signature and changes the archive hash.
- This version-specific checklist did not identify itself as a completed historical record.

[PR #94](https://github.com/ivanvyd/LakeSpeak.NET/pull/94) corrected all four. The
[post-release rehearsal](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33537144267) installed
CycloneDX 6.2.0, generated the SBOM, passed the release build and tests, and skipped publication.
Final `main` [CI](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33537586581) and
[Security](https://github.com/ivanvyd/LakeSpeak.NET/actions/runs/33537586600) runs passed at
`833f6188e137dc4a9177f9c93a601ec60910552c`.

---

## Setup & Test Data

### Prerequisites

- Run commands from the repository root.
- Start from a clean branch based on `origin/main`.
- Keep GCP validation issue #52 open; this release does not claim GCP evidence.
- Before the initial publication, confirm `v0.3.1` does not already exist on GitHub or NuGet.
- Confirm the `nuget` GitHub environment and `NUGET_USER` secret are configured.

---

## Tests

### 1. Release metadata

#### 1.1 Version surfaces agree _(AC-REL.1)_

**Steps:**

1. Confirm `Directory.Build.props` contains `<VersionPrefix>0.3.1</VersionPrefix>`.
2. Confirm `CHANGELOG.md` contains `## 0.3.1` below an empty `## Unreleased` heading.
3. Confirm the README and roadmap name v0.3.1 as the current source version or milestone and point
   to NuGet for publication status.

**Expected:** Every source-version surface says v0.3.1 without claiming that an unpublished package
is already stable, and the changelog retains an empty Unreleased section for later work.

#### 1.2 Compatibility claims remain bounded _(AC-REL.2)_

**Steps:**

1. Read the Clouds table and v0.3.1 verification entry in `docs/compatibility.md`.
2. Confirm AWS evidence links to completed runs and GCP points to open issue #52.
3. Confirm `ROADMAP.md` leaves forced `QUERY_RESULT_EXPIRED` and other future features unchecked.

**Expected:** Completed AWS/M2M/chunk work is marked complete. GCP and genuinely untested paths
remain explicit rather than being presented as release evidence.

### 2. Build, tests, and package

#### 2.1 The complete result checklist is reproducible _(AC-REL.3)_

**Steps:**

1. Run `dotnet restore LakeSpeak.slnx --locked-mode`.
2. Run the Test 2.2 command in the #54 checklist.

**Expected:** Restore succeeds and all 18 `ResultCompletenessTests` pass.

#### 2.2 Both supported frameworks remain green _(AC-REL.4)_

**Steps:**

1. Build `LakeSpeak.slnx` in Release configuration.
2. Run the complete non-live `net10.0` suite.
3. Run the complete non-live `net8.0` library suite.
4. Run `dotnet format LakeSpeak.slnx --verify-no-changes --no-restore`.
5. Run `dotnet list LakeSpeak.slnx package --vulnerable --include-transitive`.

**Expected:** The build has no warnings or errors; all tests pass with no failures; formatting is
clean; no vulnerable direct or transitive package is reported.

#### 2.3 Consumers can install the candidate _(AC-REL.5)_

**Steps:**

1. Pack the solution as version 0.3.1 into an empty local artifacts directory.
2. Confirm `LakeSpeak.Genie.0.3.1.nupkg` contains both `lib/net8.0/` and `lib/net10.0/`.
3. Install `LakeSpeak.Cli` version 0.3.1 from that directory to an isolated `--tool-path`.
4. Run the installed `lakespeak --version`.

**Expected:** Both packages and symbol packages exist. The library contains both target
frameworks, and the CLI reports version 0.3.1.

### 3. Release workflow

#### 3.1 Rehearsal produces every artifact without publishing _(AC-REL.6)_

**Steps:**

1. Dispatch the Release workflow from the release branch with version `0.3.1` and `publish` false.
2. Wait for the build job to complete.
3. Inspect the `release-artifacts` workflow artifact.

**Expected:** Build, non-live tests, package, three self-contained binaries, checksums, SBOM, and
provenance all succeed. The publish job is skipped and NuGet still has no 0.3.1 package.

#### 3.2 The signed tag publishes the release _(AC-REL.7)_

**Steps:**

1. Merge the green release PR to `main`.
2. Create signed annotated tag `v0.3.1` on the merge commit and verify it against
   `.github/allowed_signers`.
3. Push the tag and approve the `nuget` environment deployment.
4. Wait for the exact tag-triggered Release run.

**Expected:** Signature verification, build, test, pack, binary publish, checksums, SBOM,
attestation, NuGet push, and GitHub release all succeed.

### 4. Published-artifact verification

#### 4.1 Public package and release surfaces agree _(AC-REL.8)_

**Steps:**

1. Confirm NuGet indexes list LakeSpeak.Genie 0.3.1 and LakeSpeak.Cli 0.3.1.
2. Confirm GitHub release `v0.3.1` is neither draft nor prerelease.
3. Confirm the release contains three platform archives, `SHA256SUMS.txt`, `sbom.json`, and
   `provenance.intoto.jsonl`.
4. Install LakeSpeak.Cli 0.3.1 from NuGet to an isolated tool path and run `--version`.

**Expected:** All public surfaces expose 0.3.1, all six release assets exist, and the public CLI
reports 0.3.1.
