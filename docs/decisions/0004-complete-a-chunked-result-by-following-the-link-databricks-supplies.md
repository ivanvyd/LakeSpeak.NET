# 0004 — Complete a chunked result by following the link Databricks supplies

**Status:** Accepted
**Date:** 2026-08-05

## Context

A query result large enough to be split across chunks came back as chunk zero. It was correctly
flagged as truncated — that much was honest — but the rows were missing, and "your export is
incomplete" is the failure a user meets the first time they point this at real data rather than a
demo table.

Completing the result needs a way to fetch chunk *n*. The Genie Conversation API does not offer
one: every path in [`planning/genie-api-surface.md`](../planning/genie-api-surface.md) takes an
attachment, not a chunk index. Two mechanisms exist.

**The Genie download endpoints.** `POST …/attachments/{id}/downloads` returns a `download_id` and
a `download_id_signature`; `GET …/downloads/{download_id}` then returns a `statement_response`.
Squarely inside the Genie API. It is a second workflow with its own polling loop and its own
expiry semantics, and the response it returns is itself subject to the same chunking — so it
completes nothing on its own.

**The link Databricks already sends.** The Statement Execution contract pairs `next_chunk_index`
with `next_chunk_internal_link`, documented as an absolute path to be joined with the workspace
host and treated as opaque. Genie returns the statement response verbatim, so this client was
already receiving that link on every chunked result and discarding it during deserialization.

## Decision

Follow `next_chunk_internal_link` until the result is complete.

The link is treated as opaque, as documented, and is **not** reconstructed from a template. This
is deliberately not an implementation of the Statement Execution API: no path is composed, no
statement id is used, and nothing but a link the workspace itself supplied is ever requested. That
distinction is what keeps this inside `GOVERNANCE.md`'s scope rather than making LakeSpeak a
client for a second Databricks API.

Because the link is server-supplied and the bearer token rides on every workspace request, only a
workspace-relative path is accepted. An absolute or protocol-relative link is refused rather than
followed, and is not logged — the case worth logging is precisely the one where an attacker chose
its contents.

`GenieClientOptions.MaxResultRows` bounds the walk, defaulting to 100,000 rows. Following a
chunked result to its end is otherwise unbounded work whose output is held in memory.

## Consequences

Results are complete. `IsTruncated` now means what a reader would assume it means, rather than
"there were more chunks and we stopped".

Every way of failing to complete a result still reports truncation: no link accompanying the
index, a link that is not a workspace path, a chunk the caller may not read, or the row cap. The
property that mattered before this change — a partial result is never reported as complete — is
unchanged, and is now enforced across five exits rather than one.

A chunk that cannot be fetched degrades to the rows already in hand rather than throwing. The
answer text is the primary payload and a missing tail should not discard it; the truncation flag
is what tells the caller the table is short.

**This is verified by contract tests and has not been exercised against a live workspace.**
Whether a caller may read the remaining chunks of a statement Genie executed on their behalf is a
workspace permission question that no documentation settles. The unreachable-chunk path exists
because the answer may well be "no", in which case behaviour is exactly what it was before this
change. `docs/compatibility.md` records this as untested rather than implying otherwise.
