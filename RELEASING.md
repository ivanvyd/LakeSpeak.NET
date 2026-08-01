# Releasing

How a LakeSpeak release is cut. Written down because a release process that lives in one person's
head is a release process that stops when they do.

## What a release produces

| Artifact | Where it goes |
|---|---|
| `LakeSpeak.Genie` | NuGet — the library |
| `LakeSpeak.Cli` | NuGet — the `lakespeak` dotnet tool |
| `lakespeak-<version>-win-x64.zip` | GitHub release — self-contained, no SDK needed |
| `lakespeak-<version>-linux-x64.zip` | GitHub release |
| `lakespeak-<version>-osx-arm64.zip` | GitHub release (built, **not tested** — see `docs/compatibility.md`) |
| `SHA256SUMS.txt` | GitHub release |
| `sbom.json` | GitHub release — CycloneDX, the vendor list an adopter's review will ask for |
| Build provenance attestation | Attached to each `.nupkg` |

## Versioning

Semantic versioning. **Before `v1.0`, a minor version may break the API** — that is what `0.x`
means, and the README says so rather than leaving people to discover it.

The version comes from one place per situation:

- **A tag** `v1.2.3` → version `1.2.3`.
- **A manual run** with the `version` input → that value.
- **A manual run with no input** → `0.0.0-dev.<run number>`, which is never published.

`VersionPrefix` in `Directory.Build.props` is the local-development default only. CI overrides it.

## Rehearse first

The release workflow is manually runnable, and by default it does **not** publish. Use that.

1. Actions → **Release** → *Run workflow*.
2. Leave **publish** unticked. Optionally set **version** to the version you intend to cut.
3. Run it.

That builds, runs the full non-live test suite, packs, publishes the three self-contained
binaries as workflow artifacts, generates the SBOM and attests provenance — everything a real
release does except pushing to NuGet and creating a GitHub release. If the rehearsal is red, the
release would have been red.

Download the artifact and install the tool locally before trusting it:

```bash
dotnet tool install --global --add-source ./artifacts LakeSpeak.Cli --version <version>
lakespeak --version
```

CI already does this on every PR (the `tool-smoke` job), but doing it by hand once before a real
release is cheap.

## Cut the release

### Prerequisites, once

> **None of this is configured yet.** As of the last check the repository has no environments and
> no secrets. This matters more than it looks: a workflow that names an environment which does not
> exist does **not** fail — GitHub creates it implicitly, with no protection rules. So until the
> steps below are done there is no approval gate, and the only thing standing between a mistyped
> manual run and a permanent package is the version guard in the workflow itself.

Publishing uses **trusted publishing**, not a stored API key. GitHub mints a short-lived OIDC
token, nuget.org verifies it against a policy naming this exact repository and workflow, and
returns a temporary key valid for one hour. Nothing long-lived is ever stored in the repository,
so there is no key to leak, rotate, or accidentally scope too widely.

**1. Create the trusted publishing policy on nuget.org.**

Log in to nuget.org → your username → **Trusted Publishing** → add a policy:

| Field | Value |
|---|---|
| Repository Owner | `ivanvyd` |
| Repository | `lakespeak` |
| Workflow File | `release.yml` — the file name only, **not** `.github/workflows/release.yml` |
| Environment | `nuget` — must match the `environment:` in the publish job |

The policy is owned by you or by an organisation, and applies to every package that owner owns.
If Trusted Publishing does not appear in your account, it has not been rolled out to you yet;
in that case fall back to an API key scoped to `LakeSpeak.*`.

**2. Add the `NUGET_USER` repository secret.**

Your nuget.org **profile name** — not your email address. It is not a credential; it is a secret
only so the workflow file does not hard-code an account name.

```bash
gh secret set NUGET_USER
```

**3. Create the `nuget` environment with a required reviewer.**

Settings → Environments → New environment → `nuget` → add yourself under *Required reviewers*.

Creating the environment alone changes nothing — the required reviewer *is* the gate, and it is
also what the trusted publishing policy binds to. Without it, publishing is a side effect of
pushing a tag rather than a decision someone makes.

**4. Verify all three before the first release.**

```bash
gh api repos/ivanvyd/lakespeak/environments --jq '.environments[] | {name, rules: [.protection_rules[].type]}'
gh secret list
```

The first publish also completes the policy: for a new policy nuget.org records the GitHub
repository and owner IDs on first successful use, which is what stops someone deleting the repo,
recreating it under the same name, and publishing as if nothing changed.

### Steps

1. Update `CHANGELOG.md`. Move `Unreleased` entries under a new `## <version> — <date>` heading.
2. Confirm `docs/compatibility.md` reflects what has actually been verified for this version.
   An entry there with no evidence behind it is worse than a missing one.
3. Merge those to `main`.
4. Tag and push:

   ```bash
   git tag -a v1.2.3 -m "v1.2.3"
   git push origin v1.2.3
   ```

5. The workflow runs. If the environment gate is configured (see prerequisites), it stops
   there for approval — otherwise it publishes straight away.
6. Check the GitHub release: three binaries, checksums, SBOM, generated notes.

### Publishing without a tag

A manual run with **publish** ticked will push to NuGet. It exists for the case where a tag has
already been pushed and the publish job failed for an environmental reason — a NuGet outage, an
expired key — and you want to retry without inventing a new version.

It does not create a GitHub release, because a manual run has no tag to attach one to.

## If something goes wrong

**NuGet does not allow unpublishing.** A package can be deprecated or delisted, never removed.
That is why the rehearsal step exists, and why configuring the environment gate is worth
doing before the first release rather than after the first mistake.

- **Wrong version published** → publish a corrected higher version, then delist the wrong one.
  Do not attempt to reuse the version number; NuGet will reject it and `--skip-duplicate` will
  silently succeed without publishing anything.
- **Tag pushed too early** → delete the tag (`git push --delete origin v1.2.3`) *before* approving
  the environment gate. After approval, the only path forward is a new version.
- **Release job red after publish** → the packages are already on NuGet. Fix the GitHub release
  by hand rather than re-running the whole workflow.

## What is deliberately not automated

There is no auto-release on merge, no release-please, no auto-generated version from commit
messages. For a project this size those add a machine that has to be understood before a release
can be made, which is the opposite of what this file is for.
