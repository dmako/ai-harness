"""Compare PORT-006 lifecycle contracts against the pinned Go executable on isolated state."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess

from compatibility import Sandbox, write_json


def run(candidate, sandbox, *arguments):
    result = subprocess.run(["dotnet", str(candidate), *arguments], cwd=sandbox.repository,
                            env=sandbox.environment, capture_output=True, timeout=30)
    output = result.stdout.decode("utf-8")
    try:
        output = json.loads(output)
    except ValueError:
        pass
    return {"exit_code": result.returncode, "stdout": output, "stderr": result.stderr.decode("utf-8")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--oracle", required=True, type=Path)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    candidate = args.candidate.resolve()
    sandbox = Sandbox(args.oracle)
    cases = []

    def status(name):
        go = sandbox.cli("status", "--json")
        cs = run(candidate, sandbox, "status", "--json")
        # Client detection and sync reports are owned by PORT-025/016. All lifecycle fields compare exactly.
        fields = ["project_root", "state_root", "project", "git", "git_error", "worktrees", "database", "config", "providers"]
        left = {key: go["stdout"][key] for key in fields if key in go["stdout"]}
        right = {key: cs["stdout"][key] for key in fields if key in cs["stdout"]}
        cases.append({"name": name, "oracle": left, "candidate": right,
                      "pass": go["exit_code"] == cs["exit_code"] and left == right})

    try:
        status("uninitialized")
        go = sandbox.cli("init", "--dry-run")
        cs = run(candidate, sandbox, "init", "--dry-run")
        cases.append({"name": "dry-run", "pass": go["exit_code"] == cs["exit_code"] == 0
                      and not (sandbox.repository / ".harness").exists()})
        assert sandbox.cli("init")["exit_code"] == 0
        status("go-initialized-identity-read-by-csharp")
        database = sandbox.repository / ".harness/harness.db"
        before = hashlib.sha256(database.read_bytes()).hexdigest()
        assert run(candidate, sandbox, "status", "--json")["exit_code"] == 0
        assert run(candidate, sandbox, "doctor", "--json")["exit_code"] == 0
        cases.append({"name": "inspection-preserves-database", "pass": before == hashlib.sha256(database.read_bytes()).hexdigest()})
        assert run(candidate, sandbox, "init")["exit_code"] == 0
        status("csharp-reregistered-identity-read-by-go")
        sandbox.git("checkout", "--detach")
        status("detached-head")
        linked = sandbox.root / "linked Ω space"
        sandbox.git("worktree", "add", "-b", "feature", str(linked))
        assert run(candidate, sandbox, "init", "--project", str(linked))["exit_code"] == 0
        previous = sandbox.repository
        sandbox.repository = linked
        status("linked-worktree-shared-state")
        sandbox.repository = previous
        path = sandbox.repository / ".harness/config.toml"
        path.write_text("[log]\nlevel = 'invalid'\n", encoding="utf-8")
        go = sandbox.cli("init")
        cs = run(candidate, sandbox, "init")
        cases.append({"name": "invalid-config-rejected", "pass": go["exit_code"] == cs["exit_code"] == 1
                      and "config.toml" in go["stderr"] and "config.toml" in cs["stderr"]})
        write_json(args.output, {"source_commit": "9cb401dd2de4f85dd089fea86a0b139d81b178c6",
                                "cases": cases, "passed": all(case["pass"] for case in cases),
                                "limits": ["Lifecycle status fields only; client detection and sync reports remain assigned to later tasks."]})
        print(json.dumps({"passed": sum(case["pass"] for case in cases), "total": len(cases)}))
        return 0 if all(case["pass"] for case in cases) else 1
    finally:
        sandbox.close()


if __name__ == "__main__":
    raise SystemExit(main())
