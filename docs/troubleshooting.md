# Troubleshooting

Start with:

```bash
lakespeak auth check
lakespeak config show
```

Between them they tell you which profile is in play, which workspace it resolves to, whether a
token can actually be obtained, and whether the workspace answers. Neither prints any part of a
credential, so both are safe to paste into an issue.

## "Could not run the Databricks CLI"

LakeSpeak brokers tokens through the Databricks CLI and could not start it.

Install it, then log in:

```bash
databricks auth login --profile company
```

If the CLI is installed but not on `PATH` for the process running LakeSpeak — common on Windows
after a `winget` install in an already-open shell — restart the shell.

For CI, where there is usually no CLI at all, set `DATABRICKS_TOKEN` instead. See
[authentication](authentication.md).

## "No Databricks host configured"

Nothing supplied a workspace URL. Set one of, in precedence order: `DATABRICKS_HOST`, a `host` in
the named `.databrickscfg` profile, or a `host` in the `DEFAULT` profile.

`lakespeak config show` reports which source won.

## "Databricks rejected the credentials (HTTP 401)"

The token expired or was never valid. For a CLI profile:

```bash
databricks auth login --profile company
```

If you are supplying `DATABRICKS_TOKEN` yourself, it is probably stale — an Entra token lasts about
an hour.

## "The authenticated identity is not permitted to do this (HTTP 403)"

Your identity cannot use that Agent, or cannot read a table behind it. This is a Databricks grant,
and nothing LakeSpeak can change: it cannot widen your access.

## "No Genie Agents are visible to this identity"

Not an error — the call succeeded and returned nothing. Either no Genie Agents exist in the
workspace, or none are shared with your identity. Both are fixed in Databricks.

Confirm you are pointed at the workspace you think you are with `lakespeak config show`.

## "'x' matches N Agents"

Two Agents share a title. LakeSpeak refuses to guess, because answering against the wrong Agent
looks exactly like success. Use the id from `lakespeak agents list`, or add an alias to your config
so scripts do not carry raw ids.

## "Genie did not finish within Ns"

The question exceeded the timeout. Usually a cold SQL warehouse — the first question of the day
can take minutes while it starts.

Raise it per question in a pack (`timeout: 5m`) or in `defaults.timeout` in your config. The error
names the last state observed, which tells you whether it was waiting on a warehouse
(`PendingWarehouse`) or actually running a query (`ExecutingQuery`).

## "The cached query result has expired"

The message succeeded and its answer and SQL are still valid; only the cached result aged out.
Ask again to regenerate it. This is `QUERY_RESULT_EXPIRED`, which LakeSpeak deliberately does not
treat as a failure.

## Output looks broken when piped

Results go to stdout and diagnostics to stderr. If you are seeing progress messages mixed into
your data, you are capturing both:

```bash
lakespeak ask --agent finance --format json "…" 2>/dev/null   # data only
```

If you are seeing escape sequences, force plain output with `NO_COLOR=1`. LakeSpeak already
suppresses colour when output is redirected or the format is machine-readable.

## Non-ASCII shows as `?` in my terminal

The tool writes UTF-8 and pins its output encoding. A `?` almost always means the terminal or the
capture is on a legacy code page. Redirect to a file and inspect it — the bytes are usually fine.

## `pack validate` fails and I fixed one thing

It reports every problem at once, on purpose. Read the whole list before re-running.

## "spec.output.path resolves outside the pack directory"

Report paths are relative to the pack file, and traversal is rejected. A pack can arrive from a
pull request, so this is a guard rather than an inconvenience. Move the output inside the pack's
directory.

## CI fails with NU1004 after I added a dependency

The lock files no longer match the project graph. Regenerate and commit them:

```bash
dotnet restore --force-evaluate
```

CI restores in `--locked-mode` so a drifted transitive version fails the build rather than silently
resolving differently on the runner.

## Still stuck

Open an issue with what you ran, what happened, and what you expected. Include
`lakespeak --verbose` output — credentials are redacted, but **read it first**: questions, answers
and query results can contain your organisation's data, and LakeSpeak cannot know which of your
table names are sensitive.
