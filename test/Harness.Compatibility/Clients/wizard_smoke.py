"""Drive the actual setup wizard through a POSIX terminal; no real client homes are used."""
import argparse
import errno
import fcntl
import json
import os
from pathlib import Path
import pty
import select
import subprocess
import struct
import tempfile
import time
import termios


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="harness-wizard-") as temporary:
        home = Path(temporary)
        root = home / "repo with spaces"
        root.mkdir()
        claude = home / "claude" / ".claude.json"
        claude.parent.mkdir()
        claude.write_text("{}\n", encoding="utf-8")
        (home / "codex").mkdir()
        env = dict(os.environ, HOME=str(home), USERPROFILE=str(home), CLAUDE_CONFIG_DIR=str(claude.parent),
                   CODEX_HOME=str(home / "codex"), HARNESS_CONFIG_DIR=str(home / "global"), TERM="xterm-256color",
                   GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(home / "absent.gitconfig"))
        for key in ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]:
            env.pop(key, None)
        subprocess.run(["git", "init", "-q", str(root)], env=env, check=True, timeout=10)
        transcripts = []

        def drive(verb, cancel):
            master, slave = pty.openpty()
            fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 30, 180, 0, 0))
            process = subprocess.Popen(["dotnet", str(args.assembly.resolve()), verb], cwd=root, env=env,
                                       stdin=slave, stdout=slave, stderr=slave, start_new_session=True)
            os.close(slave)
            output = bytearray()

            def until(text):
                deadline = time.monotonic() + 20
                while text.encode() not in output:
                    assert time.monotonic() < deadline, output.decode(errors="replace")
                    if select.select([master], [], [], .1)[0]:
                        try:
                            data = os.read(master, 65536)
                        except OSError as error:
                            if error.errno != errno.EIO:
                                raise
                            data = b""
                        assert data, output.decode(errors="replace")
                        output.extend(data)
            try:
                until("MCP server")
                if cancel:
                    os.write(master, b"q")
                    until("Cancelled: nothing changed.")
                else:
                    os.write(master, b" \r")
                    until("Agent instructions")
                    os.write(master, b"\r")
                    until("Hooks")
                    os.write(master, b"\r")
                    until("Shell completion")
                    os.write(master, b"\r")
                    until("Review")
                    # A review screen must not have applied the pending entry change.
                    installed = "harness" in json.loads(claude.read_text())["mcpServers"] if "mcpServers" in json.loads(claude.read_text()) else False
                    assert installed == (verb == "uninstall")
                    os.write(master, b"\r")
                    until("Removing" if verb == "uninstall" else "Installing")
                assert process.wait(timeout=20) == 0, output.decode(errors="replace")
                transcripts.append(output.decode(errors="replace"))
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait()
                os.close(master)

        drive("install", True)
        assert claude.read_text() == "{}\n"
        drive("install", False)
        entry = json.loads(claude.read_text())["mcpServers"]["harness"]
        assert entry["args"][-1] == "mcp"
        drive("uninstall", False)
        assert json.loads(claude.read_text())["mcpServers"] == {}
        artifacts = args.artifacts.resolve()
        artifacts.mkdir(parents=True, exist_ok=True)
        (artifacts / "clients-wizard-terminal.json").write_text(json.dumps({"cancel_no_write": True, "review_no_write": True, "install": True, "uninstall": True, "terminal_sessions": len(transcripts)}, indent=2) + "\n")
        print("Terminal wizard: cancel, review-before-write, install and uninstall passed in isolated POSIX PTYs.")


if __name__ == "__main__":
    main()
