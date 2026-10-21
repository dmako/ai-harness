"""Exercise local telemetry through the published MCP server and CLI."""
import argparse
from contextlib import closing
import json
import os
from pathlib import Path
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
    root = artifacts / "telemetry-publish-smoke" / uuid.uuid4().hex
    root.mkdir(parents=True)
    env = dict(os.environ, HARNESS_CONFIG_DIR=str(root / "global"),
               DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(artifacts / "telemetry-bundle"),
               GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(root / "absent.gitconfig"))
    for name in ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]:
        env.pop(name, None)

    def run(arguments):
        result = subprocess.run(arguments, env=env, capture_output=True, text=True, encoding="utf-8", timeout=60)
        assert result.returncode == 0, (arguments, result.stdout, result.stderr)
        return result.stdout

    run(["git", "-C", str(root), "init", "-b", "main"])
    (root / ".gitignore").write_text(".harness/\nglobal/\n", encoding="utf-8")
    run([str(binary), "init", "--project", str(root)])
    with closing(Peer([str(binary), "mcp", "--project", str(root)], root, env)) as peer:
        peer.initialize()
        assert len(peer.request("tools/list")["result"]["tools"]) == 30
        assert not peer.call("ping", {})["result"].get("isError")
        assert not peer.call("search_project", {"query": "Widget"})["result"].get("isError")
        assert peer.call("search_project", {"query": 1})["result"].get("isError")
    metrics = json.loads(run([str(binary), "metrics", "--project", str(root), "--json"]))
    assert sum(row["calls"] for row in metrics["tools"]) == 3, metrics
    assert sum(row["errors"] for row in metrics["tools"]) == 1, metrics
    stats = json.loads(run([str(binary), "context", "stats", "--project", str(root), "--json"]))
    assert stats["calls"] == 0, stats
    report = json.loads(run([str(binary), "report", "--project", str(root), "--json"]))
    assert len(report["projects"]) == 1 and report["projects"][0]["findings"], report
    all_reports = json.loads(run([str(binary), "report", "--all", "--json"]))
    assert len(all_reports["projects"]) == 1, all_reports
    assert "MCP tools over the last 30 days" in run([str(binary), "metrics", "--project", str(root)])
    run([str(binary), "context", "stats", "--project", str(root)])
    run([str(binary), "report", "--project", str(root)])
    receipt = {"public_tools": 30, "flushed_calls": 3, "errors": 1,
               "context_stats": True, "report": True, "all_projects": True, "text_and_json": True}
    (artifacts / "telemetry-published-smoke.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Published telemetry: MCP disconnect flush, metrics, context stats and reports passed.")


if __name__ == "__main__":
    main()
