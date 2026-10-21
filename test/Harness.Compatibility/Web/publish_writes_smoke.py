"""Exercise published UI writes and streaming with no runtime on PATH."""
import argparse
import hashlib
import json
import os
from pathlib import Path
from queue import Queue
import subprocess
import tempfile
from threading import Thread
from urllib.error import HTTPError
from urllib.request import build_opener, ProxyHandler, Request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    executable = args.executable.resolve()
    with tempfile.TemporaryDirectory(prefix="harness UI writes offline ") as directory:
        environment = dict(os.environ, HARNESS_CONFIG_DIR=str(Path(directory) / "global"), PATH="")
        environment.pop("DOTNET_ROOT", None)
        environment.pop("DOTNET_ROOT_X64", None)
        process = subprocess.Popen([str(executable), "ui", "--addr", "127.0.0.1:0", "--no-open"],
                                   cwd=directory, env=environment, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, text=True)
        try:
            lines = Queue()
            Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
            announced = lines.get(timeout=20)
            assert announced.startswith("harness ui on "), announced
            address = announced.split()[3].rstrip("/")
            opener = build_opener(ProxyHandler({}))

            def write(path, body, origin=None):
                request = Request(address + path, data=json.dumps(body).encode("utf-8"),
                                  headers={"Content-Type": "application/json", "X-Harness-UI": "1",
                                           "Origin": address if origin is None else origin})
                with opener.open(request, timeout=15) as response:
                    return json.load(response)

            try:
                write("/api/knowledge/memories", {"level": "user", "type": "goal", "content": "Rejected"}, "http://foreign.invalid")
                raise AssertionError("foreign Origin should be rejected")
            except HTTPError as error:
                assert error.code == 403
                assert "error" in json.load(error)
            created = write("/api/knowledge/memories", {"level": "user", "type": "goal", "content": "Published UI runs offline"})
            memory = created["memory"]
            assert memory["recorded_by"].startswith("human:")
            corrected = write("/api/knowledge/memories/" + memory["id"] + "/supersede",
                              {"level": "user", "content": "Published UI writes run offline", "reason": "Clarify scope"})
            assert corrected["memory"]["source_ref"] == "harness ui correction"
            retired = write("/api/knowledge/memories/" + corrected["memory"]["id"] + "/retire",
                            {"level": "user", "reason": "Smoke complete"})
            assert retired["memory"]["valid_until"]
            write("/api/workspaces", {"name": "offline"})
            write("/api/workspaces/offline/rename", {"name": "verified", "title": "Offline smoke"})
            write("/api/workspaces/verified/delete", {})
            with opener.open(address + "/api/events", timeout=15) as response:
                assert response.headers.get_content_type() == "text/event-stream"
                frame = b""
                while not frame.endswith(b"\n\n"):
                    frame += response.readline()
                    assert frame and len(frame) < 4096
                assert b"retry: 3000" in frame
            receipt = {"result": "pass", "executable_sha256": hashlib.sha256(executable.read_bytes()).hexdigest(),
                       "empty_path": True, "empty_working_directory": True, "foreign_origin": "pass",
                       "memory_create_correct_retire": "pass", "human_audit": "pass",
                       "workspace_create_rename_delete": "pass", "sse": "pass"}
        finally:
            process.terminate()
            try:
                process.communicate(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.communicate(timeout=10)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    main()
