# 0008 — Opt-in complete query results for Question Packs

**Status:** Accepted
**Date:** 2026-11-07

## Context

A completed Genie message can be prose or a clarification without a query result. A result can
also be incomplete. Both remain successful Question Pack outcomes by default, which suits
text-oriented reports but cannot establish that a scheduled data report obtained complete rows.
Classifying answer text would introduce language-dependent guesses.

## Decision

Add YAML `behavior.requireQueryResult`, default `false`, backed by an init-only property on
`PackBehavior`. Keep its positional constructor unchanged. When enabled, the shared runner
requires a non-null `GenieQueryResult` whose `IsTruncated` is false. A complete zero-row result
passes. The client already flags incomplete chunk retrieval and row-limit shortfalls as truncated.

Use existing `GenieException` handling with `MessageFailed` and stable error codes
`QUERY_RESULT_REQUIRED` or `QUERY_RESULT_INCOMPLETE`. These are Question Pack policy failures;
the underlying completed response remains available as `LastKnownResponse` on a propagated
exception. Continue mode records the failure and moves on; stop mode throws before emitting that
outcome. Cancellation takes precedence over policy evaluation.

## Consequences

Existing packs retain their interpretation. Strict packs can require complete results without
claiming that SQL or the answer is correct. No natural-language detection, additional API call,
dependency, or change to `LakeSpeak.Genie` is needed. Both collected and streaming runs use the
same check, preserving the CLI's warning, partial-report exit code and atomic publication path.

The option is Unreleased. It is not accepted by the published 0.3.5 CLI; the separate package
quickstart deliberately uses only the public 0.3.5 Genie API.
