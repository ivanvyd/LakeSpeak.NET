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

## Verification — 2026-08-05

The premise this decision was least sure of has since been checked against a live Azure workspace,
and it holds.

Whether a caller may read the remaining chunks of a statement Genie executed on their behalf was
recorded here as settled by no documentation. It is now settled by observation: a Genie-executed
statement's id, taken from a completed message, returned **HTTP 200** from both
`/api/2.0/sql/statements/{id}` and `/api/2.0/sql/statements/{id}/result/chunks/0` using the
caller's own token.

The wire contract was checked the same way, on a deliberately chunked statement: `total_chunk_count`
of 2, `manifest.truncated` **false** on a merely-chunked result — the defect's premise, confirmed —
a `next_chunk_internal_link` of `/api/2.0/sql/statements/{id}/result/chunks/1`, and following it
returning the remaining rows with no `next_chunk_index`, summing exactly to `total_row_count`.

## Verification — 2026-09-01

The missing composition was driven end to end against an existing AWS Genie Agent without creating
a table or changing the Agent. Genie generated a 1,000-row diagnostic result with three repeated
review-text columns. Its SQL Statement manifest reported four chunks, `truncated: false`, and 1,000
total rows. LakeSpeak assembled all 1,000 rows and reported the result as complete.

The probe exposed one more difference between the two APIs. The SQL Statement response included
`next_chunk_internal_link`; the Genie query-result response kept the statement id and
`next_chunk_index` but omitted that link. The original implementation therefore returned only the
first chunk and correctly marked it truncated. LakeSpeak now falls back to the documented
`/api/2.0/sql/statements/{statement_id}/result/chunks/{next_chunk_index}` endpoint when Genie omits
the link. The statement id is escaped as one path segment and the resolved URI still has to match
the workspace before the credential is sent. See [compatibility.md](../compatibility.md) for the
full evidence.
