#!/usr/bin/env python3
"""Check CI package identity, restore diagnostics and maintenance shell inputs."""

import argparse
import os
from pathlib import Path
import re
import subprocess
import textwrap


ROOT = Path(__file__).resolve().parents[2]
CI = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
REFRESH = (ROOT / ".github/workflows/refresh-lock-files.yml").read_text(encoding="utf-8")


def require(text: str, fragment: str, message: str) -> None:
    if fragment not in text:
        raise AssertionError(message)


def forbid(text: str, fragment: str, message: str) -> None:
    if fragment in text:
        raise AssertionError(message)


def run(bash: str, script: str, **values: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run([bash, "-c", script], cwd=ROOT, env=dict(os.environ, **values),
                          capture_output=True, text=True, check=False, timeout=15)


def check_restore_diagnostics(bash: str) -> None:
    scripts = re.findall(r"      - name: Restore\n(.*?)(?=\n      - )", CI, re.S)
    if len(scripts) != 2:
        raise AssertionError("both restore paths must remain covered")
    for block in scripts:
        script = textwrap.dedent(block.split("        run: |\n", 1)[1])
        for tfm in ("net8.0", "net10.0"):
            rendered = script.replace("${{ matrix.tfm }}", tfm)
            slnx = "tests/libraries-only.slnx" if "slnx=" in script and tfm == "net8.0" else "LakeSpeak.slnx"
            stub = 'dotnet() { [ "$*" = "restore $EXPECTED_SLNX --locked-mode" ] || return 99; return "$RESTORE_EXIT"; }\n'
            for branch in ("dependabot/nuget/example", "audit-$(printf${IFS}INJECTION_MARKER)",
                           "audit-`printf${IFS}INJECTION_MARKER`"):
                subprocess.run(["git", "check-ref-format", "--branch", branch],
                               capture_output=True, check=True)
                candidate = rendered.replace("${{ github.head_ref || github.ref_name }}", branch)
                for code in ("0", "1"):
                    result = run(bash, stub + candidate, EXPECTED_SLNX=slnx, RESTORE_EXIT=code)
                    if result.returncode != int(code):
                        raise AssertionError(result.stderr or result.stdout)
                    if code == "0" and result.stdout:
                        raise AssertionError("successful restore must not print failure guidance")
                    if code == "1":
                        require(result.stdout, "::error title=Lock files are out of step::", "missing restore failure diagnostic")
                        require(result.stdout, "from the default branch, with the Dependabot branch as its input", "misleading dispatch guidance")
                    forbid(result.stdout, "INJECTION_MARKER", "branch input executed as shell code")


def check_branch_input(bash: str) -> None:
    block = REFRESH.split("      - name: Validate the maintenance branch\n", 1)[1].split("\n      - ", 1)[0]
    require(block, "BRANCH: ${{ inputs.branch }}", "branch must be passed as environment data")
    script = textwrap.dedent(block.split("        run: |\n", 1)[1])
    for branch, accepted in (("dependabot/nuget/example", True), ("main", False),
                             ("fix/example", False), ("dependabot/nuget/a..b", False),
                             ("dependabot/nuget/a$(printf${IFS}INJECTION_MARKER)", True)):
        result = run(bash, script, BRANCH=branch)
        if (result.returncode == 0) != accepted:
            raise AssertionError(result.stderr or result.stdout)
        forbid(result.stdout, "aINJECTION_MARKER", "maintenance branch executed as shell code")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bash", default="bash")
    args = parser.parse_args()
    require(CI, "-p:VersionSuffix=ci.${{ github.run_id }}.${{ github.run_attempt }}",
            "CI packages must have a run-specific identity distinct from published stable packages")
    require(CI, '[[ "$version" == *"-ci.${{ github.run_id }}.${{ github.run_attempt }}" ]]',
            "tool smoke must require the current run's candidate identity")
    require(CI, 'test "${actual_version%%+*}" = "$version"',
            "tool smoke must assert the installed candidate version")
    require(CI, "  workflow_dispatch:\n", "CI must retain manual diagnostics")
    guidance = ("Run the 'Refresh lock files' workflow from the default branch, "
                "with the Dependabot branch as its input, then re-run these checks.")
    if CI.count(guidance) != 2:
        raise AssertionError("both locked restore paths must guide Dependabot updates safely")
    forbid(CI, "${{ github.head_ref || github.ref_name }}",
           "CI failure guidance must not interpolate an untrusted branch name into Bash")
    require(CI, "python .github/scripts/test-refresh-lock-files.py", "CI must exercise the real Git maintenance protocol")
    forbid(REFRESH, "persist-credentials: true", "checkout credentials must stay disabled")
    forbid(REFRESH, "secrets.PAT", "the workflow must not introduce a long-lived PAT")
    check_restore_diagnostics(args.bash)
    check_branch_input(args.bash)
    print("maintenance workflow policy passed: CI identity, both restore diagnostics, branch shell data")


if __name__ == "__main__":
    main()
