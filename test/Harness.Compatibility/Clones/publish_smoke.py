"""Smoke-test published change_coupling without providers or development runtimes."""
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
    root = artifacts / "clones-publish-smoke" / uuid.uuid4().hex
    root.mkdir(parents=True)
    env = dict(os.environ, HARNESS_CONFIG_DIR=str(root / "global"),
               DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(artifacts / "clone-bundle"),
               GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(root / "absent.gitconfig"))
    for name in ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]:
        env.pop(name, None)

    def run(arguments):
        result = subprocess.run(arguments, env=env, capture_output=True, text=True, encoding="utf-8", timeout=45)
        assert result.returncode == 0, (arguments, result.stdout, result.stderr)

    def git(*arguments):
        run(["git", "-C", str(root), *arguments])

    git("init", "-b", "main")
    git("config", "user.name", "Test")
    git("config", "user.email", "test@example.invalid")
    (root / ".gitignore").write_text(".harness/\nglobal/\n", encoding="utf-8")
    for index in range(5):
        (root / "a.cs").write_text("// synthetic " + str(index), encoding="utf-8")
        git("add", ".")
        git("commit", "-m", "synthetic history")
    run([str(binary), "init", "--project", str(root)])
    (root / "a.cs").write_text("// uncommitted", encoding="utf-8")
    with closing(Peer([str(binary), "mcp", "--project", str(root)], root, env)) as peer:
        peer.initialize()

        def call(arguments):
            result = peer.call("change_coupling", arguments)["result"]
            assert not result.get("isError"), result
            assert json.loads(result["content"][0]["text"]) == result["structuredContent"]
            return result["structuredContent"]

        hot = call({})
        assert hot["commits"] == 5 and hot["hotspots"][0]["path"] == "a.cs", hot
        assert hot["hotspots"][0]["commits"] == 5, hot
        assert "partners are unknown" in hot["note"], hot
        checked = call({"check": True})
        assert checked["changed"] == ["a.cs"], checked
        assert "no usual partner is left out" in checked["note"], checked
        file = call({"file": "./a.cs", "check": True})
        assert "changed" not in file and "partners are unknown" in file["note"], file
    receipt = {"published_change_coupling": True, "git_hotspots": True, "dirty_paths": True,
               "file_precedence": True, "unknown_provider_note": True, "text_structured_agreement": True}
    (artifacts / "clones-published-smoke.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Published co-change MCP: hotspots, dirty paths, precedence and unknown-provider notes passed.")


if __name__ == "__main__":
    main()
