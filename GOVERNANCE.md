# Governance

## Current model

Single maintainer ([@ivanvyd](https://github.com/ivanvyd)) — benevolent-dictator in practice, which
is worth stating plainly rather than dressing up as a committee. If the project attracts sustained
contribution, this document changes to reflect that; until then, pretending otherwise would waste
contributors' time.

## How decisions get made

Anything that changes the architecture or the public API of `LakeSpeak.Genie` needs an ADR in
[docs/decisions](docs/decisions/), in the same pull request as the change, using the existing
`Status / Date / Context / Decision / Consequences` format. ADRs are binding once accepted. Reopening
one is fine; ignoring one is not.

Everything else is a normal pull request.

## Scope, and the project's main failure mode

The most likely way this project fails is not lack of users. It is scope growth into an unofficial
Databricks SDK for .NET that one person cannot maintain.

The promise is deliberately narrow:

> LakeSpeak.NET provides a good .NET and terminal experience for stateful conversations with
> Databricks Genie Agents.

Proposals are measured against that sentence. A feature can be genuinely useful and still be
rejected for being outside it.

**Out of scope, and not close calls:**

- A general Databricks SDK for .NET, or endpoints unrelated to Genie conversations.
- Arbitrary SQL execution, or editing generated SQL.
- Creating, deleting, or optimising Genie Agents. That is Genie Workbench's job.
- Caching, queueing, or centralised governance in front of the Genie API. That is a gateway's job.
- A web UI, a hosted service, or a background daemon.
- Bundling an LLM provider or implementing RAG.

**Deferred rather than rejected** — reconsidered once there are real users asking:

- An MCP server mode. Databricks ships managed MCP endpoints for Genie, and those are **stateless**
  — every question starts over — so a stateful LakeSpeak MCP server, or one exposing Question
  Packs, would offer something they do not. That makes this a scope decision rather than a
  duplication one: an MCP server is a second product with its own transport and support burden. It
  is deferred until a real user asks, not rejected. Re-exposing `ask` alone would still not be a
  reason. See [ADR 0001](docs/decisions/0001-a-cli-and-a-library-rather-than-another-mcp-server.md)
  and its correction.
- Multi-Agent orchestration.

## New dependencies

Every new dependency needs a justification in the pull request that adds it: what it does, why the
BCL is not enough, and what the licence is. Copyleft licences are refused by CI, because they would
make the Apache-2.0 promise false for anyone linking `LakeSpeak.Genie`.

## Releases

Semantic versioning. Before `v1.0`, minor versions may break the API; that is what `0.x` means and
it is stated in the README rather than discovered.

Releases are cut from `main` by tag. Publishing requires the protected `nuget` environment.

## If the maintainer disappears

If there is no maintainer response for 90 days, the project is unmaintained. Contributors are
encouraged to fork; the Apache-2.0 licence exists precisely so that is possible without asking.
