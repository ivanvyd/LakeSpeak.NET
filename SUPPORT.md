# Support

LakeSpeak.NET is maintained by one person in their own time. There is no SLA, and it is not a
Databricks product — Databricks support cannot help with it.

## Before opening anything

Two documents answer most questions, and both are kept honest rather than optimistic:

- [docs/troubleshooting.md](docs/troubleshooting.md) — specific errors and what causes them.
- [docs/limitations.md](docs/limitations.md) — what this tool deliberately does not do.

`lakespeak auth check` and `lakespeak config show` between them tell you which profile is in play,
which workspace it resolves to, whether a token can be obtained, and whether the workspace answers.
Neither prints any part of a credential, so both are safe to paste.

## Where to go

| You have | Use |
|---|---|
| A bug — it behaves differently from the docs | [Bug report](https://github.com/ivanvyd/LakeSpeak.NET/issues/new?template=bug_report.yml) |
| A feature idea | [Feature request](https://github.com/ivanvyd/LakeSpeak.NET/issues/new?template=feature_request.yml), after reading [GOVERNANCE.md](GOVERNANCE.md) |
| A question | [Discussions](https://github.com/ivanvyd/LakeSpeak.NET/discussions) |
| A security vulnerability | [Private advisory](https://github.com/ivanvyd/LakeSpeak.NET/security/advisories/new) — never a public issue |

## What to expect

Best effort, usually within a week. Security reports get a faster path — see
[SECURITY.md](SECURITY.md).

If a question is really about Genie itself — why an answer is wrong, why an Agent cannot see a
table — that is a Databricks question. LakeSpeak passes your identity through and can neither
widen nor correct what Genie returns.

## Paste with care

Questions, answers and query results can contain your organisation's data. LakeSpeak redacts
credentials from its own output; it has no way to know which of your table names are sensitive.
