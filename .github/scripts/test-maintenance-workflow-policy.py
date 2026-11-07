#!/usr/bin/env python3
"""Static invariants for CI package identity and lock-refresh permissions."""

import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap


ROOT = Path(__file__).resolve().parents[2]
CI = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
REFRESH = (ROOT / ".github/workflows/refresh-lock-files.yml").read_text(encoding="utf-8")


def require(text: str, fragment: str, message: str) -> int:
    position = text.find(fragment)
    if position < 0:
        raise AssertionError(message)
    return position


def forbid(text: str, fragment: str, message: str) -> None:
    if fragment in text:
        raise AssertionError(message)


def check_lock_only_commit(refresh: str, bash: str) -> None:
    block = refresh.split("      - name: Commit the refreshed lock files\n", 1)[1]
    script = textwrap.dedent(block.split("        run: |\n", 1)[1].split("\n      - name:", 1)[0])
    script = script[:script.index("gh auth setup-git")]
    with tempfile.TemporaryDirectory() as directory:
        fixture = Path(directory)

        def git(*arguments: str) -> subprocess.CompletedProcess[str]:
            return subprocess.run(["git", *arguments], cwd=fixture, capture_output=True,
                                  text=True, check=True)

        git("init", "-q")
        git("config", "user.name", "Fixture")
        git("config", "user.email", "fixture@example.invalid")
        git("config", "commit.gpgsign", "false")
        lock = fixture / "packages.lock.json"
        other = fixture / "unrelated.txt"
        lock.write_text("{}\n", encoding="utf-8")
        other.write_text("before\n", encoding="utf-8")
        git("add", "packages.lock.json", "unrelated.txt")
        git("commit", "-qm", "fixture")
        lock.write_text('{"version": 1}\n', encoding="utf-8")
        other.write_text("staged elsewhere\n", encoding="utf-8")
        git("add", "unrelated.txt")
        result = subprocess.run([bash, "-c", script], cwd=fixture,
                                env=dict(os.environ, BRANCH="dependabot/nuget/example"),
                                capture_output=True, text=True, check=False, timeout=15)
        if result.returncode:
            raise AssertionError(result.stderr)
        committed = git("diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD").stdout.strip()
        if committed != "packages.lock.json":
            raise AssertionError(f"Unrelated staged content entered the commit: {committed}")
        if git("diff", "--cached", "--name-only").stdout.strip() != "unrelated.txt":
            raise AssertionError("The refresh consumed or discarded unrelated staged content")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bash", default="bash")
    args = parser.parse_args()
    require(
        CI,
        "-p:VersionSuffix=ci.${{ github.run_id }}.${{ github.run_attempt }}",
        "CI packages must have a run-specific identity distinct from published stable packages",
    )
    require(
        CI,
        '[[ "$version" == *"-ci.${{ github.run_id }}.${{ github.run_attempt }}" ]]',
        "tool smoke must require the current run's candidate identity",
    )
    require(
        CI,
        'test "${actual_version%%+*}" = "$version"',
        "tool smoke must assert the installed candidate version",
    )
    require(CI, "  workflow_dispatch:\n", "CI must accept the post-refresh dispatch")
    refresh_guidance = (
        "Run the 'Refresh lock files' workflow using the Dependabot branch, then re-run these checks."
    )
    if CI.count(refresh_guidance) != 2:
        raise AssertionError("both locked restore paths must guide Dependabot updates safely")
    forbid(
        CI,
        "${{ github.head_ref || github.ref_name }}",
        "CI failure guidance must not interpolate an untrusted branch name into Bash",
    )

    validate = require(
        REFRESH,
        "      - name: Validate the maintenance branch\n",
        "the branch must be validated",
    )
    checkout = require(
        REFRESH,
        "      - uses: actions/checkout@",
        "the refresh workflow must check out the requested branch",
    )
    restore = require(
        REFRESH,
        "      - run: dotnet restore --force-evaluate\n",
        "the refresh workflow must regenerate lock files",
    )
    build = require(
        REFRESH,
        "      - run: dotnet build --no-restore -c Release\n",
        "the regenerated graph must build before it is written",
    )
    credentials = require(
        REFRESH,
        "          GH_TOKEN: ${{ github.token }}\n",
        "the bounded write step must receive the repository token",
    )
    push = require(
        REFRESH,
        '          git push origin "HEAD:refs/heads/$BRANCH"\n',
        "the verified lock files must be pushed to the same maintenance branch",
    )
    dispatch = require(
        REFRESH,
        '        run: gh workflow run ci.yml --ref "$BRANCH"\n',
        "CI must be dispatched for the new commit",
    )

    if not validate < checkout < restore < build < credentials < push < dispatch:
        raise AssertionError(
            "branch validation, unprivileged checks, bounded credentials, push, and CI dispatch "
            "must remain in that order"
        )

    require(
        REFRESH,
        "          persist-credentials: false\n",
        "checkout must not expose a write-capable token to restore or build",
    )
    require(
        REFRESH,
        "            dependabot/nuget/*) ;;\n",
        "only NuGet Dependabot branches may be refreshed",
    )
    require(
        REFRESH,
        "          git add -- '*packages.lock.json'\n",
        "the write step must stage only lock files",
    )
    require(REFRESH, "      actions: write\n", "dispatching CI requires actions: write")
    require(REFRESH, "          gh auth setup-git\n", "the bounded push step must configure GitHub auth")

    forbid(REFRESH, "persist-credentials: true", "checkout credentials must stay disabled")
    forbid(REFRESH, "\n          git add -A", "the workflow must not stage unrelated files")
    forbid(REFRESH, "secrets.PAT", "the workflow must not introduce a long-lived PAT")

    require(REFRESH, "ref: refs/heads/${{ inputs.branch }}", "checkout must select a branch, never a same-named tag")
    require(REFRESH, "git commit --only", "the commit must exclude unrelated staged files")
    check_lock_only_commit(REFRESH, args.bash)

    print("maintenance workflow policy test passed")


if __name__ == "__main__":
    main()
