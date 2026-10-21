"""Exercise the published sync command against an isolated synthetic repository."""
import argparse
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    args = parser.parse_args()
    executable = args.executable.resolve()
    artifacts = args.artifacts.resolve()
    root = artifacts / "sync-publish-smoke" / uuid.uuid4().hex
    repository = root / "repository"
    repository.mkdir(parents=True)
    environment = dict(os.environ, HARNESS_CONFIG_DIR=str(root / "global"),
                       GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(root / "absent.gitconfig"))
    for name in ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]:
        environment.pop(name, None)

    def run(command):
        result = subprocess.run(command, env=environment, stdin=subprocess.DEVNULL,
                                capture_output=True, text=True, encoding="utf-8", timeout=60)
        assert result.returncode == 0, (result.stdout, result.stderr)
        return result.stdout

    def git(*arguments):
        return run(["git", "-C", str(repository), *arguments])

    def harness(*arguments):
        return run([str(executable), *arguments, "--project", str(repository)])

    git("init", "-b", "main")
    git("config", "user.name", "Test")
    git("config", "user.email", "test@example.invalid")
    git("config", "core.autocrlf", "false")
    (repository / "Alpha.cs").write_text("public class Alpha {}", encoding="utf-8")
    (repository / ".gitignore").write_text(".harness/\n", encoding="utf-8")
    git("add", "-A")
    git("commit", "-m", "base")
    harness("init")
    assert "2 added" in harness("sync")
    (repository / "Alpha.cs").write_text("public class Alpha { public void Save() {} }", encoding="utf-8")
    assert "1 changed" in harness("sync")
    configuration = repository / ".harness" / "config.toml"
    with configuration.open("a", encoding="utf-8") as file:
        file.write('\n[providers.graphify-csharp]\ncommand = "nonexistent-graphify-must-not-start-during-sync"\n')
    assert "Synced." in harness("sync", "--full")
    with sqlite3.connect(repository / ".harness" / "harness.db") as database:
        rows = database.execute("SELECT mode,status,added,changed,hashed,providers FROM sync_runs ORDER BY id").fetchall()
        assert [row[:5] for row in rows] == [("full", "ok", 2, 0, 2), ("incremental", "ok", 0, 1, 1), ("full", "ok", 0, 0, 0)], rows
        assert all(json.loads(row[5]) == [] for row in rows), rows
        branch, dirty, pending = database.execute("SELECT branch,dirty,pending FROM sync_state").fetchone()
        assert branch == "main" and dirty == 1 and json.loads(pending) == ["Alpha.cs"]
    receipt = {"runs": [row[:5] for row in rows], "branch": branch, "dirty": bool(dirty),
               "pending": json.loads(pending), "graphify_not_started_during_sync": True}
    (artifacts / "sync-published-smoke.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Published CLI: init, first/full/incremental sync and graphify exclusion passed; persisted rows verified.")


if __name__ == "__main__":
    main()
