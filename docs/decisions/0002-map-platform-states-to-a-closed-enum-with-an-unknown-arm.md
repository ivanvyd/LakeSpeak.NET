# 0002 — Map platform states to a closed enum with an `Unknown` arm

**Status:** Accepted
**Date:** 2026-08-01

## Context

Genie message status is an open-ended set. Reading the generated Databricks SDK gives ten values
today: `SUBMITTED`, `FILTERING_CONTEXT`, `FETCHING_METADATA`, `ASKING_AI`, `PENDING_WAREHOUSE`,
`EXECUTING_QUERY`, `COMPLETED`, `FAILED`, `CANCELLED`, `QUERY_RESULT_EXPIRED`.

The plan this project started from listed seven, missing five of them. That is the ordinary
outcome of working from prose.

More tellingly, published Databricks documentation shows a status of `IN_PROGRESS`, which appears in
no SDK. Either it is stale or it is real and undocumented elsewhere. Either way, a client that
enumerated the ten known values and threw on anything else would compile, pass every mock test, and
fail against the live service.

There is a competing rule in this codebase's own standards: a `switch` over an enum should be
exhaustive, with a `never`-style default, so a new variant fails at compile time. That rule is
right — for types we own.

## Decision

Translate platform status strings at the wire boundary into a closed internal enum,
`GenieMessageState`, which includes an `Unknown` member. Unrecognised values map to `Unknown` and
never throw.

`Unknown` is **non-terminal**. Polling continues.

Several platform states collapse: `FILTERING_CONTEXT`, `FETCHING_METADATA` and `ASKING_AI` all
become `Thinking`, because no caller has a reason to distinguish them and preserving the difference
would leak API mechanics into a progress message.

The exhaustive-switch rule continues to apply to this project's own closed types, such as
`GenieFailureKind`, where an unhandled arm genuinely is a bug.

## Consequences

A status Databricks adds next month does not crash a released client. It shows as "Working" and
polling proceeds to a terminal state.

Treating `Unknown` as non-terminal is the deliberate half of this. An unrecognised status is far
more likely to be a new intermediate step than a new terminal one; if we guessed terminal, we would
truncate a working conversation and hand back an empty answer as though it were complete. The
failure mode we chose instead is bounded by the polling timeout, which reports honestly.

The cost is that a genuinely new terminal state would stall until timeout. That is the right trade:
a slow honest failure beats a fast wrong success.

`QUERY_RESULT_EXPIRED` is terminal but is **not** a failure — the answer and SQL remain valid and
only the cached result aged out — so it returns a response rather than throwing.
