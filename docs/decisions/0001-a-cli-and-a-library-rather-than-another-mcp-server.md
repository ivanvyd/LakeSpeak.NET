# 0001 — A CLI and a library, rather than another MCP server

**Status:** Accepted
**Date:** 2026-08-01

## Context

Databricks already ships managed MCP endpoints for Genie: a workspace-wide endpoint at
`/api/2.0/mcp/genie` and a per-Agent one at `/api/2.0/mcp/genie/{genie_space_id}`. Building an MCP
server is also the fashionable move, which is a reason to be suspicious of it rather than a reason
to do it.

Two facts decide this.

First, an MCP server for Genie would duplicate something Databricks maintains. Any bug we fixed
would be a bug they had already fixed, and any user who wanted MCP would reasonably prefer the
first-party one.

Second, Databricks documents that using Genie as an MCP tool does **not** automatically carry prior
conversation history, whereas the Conversation API is explicitly stateful. Follow-up questions are
the thing that makes Genie feel like a conversation rather than a search box, so the stateful API is
the interesting surface and MCP is the lossy one.

What is genuinely missing is not another protocol adapter. It is a good product experience: a human
sitting at a terminal, and a .NET developer with an `IGenieClient` they can inject.

## Decision

Build a terminal client and a focused .NET library against the Conversation API. Do not implement
an MCP server in v0.1.

Coding agents are served through the CLI's JSON output instead. An agent that can run a shell
command can run `lakespeak ask --format json`, which needs no protocol implementation, no second
process, and no separate authentication path.

## Consequences

We do not compete with a first-party integration, and we are not on the hook for protocol
compatibility as MCP evolves.

Agent integration is slightly less ergonomic than a native MCP tool — a shell call rather than a
declared tool. In exchange it works identically for humans, needs no server, and shares one
authentication story.

An MCP mode becomes worth reconsidering only when LakeSpeak has something the official endpoints do
not, such as Question Packs. Re-exposing `ask` over MCP would not qualify. This is recorded in
`GOVERNANCE.md` as deferred rather than rejected.

## Correction — 2026-08-05

The decision stands. Two of the premises above do not, and leaving them uncorrected would let a
future reader inherit reasoning that has since been falsified.

**"An MCP server for Genie would duplicate something Databricks maintains" is wrong.** Databricks'
managed Genie MCP endpoints invoke Genie as a stateless tool: conversation history is not carried
between calls, so every `query_space` call is a brand-new question. A stateful MCP server would not
duplicate theirs. It would do the one thing theirs cannot.

**The second premise confuses the protocol with one implementation of it.** The context above uses
Genie-as-an-MCP-tool not carrying history to conclude that "MCP is the lossy one". That is a
property of Databricks' server, not of the protocol. Nothing in MCP prevents a stateful
implementation, and this project's own conversation handling is the hard part of building one.

The decision is unchanged because the honest reason was never duplication — it is scope. An MCP
server is a second product with its own transport, surface and support burden, attached to a
project one person maintains. The trigger for reconsidering it, per `GOVERNANCE.md`, is a real user
asking. That has not happened.

One further fact bearing on the "coding agents are served through the CLI's JSON output"
consequence: the official CLI now ships `databricks genie ask`, which is stateful across calls via
`-s` and emits JSON. The consequence still holds. It is simply no longer unique to LakeSpeak.
