# Authentication

LakeSpeak never stores a credential. It has no credential store, and its configuration file has no
field capable of holding one.

## Interactive use: the Databricks CLI

The default. You log in once with the Databricks CLI, and LakeSpeak borrows the token it caches.

```bash
databricks auth login --profile company
lakespeak chat --profile company
```

Under the hood LakeSpeak runs `databricks auth token --profile company` and uses the short-lived
OAuth token it returns. The browser flow, the token cache and the refresh logic all stay inside a
tool Databricks maintains — see [ADR 0003](decisions/0003-broker-credentials-through-the-databricks-cli.md)
for why that trade was made.

The CLI is invoked with an argument vector, never through a shell, because profile names can come
from a config file or a Question Pack.

## Unattended use: an environment token

CI runners and containers have no browser, and often no Databricks CLI. Set `DATABRICKS_TOKEN` and
LakeSpeak uses it directly, taking precedence over the CLI broker.

```bash
export DATABRICKS_HOST="https://adb-xxxxxxxxxxxx.n.azuredatabricks.net"
export DATABRICKS_TOKEN="<token>"
lakespeak ask --agent finance --format json "Revenue yesterday?"
```

Two honest caveats.

A **personal access token** is a standing credential with no automatic refresh. Databricks
documents it as a local-debugging path rather than a production one, and `auth check` warns when it
finds one in a profile. A short-lived OAuth access token also works in `DATABRICKS_TOKEN`, but it
must not be stored for a scheduled job: it will be expired by a later run.

On **Azure**, a better option exists: an Entra ID access token for the Databricks resource works
directly as a bearer token, for a user principal or a managed identity. It is short-lived, and a
managed identity needs no Azure RBAC role for this.

```bash
export DATABRICKS_TOKEN=$(az account get-access-token \
  --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d --query accessToken -o tsv)
```

This is the path used to verify LakeSpeak against a live workspace; see
[compatibility.md](compatibility.md).

## Unattended use: OAuth M2M (native)

The cleanest path for CI, a scheduled Question Pack, or anything with no human at a terminal is a
**Databricks service principal** authenticated via the OAuth machine-to-machine flow. Set
`DATABRICKS_CLIENT_ID` and `DATABRICKS_CLIENT_SECRET` and LakeSpeak handles the rest — token endpoint,
refresh, expiry window — the same way the CLI broker does, but without the CLI and without a
long-lived `DATABRICKS_TOKEN` left in the environment.

```bash
export DATABRICKS_HOST="https://adb-1234567890123456.7.azuredatabricks.net"
export DATABRICKS_CLIENT_ID="<service-principal-application-id>"
export DATABRICKS_CLIENT_SECRET="<oauth-secret>"
lakespeak pack run packs/daily-brief.yaml
```

The token is fetched from `{host}/oidc/v1/token` with HTTP Basic auth, cached in memory and
refreshed before it expires. The credential is never written to disk, never placed in a URL, and
never appears in `argv` — see [ADR 0003](decisions/0003-broker-credentials-through-the-databricks-cli.md)
for the reasoning that this principle was built around.

`DATABRICKS_CLIENT_ID` and `DATABRICKS_CLIENT_SECRET` must be set together. Setting only one is a
misconfiguration that produces an OAuth failure indistinguishable from a wrong credential; the
client throws a clear message at start-up rather than letting that ambiguity reach the first
Databricks call.

Resolution order when more than one credential is set: `DATABRICKS_TOKEN` first (the local-debug
shortcut), then M2M (the unattended path), then the Databricks CLI broker (the interactive
default). `lakespeak auth check` names the path it picked, so the diagnostic value of "it
worked" is "it worked via M2M" when that is the surprise.

The service principal sees what it is granted, and nothing else. It needs explicit access to the
Genie Agent and its SQL warehouse; it does not inherit your permissions. `lakespeak agents list`
under the service principal is the fastest way to confirm what it can actually reach.

## Unattended runs: minting the token outside LakeSpeak (legacy)

Before LakeSpeak implemented the OAuth M2M flow directly, a service principal was used through a
short shell snippet that minted a token with `curl` and passed it as `DATABRICKS_TOKEN`. Native
M2M removes the need for that recipe, so it is no longer the recommended path.

If you cannot move to native M2M yet—for example, you are pinned to an older version—the same shape
still works, with the caveats that applied to it: the secret must travel through stdin rather than
`argv`, the token expires in an hour, and a long-running daemon is not what this is for. The current
release no longer needs it.

## What is not supported

**OIDC workload federation**, GitHub Actions OIDC, and Azure DevOps OIDC. v0.2 or later.

## Resolving the workspace host

First match wins:

1. `GenieClientOptions.Host` set in code
2. `DATABRICKS_HOST`
3. the `host` of the named profile in `.databrickscfg`
4. the `host` of the `DEFAULT` profile

`https` is required. A non-https host is rejected rather than accepted, because a bearer token over
plain HTTP is a disclosed token and the mistake is otherwise silent.

`lakespeak config show` prints which value won and where it came from.

## In a .NET application

```csharp
services.AddLakeSpeak(options => options.Profile = "production");
```

To supply your own token — from an application's existing OAuth flow, or a managed identity:

```csharp
services.AddGenieTokenProvider(async ct => await myTokenSource.GetAsync(ct));
services.AddLakeSpeak(options => options.Host = new Uri("https://..."));
```

Register the provider **before** `AddLakeSpeak`; it uses `TryAdd`, so a provider you register wins.

## What LakeSpeak guarantees

- No token is written to disk, put in a URL, or passed as a process argument.
- Authorization headers and both Databricks signature fields are redacted from all diagnostic
  output, including `--verbose`, and from exception messages.
- Presigned result URLs are fetched **without** the `Authorization` header, so a Databricks
  credential is never handed to blob storage.
- `auth check` and `config show` print no part of any credential.
