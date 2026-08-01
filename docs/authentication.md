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

A **personal access token** is a standing credential with no refresh and no expiry pressure.
Databricks documents it as a local-debugging path rather than a production one, and `auth check`
warns when it finds one in a profile.

On **Azure**, a better option exists: an Entra ID access token for the Databricks resource works
directly as a bearer token, for a user principal or a managed identity. It is short-lived, and a
managed identity needs no Azure RBAC role for this.

```bash
export DATABRICKS_TOKEN=$(az account get-access-token \
  --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d --query accessToken -o tsv)
```

This is the path used to verify LakeSpeak against a live workspace; see
[compatibility.md](compatibility.md).

## What is not supported

**OAuth M2M client-credential profiles.** `databricks auth token` covers user-to-machine profiles
only, so a profile configured with `DATABRICKS_CLIENT_ID` and `DATABRICKS_CLIENT_SECRET` cannot be
brokered through the CLI. Native M2M is v0.2 work. Until then, use an environment token.

**OIDC workload federation**, GitHub Actions OIDC, and Azure DevOps OIDC. Also v0.2 or later.

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
