# 0003 — Broker credentials through the Databricks CLI

**Status:** Accepted
**Date:** 2026-08-01

## Context

LakeSpeak needs a bearer token for every request. The options were to implement a browser-based
OAuth flow ourselves, to ask users for a personal access token, or to reuse a login someone has
already performed.

Implementing U2M OAuth means owning a redirect listener, a token cache, refresh logic, and a
credential store on three operating systems. Every one of those is a place to leak a token, and none
of them is the problem this project exists to solve.

Asking for a personal access token is easy and wrong: a PAT is a standing credential with no expiry
pressure, and Databricks documents it as a local-debugging path rather than a production one.

The Databricks CLI already performs the browser login, caches the result, and refreshes it.
`databricks auth token --profile <p>` prints a currently-valid token as JSON.

## Decision

Shell out to the Databricks CLI as an OAuth token broker. LakeSpeak stores no credential of its own
and has no configuration field capable of holding one.

The CLI is invoked with an argument vector and `UseShellExecute = false`. Profile names can come
from a configuration file or a Question Pack, so they are attacker-influenced and must never reach a
shell interpreter.

For unattended use where no browser exists, read `DATABRICKS_TOKEN` from the environment, and say
plainly in the docs that this is the weaker path.

## Consequences

Onboarding is one command a Databricks user has usually already run, and the whole credential
lifecycle stays inside a tool Databricks maintains. There is no LakeSpeak credential store to
compromise.

The costs are real and worth naming. LakeSpeak now depends on an external binary being installed and
on a version whose output format is not contractual — so the token JSON is parsed defensively, and a
missing CLI produces an error naming the install page rather than a stack trace. Each refresh spawns
a process, which is why the token is cached in memory behind a semaphore rather than fetched per
request.

`databricks auth token` supports U2M profiles only; **OAuth M2M client-credential profiles are
explicitly unsupported**. Native M2M is therefore v0.2 work, and until then unattended use means a
personal access token with the caveat above. This is recorded in `docs/limitations.md` rather than
left for someone to discover in a pipeline.
