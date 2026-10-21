"""Exercise published hooks and completion with isolated client homes and real shells."""
import argparse
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    args = parser.parse_args()
    artifacts = args.artifacts.resolve()
    home = artifacts / "hooks-publish-smoke" / ("home with spaces λ " + uuid.uuid4().hex)
    root = home / "repository with spaces"
    root.mkdir(parents=True)
    binary = home / "bin with spaces" / args.executable.name
    binary.parent.mkdir()
    shutil.copy2(args.executable.resolve(), binary)
    env = dict(os.environ, HOME=str(home), USERPROFILE=str(home),
               HARNESS_CONFIG_DIR=str(home / "global"), CLAUDE_CONFIG_DIR=str(home / "claude"),
               CODEX_HOME=str(home / "codex"), XDG_CONFIG_HOME=str(home / ".config"),
               ZDOTDIR=str(home), GIT_CONFIG_GLOBAL=str(home / "absent.gitconfig"),
               GIT_CONFIG_NOSYSTEM="1", PATH=str(binary.parent) + os.pathsep + os.environ["PATH"],
               DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(artifacts / "hooks-bundle"), HARNESS_HOOK_DEBUG="")
    for key in ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]:
        env.pop(key, None)

    def run(command, payload="", cwd=root, code=0):
        result = subprocess.run(command, input=payload, cwd=cwd, env=env, capture_output=True,
                                text=True, encoding="utf-8", timeout=20)
        assert result.returncode == code, (command, result.stdout, result.stderr)
        return result

    def cli(*arguments, **kwargs):
        return run([str(binary), *arguments], **kwargs)

    run(["git", "init", "-q", "-b", "main"])
    run(["git", "config", "user.name", "Fixture"])
    run(["git", "config", "user.email", "fixture@example.invalid"])
    cli("init")
    (home / "claude").mkdir()
    (home / "codex").mkdir()
    database = root / ".harness" / "harness.db"
    cli("hooks", "install", "--kind", "all")
    settings = home / "claude" / "settings.json"
    hooks = json.loads(settings.read_text(encoding="utf-8"))["hooks"]
    event_command = hooks["PostToolUse"][0]["hooks"][0]["command"]
    session_command = hooks["SessionStart"][0]["hooks"][0]["command"]
    payload = json.dumps({"session_id": "published-session", "cwd": str(root), "tool_name": "Read",
                          "tool_input": {"file_path": str(root / "src/a.cs"), "content": "PRIVATE"},
                          "tool_response": {"content": "PRIVATE"}})
    bash = shutil.which("bash")
    if os.name == "nt":
        candidate = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe"
        bash = str(candidate) if candidate.exists() else bash
    assert bash, "bash is required for the installed hook quoting check"
    event = run([bash, "-c", event_command], payload)
    assert event.stdout == event.stderr == "", event
    session = run([bash, "-c", session_command], json.dumps({"cwd": str(root)}))
    assert "This repository uses harness" in session.stdout and session.stderr == ""
    with sqlite3.connect(database) as connection:
        assert connection.execute("SELECT client,session_id,kind,path FROM tool_events").fetchall() == [
            ("claude-code", "published-session", "read", "src/a.cs")]
    # Git really invokes the installed background hook, including for a linked worktree.
    (root / "a.cs").write_text("class Fixture {}\n", encoding="utf-8")
    run(["git", "add", "a.cs"])
    run(["git", "commit", "-qm", "fixture"])
    head = run(["git", "rev-parse", "HEAD"]).stdout.strip()

    def synced(commit, worktree):
        with sqlite3.connect(database, timeout=1) as connection:
            return connection.execute("SELECT count(*) FROM sync_state s JOIN worktrees w ON w.id=s.worktree_id WHERE s.head_commit=? AND w.path=?",
                                      (commit, str(worktree))).fetchone()[0] > 0

    def wait_sync(commit, worktree):
        deadline = time.monotonic() + 15
        while not synced(commit, worktree):
            assert time.monotonic() < deadline, "Git hook did not refresh the current worktree"
            time.sleep(.1)

    wait_sync(head, root)
    linked = home / "linked worktree"
    run(["git", "worktree", "add", "-qb", "linked", str(linked)])
    wait_sync(head, linked)
    run(["git", "worktree", "remove", str(linked)])
    # Native bash completion asks the installed binary for the registered command tree.
    script = cli("completion", "bash").stdout
    completed = run([bash, "-c", script + '\nCOMP_WORDS=(harness tasks li); COMP_CWORD=2; _harness; printf "%s\\n" "${COMPREPLY[@]}"'])
    assert "list" in completed.stdout.splitlines(), completed
    powershell = shutil.which("pwsh") or shutil.which("powershell")
    pwsh_checked = False
    if powershell:
        script = cli("completion", "pwsh").stdout
        temporary = home / "complete.ps1"
        temporary.write_text(script + '\n(TabExpansion2 "harness tasks li" 16).CompletionMatches.CompletionText\n', encoding="utf-8-sig")
        completed = run([powershell, "-NoProfile", "-NonInteractive", "-File", str(temporary)])
        assert "list" in completed.stdout.splitlines(), completed
        pwsh_checked = True
    for shell in ["bash", "zsh", "fish", "pwsh"]:
        cli("completion", "install", "--shell", shell)
        assert "already installed" in cli("completion", "install", "--shell", shell).stdout
    assert "installed" in cli("hooks", "status").stdout
    cli("uninstall", "--all", "--project", str(root))
    assert "hook event" not in settings.read_text(encoding="utf-8") if settings.exists() else True
    assert database.exists()
    receipt = {"published_space_path": True, "client_command_runs_in_bash": True,
               "session_context": True, "metadata_only_event": True, "git_commit_refresh": True,
               "linked_worktree_refresh": True, "bash_completion": True, "powershell_completion": pwsh_checked,
               "profiles": ["bash", "zsh", "fish", "pwsh"], "cleanup": True}
    (artifacts / "hooks-published-smoke.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Published hooks: client commands, Git commit/worktree refresh, native completion and cleanup passed.")


if __name__ == "__main__":
    main()
