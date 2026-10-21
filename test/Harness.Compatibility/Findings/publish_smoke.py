"""Exercise published finding reads and pending proposals with synthetic project state."""
import argparse
from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from compatibility import Peer


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    args = parser.parse_args()
    binary = args.executable.resolve()
    artifacts = args.artifacts.resolve()
    root = artifacts / "findings-publish-smoke" / uuid.uuid4().hex
    root.mkdir(parents=True)
    env = dict(os.environ, HARNESS_CONFIG_DIR=str(root / "global"),
               DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(artifacts / "finding-bundle"),
               GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(root / "absent.gitconfig"))
    for name in ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]:
        env.pop(name, None)

    def run(arguments):
        result = subprocess.run(arguments, env=env, capture_output=True, text=True, encoding="utf-8", timeout=60)
        assert result.returncode == 0, (arguments, result.stdout, result.stderr)
        return result.stdout

    run(["git", "-C", str(root), "init", "-b", "main"])
    (root / ".gitignore").write_text(".harness/\nglobal/\n", encoding="utf-8")
    for name, references in [("Domain", '<ItemGroup><ProjectReference Include="../Dal/Dal.csproj" /></ItemGroup>'), ("Dal", "")]:
        directory = root / "src" / name
        directory.mkdir(parents=True)
        (directory / (name + ".csproj")).write_text('<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>' + references + '</Project>', encoding="utf-8")
    run([str(binary), "init", "--project", str(root)])
    with closing(Peer([str(binary), "mcp", "--project", str(root)], root, env)) as peer:
        peer.initialize()
        assert len(peer.request("tools/list")["result"]["tools"]) == 30

        def call(name, arguments):
            result = peer.call(name, arguments)["result"]
            assert not result.get("isError"), result
            assert json.loads(result["content"][0]["text"]) == result["structuredContent"]
            return result["structuredContent"]

        call("memory_put", {"type": "constraint", "content": "forbid: Domain -> Dal", "force": True})
        found = call("architecture_findings", {"kind": "boundary", "snippets": True})
        finding = found["groups"][0]["findings"][0]
        assert finding["kind"] == "boundary" and found["groups"][0]["code"], found
        proposal = call("architecture_propose", {"kind": "by_design", "finding": finding["key"], "reason": "Deliberate compatibility boundary"})
        assert proposal["status"] == "pending" and proposal["proposal"]["status"] == "pending", proposal
        same = call("architecture_propose", {"kind": "by_design", "finding": finding["key"], "reason": "Another reason"})
        assert same["status"] == "exists", same
        observation = call("architecture_propose", {"kind": "task", "title": "Review policy", "reason": "Consolidate shared policy", "evidence": ["src/Domain/Domain.csproj:1"]})
        assert observation["proposal"]["basis"] == "observation", observation
    output = run([str(binary), "sync", "--project", str(root)])
    assert "Findings  project:" in output and "new," in output, output
    with sqlite3.connect(root / ".harness" / "harness.db") as db:
        assert db.execute("select count(*) from finding_proposals where status='pending'").fetchone()[0] == 2
        assert db.execute("select count(*) from finding_marks").fetchone()[0] == 0
        assert db.execute("select count(*) from tasks").fetchone()[0] == 0
        assert db.execute("select count(*) from memories where type='decision'").fetchone()[0] == 0
        assert db.execute("select count(*) from finding_snapshots").fetchone()[0] == 1
    receipt = {"public_tools": 30, "boundary_with_snippet": True, "pending_by_design": True,
               "pending_observation": True, "deduplicated_proposal": True, "no_task_mark_or_decision_created": True,
               "sync_snapshot_and_cli": True, "text_structured_agreement": True}
    (artifacts / "findings-published-smoke.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Published findings MCP: boundary evidence, pending proposals, deduplication and sync snapshot passed.")


if __name__ == "__main__":
    main()
