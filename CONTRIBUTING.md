# Contributing

Thanks for considering it. This is a small, deliberately narrow project — read
[GOVERNANCE.md](GOVERNANCE.md) first if you are proposing a feature, because scope is the most
likely reason for a pull request to be declined, and that is easier to hear before you write the
code than after.

## Getting set up

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for anything touching
authentication, the [Databricks CLI](https://docs.databricks.com/dev-tools/cli/install.html).

```bash
git clone https://github.com/ivanvyd/lakespeak.git
cd lakespeak
dotnet restore
dotnet build -c Release
dotnet test -c Release --filter "Category!=Live"
```

The default test run needs no Databricks workspace and no credentials. That is deliberate: a
contributor should be able to make a change and prove it without an account or a bill.

## Before opening a pull request

```bash
dotnet build -c Release            # warnings are errors
dotnet format --verify-no-changes
dotnet test -c Release --filter "Category!=Live"
```

If you added or changed a dependency, regenerate the lock files and commit them:

```bash
dotnet restore --force-evaluate
```

CI restores in `--locked-mode`, so a stale lock file fails the build rather than silently
resolving to different versions on the runner than on your machine.

## What a good change looks like

**A test that would fail without the fix.** For anything that could regress, prove it: revert your
fix, watch the test go red, restore it, watch it go green. A test that passes either way is a
comment shaped like a test.

**Wire changes backed by evidence.** If you change how a Databricks response is parsed, say where
the field name came from. The generated SDKs are a more reliable source than the prose docs, which
contain at least one status value that does not exist.
[`docs/planning/genie-api-surface.md`](docs/planning/genie-api-surface.md) records which claims are
verified and which are not — add to that table rather than quietly widening a parser.

**Comments that say why, not what.** This codebase leans on comments explaining the failure a piece
of code prevents. Restating the code in English is noise, and gets removed.

**An ADR for anything architectural.** Public API or architecture changes need one in
`docs/decisions/`, in the same pull request, in the existing format.

## Live tests

Tests marked `Category=Live` need a real workspace with a Genie Agent and cost real compute. They
never run for pull requests from forks, because they require credentials. Run them yourself with:

```bash
dotnet test -c Release --filter "Category=Live"
```

Do not point them at a production workspace.

## Reporting a bug

Include what you ran, what happened, and what you expected. `lakespeak --verbose` prints
diagnostics with credentials redacted.

**Read the output before pasting it.** Questions, answers and query results can contain your
organisation's data. LakeSpeak redacts credentials; it has no way to know which of your table names
are sensitive.

For security issues, do not open an issue — see [SECURITY.md](SECURITY.md).
