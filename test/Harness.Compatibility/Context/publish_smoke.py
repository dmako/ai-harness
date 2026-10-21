"""Exercise published get_context with an isolated synthetic provider and state."""
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
    executable = args.executable.resolve()
    artifacts = args.artifacts.resolve()
    root = artifacts / "context-publish-smoke" / uuid.uuid4().hex
    root.mkdir(parents=True)
    (root / "Widget.cs").write_text("class Widget {}", encoding="utf-8")
    state = root / ".harness"
    state.mkdir()
    provider = Path(__file__).resolve().parents[1] / "fake_provider.py"
    configuration = ('[providers.fixture]\ntype = "harness"\ncommand = ' + json.dumps(sys.executable) +
                     '\nargs = ' + json.dumps([str(provider), "--log", str(root / "provider.jsonl")]) +
                     '\ndir = "project"\n\n[providers.heavy]\npack = "graphify-csharp"\ncommand = "does-not-exist-context-heavy"\n')
    (state / "config.toml").write_text(configuration, encoding="utf-8")
    environment = dict(os.environ, HARNESS_CONFIG_DIR=str(root / "global"),
                       DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(artifacts / "context-bundle"),
                       GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(root / "absent.gitconfig"))
    for name in ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]:
        environment.pop(name, None)
    (root / ".gitignore").write_text(".harness/\nglobal/\nprovider.jsonl\n", encoding="utf-8")
    for arguments in [["init", "-b", "main"], ["add", "Widget.cs", ".gitignore"],
                      ["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "synthetic Widget"]]:
        result = subprocess.run(["git", "-C", str(root), *arguments], env=environment, capture_output=True, timeout=30)
        assert result.returncode == 0, result.stderr
    results = []

    def request(peer, arguments):
        result = peer.call("get_context", arguments)["result"]
        assert not result.get("isError"), result
        text = result["content"][0]["text"]
        output = result["structuredContent"]
        assert json.loads(text) == output
        assert len(text.encode("utf-8")) == output["used_bytes"] <= output["budget_bytes"]
        assert output["code"] and output["code"][0]["units"][0]["providers"] == ["fixture"], output
        results.append({"budget": output["budget_bytes"], "used": output["used_bytes"], "debug": "debug" in output})
        return output

    with closing(Peer([str(executable), "mcp", "--project", str(root)], root, environment)) as peer:
        peer.initialize()
        assert any(tool["name"] == "get_context" for tool in peer.request("tools/list")["result"]["tools"])
        for arguments in [{"task": "Widget"}, {"task": "Widget é😀 <tag> & quoted", "budget": 120, "debug": True}, {"task": "Widget", "tokens": 500, "budget": 9000}]:
            request(peer, arguments)
    assert not (state / "harness.db").exists()
    initialized = subprocess.run([str(executable), "init", "--project", str(root)], env=environment, capture_output=True, text=True, timeout=30)
    assert initialized.returncode == 0, initialized.stderr
    with closing(Peer([str(executable), "mcp", "--project", str(root)], root, environment)) as peer:
        peer.initialize()
        put = peer.call("memory_put", {"type": "decision", "content": "Widget operations remain deterministic"})["result"]
        assert not put.get("isError"), put
        output = request(peer, {"task": "Widget", "debug": True})
        assert any(memory.get("content") == "Widget operations remain deterministic" for memory in output["memory"])
    receipt = {"cases": results, "uninitialized_read_only": True, "initialized_memory": True, "excluded_heavy_provider_not_started": True}
    (artifacts / "context-published-smoke.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Published get_context: byte/token/debug budgets, Unicode, provenance, lazy state and initialized memory passed; excluded heavy executable was never started.")


if __name__ == "__main__":
    main()
