## What this changes

<!-- What behaves differently after this merges, in plain language. -->

## Why

<!-- The problem it solves. If it fixes a bug, say what the bug actually did. -->

## Evidence

<!--
How you know it works. This project's standard is output from the session that made the change,
not an assertion.

For anything that could regress: revert the fix, watch the test fail, restore it, watch it pass —
and say so here. That minute is the difference between a regression test and a comment shaped
like one.
-->

## Checklist

- [ ] `dotnet build -c Release` is clean — warnings are errors
- [ ] `dotnet format --verify-no-changes` is clean
- [ ] `dotnet test -c Release --filter "Category!=Live"` passes
- [ ] Lock files regenerated with `dotnet restore --force-evaluate` if dependencies changed
- [ ] Tests use Arrange / Act / Assert with explicit section markers
- [ ] An ADR is included if this changes architecture or the public API of `LakeSpeak.Genie`
- [ ] No comment asserts a guarantee the code does not actually provide

## What a reviewer should be sceptical of

<!--
Where you were unsure, what you did not test, what you would attack first. "Nothing" is allowed
but is rarely true.
-->
