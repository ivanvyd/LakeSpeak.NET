# Testing Checklist: Complete Genie Chunk Assembly and Scheduled M2M

**Ticket:** #54
**Source:** [PR #89](https://github.com/ivanvyd/LakeSpeak.NET/pull/89) and
[issue #54](https://github.com/ivanvyd/LakeSpeak.NET/issues/54)

---

## Setup & Test Data

### Required test resources

| Resource | Required state | Used by |
|---|---|---|
| Local checkout | The pull-request branch, with the SDK from `global.json` installed | Sections 1–3 |
| Databricks workspace | An existing Genie Agent and SQL warehouse; do not create shared fixtures for this checklist | Section 4 |
| Service principal | Existing principal with `CAN_RUN` on the Agent and `CAN_USE` on its warehouse | Sections 4–5 |
| GitHub repository | `LAKESPEAK_LIVE_HOST` and `LAKESPEAK_LIVE_AGENT` variables plus M2M client-id and client-secret secrets | Section 5 |

### Prerequisites

- Run commands from the repository root.
- Run `dotnet restore LakeSpeak.slnx --locked-mode` before commands that use `--no-restore`.
- Do not print or echo credential values. Store the client id and secret through the GitHub secrets
  interface or `gh secret set` over standard input.
- Leave `DATABRICKS_TOKEN` unset for M2M tests.
- Use an existing Agent that can answer from a table large or wide enough to produce more than one
  result chunk. If the workspace cannot produce one, record Section 4 as blocked rather than creating
  a shared table.

---

## Tests

### 1. Deterministic result composition

#### 1.1 Genie omits the next-chunk link _(AC-54.1)_

**Steps:**

1. Run:
   ```powershell
   dotnet test tests/LakeSpeak.ContractTests/LakeSpeak.ContractTests.csproj --no-restore -c Release -f net10.0 -- --filter-method "*A_Genie_result_without_a_chunk_link_uses_its_statement_id"
   ```

**Expected:** One test passes. The result contains the first and successor rows, reports the manifest
row count, and does not report truncation.

#### 1.2 Neither link nor statement id is available _(AC-54.2)_

**Steps:**

1. Run:
   ```powershell
   dotnet test tests/LakeSpeak.ContractTests/LakeSpeak.ContractTests.csproj --no-restore -c Release -f net10.0 -- --filter-method "*A_chunked_result_with_no_link_to_follow_is_reported_as_truncated"
   ```

**Expected:** One test passes. LakeSpeak keeps the rows it received and reports the result as
truncated.

### 2. Chunk endpoint safeguards

#### 2.1 A successor link cannot send credentials off-workspace _(AC-54.3)_

**Steps:**

1. Run:
   ```powershell
   dotnet test tests/LakeSpeak.ContractTests/LakeSpeak.ContractTests.csproj --no-restore -c Release -f net10.0 -- --filter-method "*A_next_chunk_link_pointing_off_the_workspace_is_refused"
   ```

**Expected:** Both test cases pass. LakeSpeak retains the first chunk, marks it truncated, and sends
no request to the external host.

#### 2.2 Malformed chunk streams remain bounded _(AC-54.4)_

**Steps:**

1. Run:
   ```powershell
   dotnet test tests/LakeSpeak.ContractTests/LakeSpeak.ContractTests.csproj --no-restore -c Release -f net10.0 -- --filter-class "*ResultCompletenessTests*"
   ```

**Expected:** All 18 result-completeness tests pass, including repeated-link, request-limit,
row-limit, unreachable-chunk, and row-order cases. The run completes instead of looping
indefinitely.

### 3. Cross-framework and packaging compatibility

#### 3.1 The fallback works on both supported library targets _(AC-54.5)_

**Steps:**

1. Run the Test 1.1 command with `-f net8.0`.
2. Run the Test 1.1 command with `-f net10.0`.
3. Run `dotnet pack LakeSpeak.slnx --no-restore -c Release -o artifacts`.

**Expected:** Both targeted tests pass. Packing produces `LakeSpeak.Genie.<version>.nupkg` containing
both `lib/net8.0/` and `lib/net10.0/`, plus the CLI package for the same version.

### 4. Live Databricks behavior

#### 4.1 An existing Genie Agent returns a complete multi-chunk result _(AC-54.6)_

**Steps:**

1. Set `DATABRICKS_HOST`, `DATABRICKS_CLIENT_ID`, and `DATABRICKS_CLIENT_SECRET` for the existing
   service principal. Leave `DATABRICKS_TOKEN` unset.
2. Use `lakespeak agents list` to confirm the intended Agent is visible.
3. Ask the Agent for 1,000 rows from an existing wide-text table. Request several repeated wide-text
   columns if the Agent needs more payload to produce multiple chunks.
4. Compare the returned row count with the SQL Statement manifest through the workspace query
   history or Statement Execution API.

**Expected:** The statement manifest reports more than one chunk. LakeSpeak returns the manifest's
full row count in order and reports `Truncated = false`. If Genie omits
`next_chunk_internal_link`, the result still completes through the statement-id fallback.

### 5. Scheduled native OAuth M2M

#### 5.1 The live workflow uses native M2M _(AC-M2M.1)_

**Steps:**

1. Confirm the repository has `LAKESPEAK_LIVE_HOST` and `LAKESPEAK_LIVE_AGENT` variables and
   `DATABRICKS_CLIENT_ID` and `DATABRICKS_CLIENT_SECRET` secrets.
2. Confirm the repository does not contain a `DATABRICKS_TOKEN` secret.
3. Dispatch `Live smoke (Databricks Genie)` from the branch under test.
4. Open the completed job log and search for `Test run summary`, `DATABRICKS_TOKEN`, and the two M2M
   variable names.

**Expected:** All nine live tests pass. The job environment contains the two M2M names with masked
values, contains no `DATABRICKS_TOKEN`, and prints no credential value.

#### 5.2 Missing M2M configuration fails before tests _(AC-M2M.2)_

**Steps:**

1. In a fork or disposable repository, configure the host and Agent variables but omit one M2M
   secret.
2. Dispatch `Live smoke (Databricks Genie)`.

**Expected:** The credential-check step exits with an error naming both required secret names.
Restore fails to start, and the log contains no credential value.

**Recorded evidence, 2026-09-01:** [run 33524613970](https://github.com/ivanvyd/LakeSpeak.NET-live-smoke-negative-20260901/actions/runs/33524613970)
in the archived private disposable repository failed at the credential-check step with the two
required secret names. The restore and test steps were both skipped, and no credential value was
printed.

---

## Blocked Environment Coverage

GCP verification requires a GCP Databricks workspace and profile. Keep #52 open and record the
missing environment if those resources are unavailable. AWS or Azure results do not count as GCP
evidence.
