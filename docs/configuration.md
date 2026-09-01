# Configuration

LakeSpeak reads one optional YAML file. Everything in it is a convenience — the tool works with no
configuration at all, given a Databricks profile and an Agent name on the command line.

**No credential is ever stored here.** The file holds names, ids and display preferences. Tokens
stay with the Databricks CLI or the environment; see [authentication](authentication.md).

## Where the file lives

| Platform | Path |
|---|---|
| Windows | `%APPDATA%\LakeSpeak\config.yaml` |
| macOS / Linux | `$XDG_CONFIG_HOME/lakespeak/config.yaml`, or `~/.config/lakespeak/config.yaml` when `XDG_CONFIG_HOME` is unset |

`lakespeak config show` prints the resolved path along with what was loaded, which is faster than
working it out from the table.

The file is not created for you. Make it by hand when you want one.

## A complete example

```yaml
version: 1

defaults:
  profile: my-workspace
  agent: sales
  output: text
  timeout: 10m

agents:
  sales:
    id: 01ef1234567890abcdef1234567890ab
  platform:
    id: 01effedcba0987654321fedcba098765
    profile: prod-workspace

display:
  maxRows: 50
  showSqlByDefault: false
```

Keys are camelCase, and matched case-sensitively. Unrecognised keys are **ignored without
warning** — a typo like `maxrows` or `defaults.aegnt` silently does nothing rather than failing.
Run `lakespeak config show` after editing this file to confirm what was actually picked up;
malformed YAML *is* reported, with the file and line number, but a well-formed key nobody reads
is not.

## What each key does

### `version`

Schema version, currently `1`. Present so a future change can be handled rather than guessed at.

### `defaults`

| Key | Default | Meaning |
|---|---|---|
| `profile` | none | Databricks CLI profile to use when `--profile` is not given |
| `agent` | none | Agent to use when `--agent` is not given |
| `output` | `text` | Output format when `--format` is not given: `text`, `table`, `markdown`, `json`, `jsonl` or `csv` |
| `timeout` | `10m` | How long to wait for an answer before giving up, as a duration such as `90s`, `5m` |

A command-line flag always wins over a default here.

### `agents`

Aliases, so scripts and Question Packs carry a readable name instead of a raw id. Each entry maps
an alias to an `id`, and optionally to the `profile` that Agent lives in — useful when you work
across more than one workspace.

```yaml
agents:
  sales:
    id: 01ef1234567890abcdef1234567890ab
```

`lakespeak ask --agent sales "…"` then resolves without a lookup.

Alias names are matched case-insensitively, so `--agent Sales` and `--agent sales` reach the same
Agent. An alias takes precedence over an Agent title: if you alias `sales` to one id and a Genie
Agent in the workspace is also titled "sales", the alias wins, because it is an explicit
instruction and the title is a coincidence.

### `display`

| Key | Default | Meaning |
|---|---|---|
| `maxRows` | `50` | Rows shown in a terminal table. Exports and machine formats are never truncated by this — it only limits what is printed. |
| `showSqlByDefault` | `false` | Print the generated SQL without having to pass `--show-sql` |

## Resolution order

For the Agent, highest priority first:

1. `--agent` on the command line
2. `defaults.agent` in this file
3. Nothing — the command reports that no Agent was specified

Whatever that yields is then resolved as: a configured alias, then an exact Agent id, then an exact
Agent title, then a case-insensitive title match. An ambiguous name lists the candidates and exits
rather than picking one.

For the profile:

1. `--profile` on the command line
2. The profile the last answer came from, for `export last` and `feedback last` — so they address
   the workspace the conversation actually lives in
3. `defaults.profile` in this file
4. Environment configuration: `DATABRICKS_HOST`, then `DATABRICKS_TOKEN` or the
   `DATABRICKS_CLIENT_ID` / `DATABRICKS_CLIENT_SECRET` pair; otherwise `.databrickscfg`

## Checking what was loaded

```bash
lakespeak config show
```

Prints the resolved file path, the effective defaults, and each configured alias. Use it before
opening an issue about the wrong Agent being reached — it is usually a `defaults.agent` someone
forgot about.
