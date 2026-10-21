"""Exercise published workspace CLI/MCP with synthetic repositories and isolated state."""
import argparse
from contextlib import closing
import hashlib
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
    root = artifacts / "workspace-publish-smoke" / uuid.uuid4().hex
    root.mkdir(parents=True)
    environment = dict(os.environ, HARNESS_CONFIG_DIR=str(root / "global"),
                       DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(artifacts / "workspace-bundle"),
                       GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(root / "absent.gitconfig"))
    for name in ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]:
        environment.pop(name, None)
    provider = Path(__file__).resolve().parents[1] / "fake_provider.py"
    repositories = []

    def run(command):
        result = subprocess.run(command, env=environment, capture_output=True, text=True, encoding="utf-8", timeout=45)
        assert result.returncode == 0, (command, result.stdout, result.stderr)
        return result.stdout

    def cli(*arguments):
        return run([str(executable), *arguments])

    for name in ["alpha", "beta", "gamma"]:
        repository = root / name
        repository.mkdir()
        repositories.append(repository)
        (repository / "Widget.cs").write_text("namespace Example; class Widget {}\n", encoding="utf-8")
        state = repository / ".harness"
        state.mkdir()
        configuration = ('[providers.fixture]\ntype = "harness"\ncommand = ' + json.dumps(sys.executable) +
                         '\nargs = ' + json.dumps([str(provider), "--log", str(state / "provider.jsonl")]) +
                         '\ndir = "project"\n[providers.heavy]\ntype = "harness"\ncontext = false\ncommand = "missing-workspace-heavy-fixture"\n')
        (state / "config.toml").write_text(configuration, encoding="utf-8")
        (repository / ".gitignore").write_text(".harness/\n", encoding="utf-8")
        for arguments in [["init", "-b", "main"], ["add", "Widget.cs", ".gitignore"],
                          ["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "synthetic Widget"]]:
            run(["git", "-C", str(repository), *arguments])
        cli("init", "--project", str(repository))
    cli("workspace", "create", "one", "--discover", str(repositories[0]))
    cli("workspace", "join", "one", "--project", str(repositories[1]))
    cli("workspace", "create", "two", "--discover", str(repositories[2]))
    rows = json.loads(cli("projects", "--json"))["projects"]
    assert [row["live"]["state"] for row in rows] == ["ok"] * 3
    assert "beta" in cli("workspaces")
    foreign = repositories[1] / ".harness" / "harness.db"
    before = hashlib.sha256(foreign.read_bytes()).hexdigest()

    def call(peer, name, arguments):
        result = peer.call(name, arguments)["result"]
        assert not result.get("isError"), result
        return result["structuredContent"]

    with closing(Peer([str(executable), "mcp", "--project", str(repositories[0])], repositories[0], environment)) as peer:
        peer.initialize()
        call(peer, "memory_put", {"type": "decision", "content": "Widget shared rule", "scope": "workspace:one"})
        search = call(peer, "search_project", {"query": "Widget", "kind": "symbol", "scope": "workspace"})
        assert [item["project"] for item in search["results"]] == ["alpha", "beta"], search
        assert not search.get("errors"), search
        selected = call(peer, "search_project", {"query": "Widget", "kind": "symbol", "scope": "workspace", "path": "beta:Widget.cs#Widget"})
        assert [item["project"] for item in selected["results"]] == ["beta"], selected
        context = call(peer, "get_context", {"task": "Adjust Widget", "scope": "workspace", "budget": 2000})
        assert context["workspace"][0]["project"] == "beta", context
        assert context["used_bytes"] <= context["budget_bytes"] == 2000
        assert any(item.get("content") == "Widget shared rule" for item in context["memory"]), context
        assert "gamma" not in json.dumps(context), context
        cli("workspace", "rename", "one", "--to", "renamed")
        renamed = call(peer, "get_context", {"task": "Adjust Widget", "scope": "workspace"})
        assert any(item.get("content") == "Widget shared rule" for item in renamed["memory"]), renamed
        cli("workspace", "leave", "renamed", "--project", str(repositories[1]))
        cli("workspace", "join", "renamed", "--project", str(repositories[2]))
        updated = call(peer, "search_project", {"query": "Widget", "kind": "symbol", "scope": "workspace"})
        assert [item["project"] for item in updated["results"]] == ["alpha", "gamma"], updated
    cli("workspace", "delete", "renamed")
    after = hashlib.sha256(foreign.read_bytes()).hexdigest()
    assert before == after
    receipt = {"public_cli_and_mcp": True, "scope_isolation": True, "member_provenance": True,
               "membership_invalidation": True, "rename_preserves_shared_knowledge": True,
               "foreign_database_unchanged": True, "light_excluded_heavy_not_started": True,
               "context_used_bytes": context["used_bytes"], "context_budget": 2000}
    (artifacts / "workspace-published-smoke.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Published workspace CLI/MCP: membership, provenance, isolation, context budget, rename-stable knowledge and read-only foreign state passed.")


if __name__ == "__main__":
    main()
