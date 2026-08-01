# Limitations

What to know before relying on this.

## It cannot make an answer correct

Genie turns a natural-language question into SQL. That SQL can be wrong in ways that read as
entirely plausible: a subtly wrong join, a date boundary off by one, a filter that quietly excludes
cancelled orders. The answer is still a confident sentence with a number in it.

LakeSpeak preserves the generated SQL, the query result and the message identifiers precisely so a
person can check. It has no way to judge correctness, and neither does any other client.

## Genie may answer with a question

Genie sometimes responds by asking for clarification rather than answering — for example, "Would
you prefer to see the active customers for exactly the quarter '2026-Q1' instead of any quarter
containing '2026-Q1'?" This is observed behaviour, not a hypothetical; it happened on the second
question of the first Question Pack ever run against a live workspace.

The message still completes successfully and the clarification arrives as the answer text, because
that is genuinely what Genie returned. LakeSpeak does not try to detect it: distinguishing "a
question back" from "an answer phrased as a question" is a judgement call, and guessing wrong in
either direction is worse than reporting faithfully.

The consequence for automation is worth planning around. An unattended Question Pack can produce a
report whose answer is a request for clarification, and it will exit `0` because nothing failed.
Phrase pack questions to be unambiguous, and read reports rather than trusting the exit code alone.

## It cannot see more than you can

Every request carries your identity, and Unity Catalog decides what that identity sees. There is no
service-account mode and no impersonation, so LakeSpeak cannot widen your access — and cannot
narrow it either. If you can see more than you expected, that is a workspace governance question
rather than a LakeSpeak one.

## Conversation state lives in Databricks

There is no local conversation database. History therefore survives across machines, and is also
subject to whatever retention Databricks applies. LakeSpeak stores only a pointer: profile, Agent
id, conversation id.

## The API is Public Preview

Fields can appear and statuses can be added. The client tolerates that — an unrecognised status maps
to `Unknown` and is treated as non-terminal rather than throwing — but a large enough change will
still break it. [`planning/genie-api-surface.md`](planning/genie-api-surface.md) records which parts
of the contract are verified and which are not.

## Authentication is user-to-machine first

v0.1 brokers OAuth tokens through the Databricks CLI, which covers U2M profiles.
`databricks auth token` does **not** support OAuth M2M client-credential profiles. For unattended
use, supply `DATABRICKS_TOKEN` — noting that a personal access token is a standing credential with
no refresh, which Databricks documents as a local-debugging path rather than a production one.
Native M2M is on the roadmap.

## Exports are yours to look after

An exported CSV is an ordinary file containing governed data. LakeSpeak warns before writing and
refuses to overwrite without confirmation; it cannot protect the file afterwards. In CI, remember
that job logs are usually readable by everyone with repository access.

## Large results are returned as a first chunk, flagged

The Statement Execution contract splits large results into chunks. v0.1 reads only the first one.

What it does **not** do is pretend that is the whole result. A response carrying a
`next_chunk_index`, or fewer rows than the manifest's total, is reported as **truncated** — in the
terminal, in the JSON `truncated` field, and in Question Pack reports. So an export can be
incomplete, but it is never *silently* incomplete.

This was wrong until a post-ship review caught it: the client relied on `manifest.truncated`, which
reports statement-level truncation by Databricks and is `false` for a merely-chunked result. A large
result was returned as its first chunk labelled complete. Fetching remaining chunks is v0.2 work.

## Not implemented in v0.1

Full-result downloads beyond the first chunk, visualization rendering, conversation list and resume
commands, OAuth M2M, and an MCP server mode. See [ROADMAP.md](../ROADMAP.md) for what is planned and
[GOVERNANCE.md](../GOVERNANCE.md) for what is deliberately out of scope.
