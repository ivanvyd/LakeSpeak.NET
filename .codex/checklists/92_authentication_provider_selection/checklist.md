# Testing Checklist: Authentication Provider Selection

**Ticket:** [PR #92](https://github.com/ivanvyd/LakeSpeak.NET/pull/92)
**Source:** PR diff and the authentication precedence documented in `docs/authentication.md`

---

## Setup & Test Data

### Required resources

| Resource | Required state | Used by |
|---|---|---|
| Local checkout | The pull-request branch | Sections 1–2 |
| .NET SDKs | The SDK from `global.json` plus .NET 8 | Sections 1–2 |

### Prerequisites

- Run commands from the repository root.
- Do not provide real credentials. The tests set dummy process-local values and restore the values
  they found before each case.
- Run `dotnet restore --locked-mode` before the test commands.

---

## Tests

### 1. Provider precedence

#### 1.1 Every authentication selection branch is exercised _(AC-AUTH.1)_

**Steps:**

1. Run:
   ```powershell
   dotnet test tests/LakeSpeak.Genie.Tests/LakeSpeak.Genie.Tests.csproj --no-restore -c Release -f net10.0 --filter-method "*AuthenticationSelectionTests*" --ignore-exit-code 8
   ```

**Expected:** Seven tests pass. They cover PAT precedence over partial and complete M2M settings,
both one-sided M2M errors, complete M2M selection, CLI fallback, and caller-provider precedence.

#### 1.2 The same contract holds on the supported net8.0 target _(AC-AUTH.2)_

**Steps:**

1. Repeat Test 1.1 with `-f net8.0`.

**Expected:** The same seven tests pass under net8.0.

### 2. Cross-feature regression

#### 2.1 The complete non-live suite remains green _(AC-AUTH.3)_

**Steps:**

1. Run the net10.0 suite:
   ```powershell
   dotnet test LakeSpeak.slnx --no-restore -c Release -f net10.0 --filter-not-trait 'Category=Live' --ignore-exit-code 8
   ```
2. Run the net8.0 library suite:
   ```powershell
   dotnet test tests/libraries-only.slnx --no-restore -c Release -f net8.0 --filter-not-trait 'Category=Live' --ignore-exit-code 8
   ```

**Expected:** Both commands finish with zero failed tests. The live tests do not contact a
Databricks workspace.

---

## Explicitly Uncovered

This checklist does not prove the GitHub-hosted `live-smoke.yml` branch for a missing repository
secret. That end-to-end case remains Test 5.2 in the #54 checklist and requires a disposable
repository or fork whose secret configuration can be changed safely.
