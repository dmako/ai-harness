"""Exercise published client setup in isolated homes, including paths with spaces."""
import argparse
from contextlib import closing
import json
import os
from pathlib import Path
import shutil
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
    artifacts = args.artifacts.resolve()
    home = artifacts / "clients-publish-smoke" / ("home with spaces " + uuid.uuid4().hex)
    root = home / "repository with spaces"
    root.mkdir(parents=True)
    binary = home / "bin with spaces" / args.executable.name
    binary.parent.mkdir()
    shutil.copy2(args.executable.resolve(), binary)
    claude = home / "claude" / ".claude.json"
    codex = home / "codex" / "config.toml"
    claude.parent.mkdir()
    codex.parent.mkdir()
    original = '{"theme":"preserve","mcpServers":{"other":{"command":"other"}}}\n'
    claude.write_text(original, encoding="utf-8")
    codex.write_text("# preserve\nmodel = 'fixture'\n", encoding="utf-8")
    env = dict(os.environ, HARNESS_CONFIG_DIR=str(home / "global"), HOME=str(home), USERPROFILE=str(home),
               CLAUDE_CONFIG_DIR=str(claude.parent), CODEX_HOME=str(codex.parent), ZDOTDIR=str(home),
               XDG_CONFIG_HOME=str(home / ".config"), DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(artifacts / "clients-bundle"),
               GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(home / "absent.gitconfig"))
    for name in ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]:
        env.pop(name, None)

    def run(arguments, code=0):
        result = subprocess.run(arguments, cwd=root, env=env, capture_output=True, text=True, encoding="utf-8", timeout=60)
        assert result.returncode == code, (arguments, result.stdout, result.stderr)
        return result.stdout

    def cli(*arguments, code=0):
        return run([str(binary), *arguments, "--project", str(root)], code)

    run(["git", "init", "-b", "main"])
    cli("init")
    before = {path: path.read_bytes() for path in [claude, codex]}
    assert "would add harness server" in cli("install", "--command", str(binary), "--dry-run")
    assert all(path.read_bytes() == data for path, data in before.items())
    assert "backup:" in cli("install", "--command", str(binary))
    assert "unchanged" in cli("install", "--command", str(binary))
    assert "unchanged" in cli("install", "--client", "codex")
    server = json.loads(claude.read_text(encoding="utf-8"))["mcpServers"]["harness"]
    assert server == {"type": "stdio", "command": str(binary), "args": ["mcp"]}, server
    with closing(Peer([server["command"], *server["args"]], root, env)) as peer:
        peer.initialize()
        assert len(peer.request("tools/list")["result"]["tools"]) == 30
        assert not peer.call("ping", {})["result"].get("isError")
    cli("install", "--client", "claude-code", "--scope", "project", "--command", str(binary))
    project = json.loads((root / ".mcp.json").read_text(encoding="utf-8"))
    assert project["mcpServers"]["harness"]["command"].startswith("${HOME}/"), project
    cli("install", "--client", "claude-code", "--scope", "local", "--command", str(binary))
    cli("instructions")
    assert "@AGENTS.md" in (root / "CLAUDE.md").read_text(encoding="utf-8")
    assert "up to date" in cli("instructions")
    assert "Run it in a terminal" in run([str(binary), "install"])
    run([str(binary), "uninstall", "--all", "--delete-data"], code=2)
    cli("uninstall", "--delete-data", code=2)
    assert "would delete" in cli("uninstall", "--all", "--delete-data", "--dry-run")
    assert (root / ".harness" / "harness.db").exists()
    assert "kept " in cli("uninstall", "--all")
    remaining = json.loads(claude.read_text(encoding="utf-8"))
    assert remaining["theme"] == "preserve" and remaining["mcpServers"] == {"other": {"command": "other"}}, remaining
    assert codex.read_bytes() == before[codex]
    assert not (root / "AGENTS.md").exists() and not (root / "CLAUDE.md").exists()
    assert "deleted " in cli("uninstall", "--all", "--delete-data")
    assert not (root / ".harness").exists() and binary.exists()
    receipt = {"space_path_launch": True, "dry_run": True, "backups": True, "idempotent": True,
               "scopes": ["user", "project", "local"], "public_tools": 30, "guidance": True,
               "nonterminal_status": True, "guarded_data_deletion": True, "foreign_values_preserved": True}
    (artifacts / "clients-published-smoke.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Published setup: all scopes, guidance, space-path MCP launch, safe removal and deletion guards passed.")


if __name__ == "__main__":
    main()
