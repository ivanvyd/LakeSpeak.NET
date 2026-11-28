# 0010 — Opt-in strict inline results

**Status:** Accepted
**Date:** 2026-11-17

## Context

The legacy row cap stops further chunk requests, so one chunk can exceed it. Normalization
substitutes requested message identifiers without comparing returned identifiers. Ask fetches
only the first query attachment. Offline HTTP fixtures reproduce all three behaviors.
Consumers exporting bounded tables need a stricter contract without changing existing callers.

## Decision

Add nullable `GenieClientOptions.StrictResults`, captured when the client is constructed, with
immutable `GenieResultLimits(MaxRows, MaxBytes)`. Both limits must be positive; invalid options
fail before submission. Null preserves legacy behavior, including its soft `MaxResultRows` cap,
partial chunk handling and first-result aliases. Strict mode uses its own hard row budget.

Add `GenieResponse.QueryAttachments` with every query's attachment ID, query, nullable result,
and the message metadata's truncation flag. Existing `Query`, `Result`, and
`Metadata.AttachmentId` remain aliases for the first query. Legacy Ask/FollowUp fetch only that
first result. Strict Ask/FollowUp fetch all query results in server order. IncludeQueryResult=false,
GetMessage and WaitForResponse return metadata only; neither a null result nor a false metadata
truncation flag proves that a complete table exists. Prose-only responses remain valid.

Strict Ask/FollowUp share one row and JSON-body byte budget across all query-result responses
and their continuation chunks. Each GetQueryResult call has its own budget. ReExecute shares
one budget across the initial POST, polling GETs and chunks; its POST is still sent once.
Submission, message polling and agent listing envelopes each have a separate MaxBytes body
limit. Strict mode does not read HTTP error bodies at all: the status code alone classifies the
failure, so an oversized or unreadable error body cannot turn a 401, 403, 404 or 429 into a
budget or network failure. Polling duration retains its existing deadline; body cancellation uses
the caller/polling token. Each streamed body additionally has its own RequestTimeout deadline;
header and body phases are timed separately. This is not an aggregate budget for all polling traffic.

Send strict requests with `ResponseHeadersRead`, reject a declared oversized body before reading
it, and count actual body bytes before parsing. With unknown length, at most one byte beyond the
remaining budget is read to detect overflow, and that byte is never parsed or retained in the
body buffer. The bound concerns decoded JSON bodies supplied by HttpContent, after any HTTP
decompression configured on the handler. It does not bound compressed wire bytes, transport
buffers, CLR heap allocation, parser overhead, or the lifetime of results retained by consumers.

Strict results reject budget overflow (`UnsupportedResult`, `GenieErrorCodes.StrictResultLimit`)
and known incompleteness (`UnsupportedResult`, `GenieErrorCodes.StrictResultIncomplete`). The
constants are public so callers can branch on `GenieException.ErrorCode` without copying string
literals. A budget is exhausted only by rows that arrive: an advertised next chunk is still
fetched when the row budget is used up, and only a non-empty chunk then fails. No partial result escapes from
the legacy chunk catch. Incompleteness includes server truncation, manifest row discrepancies,
missing inline data, external links, failed statement status, missing/unsafe/repeated continuation,
and exhausted re-execution. Missing inline data is tolerated only where it is provably empty: a
chunk without `data_array` whose `row_count` is explicitly 0, or a statement without `result`
whose manifest `total_row_count` is explicitly 0. Pending result reads may return null; that is
unavailable, never complete. Ordinary HTTP failures and cancellation propagate. A successful fetched table is
complete according to the available server metadata, not proof of SQL correctness or data freshness.

Strict normalization compares explicit space/conversation/message IDs, including message `id`,
against the requested or accepted identity. Start also checks its nested conversation/message.
Omitted IDs remain compatible; a query attachment without an attachment ID leaves its table
unreachable, so strict Ask/FollowUp report it as `StrictResultIncomplete`. Explicit
null/empty/non-string values of the identity fields this client compares (`space_id`,
`conversation_id`, `message_id`, `id`, `attachment_id`, `statement_id`), duplicate JSON property
names (case-insensitively), repeated attachment IDs, incompatible row widths, a chunk index other
than the expected one, and a `next_chunk_index` that is not exactly one more than the current
chunk fail as `MalformedResponse`. Other `*_id` fields, such as the numeric `user_id` on messages,
are not identities this client checks and are not constrained. Strict mode can therefore reject upstream responses that
legacy mode accepts. Query attachment statement IDs are compared when supplied on both sides.

Continuation links remain internal and same-workspace validated. One resolver serves both modes:
it supplies the documented statement chunk fallback, rejects links outside the workspace or
carrying user info, and detects repeats by resolved URI. Legacy mode logs and truncates where
strict mode rejects. Strict errors do not include
payloads, returned IDs, continuation URLs, server error text, or transport exception details.
All strict operations share the existing limit of 1,000 continuation fetches per result operation,
including empty chunks and all attachments in a high-level response. There are no new dependencies,
retry policies, public interface members, state stores, or execution modes. The public
`GenieErrorCodes` class only names the two existing error-code values.

An offline fixture using real AddLakeSpeak/HttpClientFactory registration at Trace reproduced
bearer-header and signed-continuation-query leakage through .NET 8's built-in HTTP logging,
including formatted messages and scopes. AddLakeSpeak removes all previously registered HTTP
loggers for its named client in both modes, including defaults and earlier custom loggers.
Applications that need custom HTTP factory logging must configure it after AddLakeSpeak.
LakeSpeak's own state diagnostics and the configured authentication, resilience and primary
handlers remain available. Custom external HttpClients, handlers, host telemetry or loggers
re-enabled after registration are outside this protection; hosts must keep credentials and
signed URLs out of those logs. This security correction intentionally changes default and
pre-registered custom HTTP factory logging; default result/submission behavior stays unchanged.

The statement status values and result relationship were checked against the official
[Statement Execution response reference](https://docs.databricks.com/api/statement-execution/v1/get-statement-result)
on 2026-11-17. The SDK's existing nullable wire contract remains authoritative for omitted fields.

## Consequences

Consumers opt into rejection and explicit finite budgets. They can inspect every query without
changing old integrations. Large single chunks are bounded before JSON deserialization by bytes
and rejected after parsing if their row count exceeds the remaining budget. Byte counting does
not claim a row-dependent parser or heap bound. Offline fixtures and packaged consumers cover
both target frameworks; no live Databricks compatibility or answer-accuracy claim follows.
