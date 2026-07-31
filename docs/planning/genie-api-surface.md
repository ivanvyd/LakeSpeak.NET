# Genie Conversation API surface

Every field name and path below was read from the generated Databricks Python SDK
(`databricks/sdk/service/dashboards.py`, `databricks-sdk-py@main`), which is the most reliable
published record of the wire contract. Prose documentation was not treated as authoritative where
the two could disagree.

Anything marked UNVERIFIED was not confirmed from that source. The client tolerates those rather
than hard-coding them.

## Terminology

The public product name is "Genie Agent". **The API is entirely `space`-based**: every path segment
is `spaces`, and every identifier is `space_id`. There is no `agents` path.

`LakeSpeak.Genie` therefore exposes `GenieAgent` / `AgentId` in its public surface and keeps
`space_id` in the wire DTOs. The mapping happens once, at the serialization boundary.

## Endpoints

All under `/api/2.0/genie`. VERIFIED unless noted.

| Operation | Verb | Path |
|---|---|---|
| List spaces | GET | `/spaces` |
| Get space | GET | `/spaces/{space_id}` |
| Start conversation | POST | `/spaces/{space_id}/start-conversation` |
| Create follow-up message | POST | `/spaces/{space_id}/conversations/{conversation_id}/messages` |
| Get message | GET | `/spaces/{space_id}/conversations/{conversation_id}/messages/{message_id}` |
| Get attachment query result | GET | `…/messages/{message_id}/attachments/{attachment_id}/query-result` |
| Re-execute attachment query | POST | `…/attachments/{attachment_id}/execute-query` |
| Generate full-result download | POST | `…/attachments/{attachment_id}/downloads` |
| Get full-result download | GET | `…/attachments/{attachment_id}/downloads/{download_id}` |
| Send message feedback | POST | `…/messages/{message_id}/feedback` |
| List conversations | GET | `/spaces/{space_id}/conversations` |
| List conversation messages | GET | `/spaces/{space_id}/conversations/{conversation_id}/messages` |

Not used by v0.1, and listed so nobody re-derives them: `create_space`, `update_space`,
`trash_space`, `delete_conversation`, the `eval-runs/*` family, and the comment endpoints. Creating
or optimising Agents is out of scope per `GOVERNANCE.md`.

`download_message_attachment_visualization` is `GET /api/2.0/genie/{name}/download-visualization`.
The `{name}` segment does not follow the pattern of any other endpoint. **UNVERIFIED** — not used
in v0.1.

## Message status

Ten values, VERIFIED against the SDK enum:

```
SUBMITTED  FILTERING_CONTEXT  FETCHING_METADATA  ASKING_AI  PENDING_WAREHOUSE
EXECUTING_QUERY  COMPLETED  FAILED  CANCELLED  QUERY_RESULT_EXPIRED
```

Terminal: `COMPLETED`, `FAILED`, `CANCELLED`. Polling stops on these.

`QUERY_RESULT_EXPIRED` is **not** a failure. The message succeeded; its cached SQL result aged out.
Recovery is `execute-query` on the attachment, not re-asking the question.

Databricks documents the set as open-ended, so the client maps to a closed internal enum with an
`Unknown` arm rather than switching exhaustively over platform states. A status added by Databricks
next month must not crash a released client.

## Response shapes

### GenieMessage

Required: `space_id`, `conversation_id`, `message_id`, `content`.
Optional: `attachments[]`, `status`, `error`, `feedback`, `query_result`, `created_timestamp`,
`last_updated_timestamp`, `user_id`, `id`.

`content` is the **question that was asked**, not the answer. The answer arrives in
`attachments[].text.content`. Reading `content` as the answer is the single easiest mistake to make
against this API.

### GenieAttachment

`attachment_id`, plus exactly one populated variant: `text`, `query`, `suggested_questions`, `viz`.

`suggested_questions` is a fourth variant that most descriptions of this API omit.

### GenieQueryAttachment

`query` (**the generated SQL — the field is `query`, not `sql`**), `title`, `description`,
`statement_id`, `parameters[]`, `query_result_metadata`, `thoughts[]`, `id`,
`last_updated_timestamp`.

### TextAttachment

`content`, `id`, `phase` (`RESPONSE_PHASE_THINKING` | `RESPONSE_PHASE_VERIFYING`),
`purpose` (`FOLLOW_UP_QUESTION`), `verification_metadata` (`index`, `section`).

### Query result

`GenieGetMessageQueryResultResponse` has exactly one field: `statement_response`, of type
`sql.StatementResponse`.

**The Genie query result reuses the SQL Statement Execution API contract**: `manifest` (schema and
column types), `result` (`data_array`, `external_links`, `chunk_index`, `row_count`), `status`.
Anything already known about that API applies here, including that presigned `external_links` URLs
must be fetched **without** the `Authorization` header.

`GenieResultMetadata`: `is_truncated`, `row_count`.

### Others

- `GenieSpace`: `space_id`, `title`, `description`, `warehouse_id`, `etag`, `parent_path`, `serialized_space`
- `GenieListSpacesResponse`: `spaces[]`, `next_page_token`
- `GenieStartConversationResponse`: `conversation_id`, `message_id`, `conversation`, `message`
- `GenieConversation`: `space_id`, `conversation_id`, `title`, timestamps, `user_id`
- `GenieFeedback`: `rating` (`POSITIVE` | `NEGATIVE` | `NONE`), `comment`
- `MessageError`: `error`, `type`
- `QueryAttachmentParameter`: `keyword`, `sql_type`, `value`
- `GenieGenerateDownloadFullQueryResultResponse`: `download_id`, `download_id_signature`

`download_id_signature` is a bearer-equivalent secret. It is redacted in all diagnostic output.

## Corrections to the original specification

The plan this project was built from proposed a model that does not match the API. These are
corrections, not preferences:

| Plan said | Reality |
|---|---|
| 7 message states (`Submitted`, `Processing`, `ExecutingQuery`, …) | 10 states. `FETCHING_METADATA`, `FILTERING_CONTEXT`, `ASKING_AI`, `PENDING_WAREHOUSE` and `QUERY_RESULT_EXPIRED` were all absent. |
| `GenieQuery.IsTrustedAsset` | **No such field exists** anywhere on the query attachment. Modelling it would have meant inventing a trust signal and showing it to users. Dropped. |
| Attachments are text / query / visualization | There is also `suggested_questions`. |
| Generated SQL in `sql` | The field is `query`. |
| Poll "every 1-5 seconds" | Consistent with the docs; the client defaults to 1s with backoff to 5s. |

## Not established

| Item | Status |
|---|---|
| Rate-limit response shape and retry-after semantics | UNVERIFIED. Client treats HTTP 429 as retryable and honours `Retry-After` when present. |
| Error body JSON shape (`error_code` / `message`) | UNVERIFIED for Genie specifically. Client parses defensively and falls back to the raw status. |
| `databricks auth token` output field names | UNVERIFIED from source. Parsed defensively; see the authentication ADR. |
| Behaviour against AWS and GCP workspaces | Not tested. Paths are identical, which is not evidence. |
