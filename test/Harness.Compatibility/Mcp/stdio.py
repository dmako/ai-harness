"""PORT-007 real stdio and pinned-Go comparisons; every process uses isolated state."""

import argparse
from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from compatibility import Normalizer, Peer, Sandbox, write_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--oracle", required=True, type=Path)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    candidate = args.candidate.resolve()
    command = [str(candidate)] if candidate.suffix.lower() == ".exe" else ["dotnet", str(candidate)]
    sandbox = Sandbox(args.oracle)
    cases = []
    peers = []

    def start(project=None):
        peer = Peer([*command, "mcp", "--project", str(project or sandbox.repository)], sandbox.repository, sandbox.environment)
        peers.append(peer)
        initialized = peer.initialize()
        assert initialized["result"]["serverInfo"]["name"] == "harness", initialized
        return peer, initialized

    def check(name, condition, **evidence):
        cases.append({"name": name, "pass": bool(condition), **evidence})
        if not condition:
            raise AssertionError(name + ": " + json.dumps(evidence, ensure_ascii=True)[:3000])

    def result(peer, tool, arguments):
        value = peer.call(tool, arguments)
        if "result" in value:
            envelope = value["result"]
            if "structuredContent" in envelope:
                assert json.loads(envelope["content"][0]["text"]) == envelope["structuredContent"]
        return value

    try:
        cs, initialized = start()
        go = sandbox.peer()
        go_initialized = go.initialize()
        go_norm = Normalizer({}, {"version": go_initialized["result"]["serverInfo"]["version"]})
        cs_norm = Normalizer({}, {"version": initialized["result"]["serverInfo"]["version"]})

        def compare(name, tool, arguments):
            left = result(go, tool, arguments)
            right = result(cs, tool, arguments)
            # Request ids identify each peer's calls, not application state.
            left.pop("id", None)
            right.pop("id", None)
            check(name, go_norm.value(left) == cs_norm.value(right), oracle=left, candidate=right)

        check("initialize-capabilities", go_initialized["result"]["capabilities"] == initialized["result"]["capabilities"], candidate=initialized["result"]["capabilities"])
        left = go.request("tools/list")["result"]
        right = cs.request("tools/list")["result"]
        expected = {tool["name"]: tool for tool in left["tools"] if tool["name"] in ("ping", "diagnostics", "state_put_test", "state_get_test")}
        check("four-bootstrap-tool-schemas", expected == {tool["name"]: tool for tool in right["tools"] if tool["name"] in expected}, count=len(right["tools"]))
        check("tool-list-cache-envelope", {key: value for key, value in left.items() if key != "tools"} == {key: value for key, value in right.items() if key != "tools"})
        compare("ping", "ping", {})
        diagnostics = result(cs, "diagnostics", {})["result"]["structuredContent"]
        check("lazy-diagnostics", "db_error" in diagnostics and not (sandbox.repository / ".harness").exists(), diagnostics=diagnostics)
        for name, tool, arguments in [
            ("required-key", "state_get_test", {}),
            ("required-value", "state_put_test", {"key": "x"}),
            ("unknown-key", "ping", {"unexpected": True}),
            ("wrong-type", "state_get_test", {"key": 1}),
            ("null-key", "state_get_test", {"key": None}),
            ("empty-key", "state_get_test", {"key": ""}),
            ("too-many-runes", "state_get_test", {"key": "🧪" * 257}),
            ("too-many-utf8-bytes", "state_put_test", {"key": "x", "value": "ž" * 32768 + "x"}),
            ("unknown-tool", "does_not_exist", {}),
        ]:
            compare(name, tool, arguments)
        check("rejected-inputs-no-state", not (sandbox.repository / ".harness").exists())
        for name, raw in [
            ("parse-error", b"{nope\n"),
            ("invalid-object", b"{}\n"),
            ("invalid-scalar", b"42\n"),
            ("invalid-version-id", b'{"jsonrpc":"1.0","id":"wrong","method":"ping"}\n'),
            ("empty-batch", b"[]\n"),
            ("bad-batch-member", b'[{"jsonrpc":"2.0","id":98,"method":"ping"},{}]\n'),
            ("oversized-frame", b"x" * (16 * 1024 * 1024 + 1) + b"\n"),
        ]:
            responses = []
            for peer in (go, cs):
                peer.process.stdin.write(raw)
                peer.process.stdin.flush()
                responses.append(peer.receive())
            check(name, responses[0] == responses[1], response=responses[1])
        compare("recovery-after-invalid-frames", "ping", {})
        compare("unicode-put", "state_put_test", {"key": "Unicode 🧪", "value": "žluťoučký <>&"})
        compare("unicode-get", "state_get_test", {"key": "Unicode 🧪"})
        compare("empty-put", "state_put_test", {"key": "empty", "value": ""})
        compare("empty-get-omits-value", "state_get_test", {"key": "empty"})
        compare("missing-get", "state_get_test", {"key": "missing"})
        compare("maximum-key", "state_put_test", {"key": "🧪" * 256, "value": "valid"})
        synchronized = sandbox.cli("sync")
        check("seed-go-sync-for-diagnostics", synchronized["exit_code"] == 0, stderr=synchronized["stderr"])
        # Compare diagnostics on a shared DB, preserving actual project/worktree identities.
        go_diagnostics = result(go, "diagnostics", {})["result"]["structuredContent"]
        cs_diagnostics = result(cs, "diagnostics", {})["result"]["structuredContent"]
        for value in (go_diagnostics, cs_diagnostics):
            for key in ("version", "commit", "go_version"):
                value.pop(key)
        check("diagnostics-identities-and-sync", go_diagnostics == cs_diagnostics, oracle=go_diagnostics, candidate=cs_diagnostics)
        # Force process termination after a committed write, then read it from a new process.
        cs.process.terminate()
        cs.process.wait(timeout=10)
        cs.close()
        restarted, _ = start()
        restored = result(restarted, "state_get_test", {"key": "Unicode 🧪"})["result"]["structuredContent"]
        check("restart-persistence", restored == {"key": "Unicode 🧪", "found": True, "value": "žluťoučký <>&"})
        restarted.send({"jsonrpc": "2.0", "id": 901, "method": "tools/call", "params": {"name": "state_put_test", "arguments": {"key": "eof", "value": "saved"}}})
        restarted.send({"jsonrpc": "2.0", "id": 902, "method": "tools/call", "params": {"name": "ping", "arguments": {}}})
        restarted.process.stdin.close()
        responses = [restarted.receive(), restarted.receive()]
        check("stdin-eof-drains-responses", {value.get("id") for value in responses} == {901, 902})
        restarted.process.wait(timeout=10)
        check("graceful-disconnect", restarted.process.returncode == 0)
        restarted.close()
        go.close()
        database = sandbox.repository / ".harness/harness.db"
        with closing(sqlite3.connect(database)) as connection:
            check("disconnect-storage-released", connection.execute("PRAGMA integrity_check").fetchone()[0] == "ok" and connection.execute("SELECT value FROM diagnostics WHERE key='eof'").fetchone()[0] == "saved")
        registry = json.loads((Path(sandbox.environment["HARNESS_CONFIG_DIR"]) / "projects.json").read_text(encoding="utf-8"))
        check("machine-event-on-close", "last_mcp" in registry["projects"][0])
        batch_candidate, _ = start()
        batch_oracle = sandbox.peer()
        batch_oracle.initialize()
        batch = b'[{"jsonrpc":"2.0","id":55,"method":"ping"}]\n'
        for peer in (batch_oracle, batch_candidate):
            peer.process.stdin.write(batch)
            peer.process.stdin.flush()
            peer.process.wait(timeout=10)
            peer.close()
        check("modern-batch-terminates-like-go", batch_oracle.process.returncode == batch_candidate.process.returncode == 1
              and b"batching is not supported" in batch_candidate.errors and b"Unhandled exception" not in batch_candidate.errors)

        # Bad configuration must fail before any JSON-RPC stdout or database work.
        config = Path(sandbox.environment["HARNESS_CONFIG_DIR"]) / "config.toml"
        config.write_text("[storage]\nunknown = true\n", encoding="utf-8")
        failed = subprocess.run([*command, "mcp", "--project", str(sandbox.repository)], env=sandbox.environment, cwd=sandbox.repository, capture_output=True, timeout=10)
        check("startup-config-fails-cleanly", failed.returncode == 1 and not failed.stdout and b"config.toml" in failed.stderr)
        absent = subprocess.run([*command, "mcp", "--project", str(sandbox.root / "absent")], env=sandbox.environment, cwd=sandbox.repository, capture_output=True, timeout=10)
        check("startup-missing-project-fails-cleanly", absent.returncode == 1 and not absent.stdout and b"Project directory" in absent.stderr)
        blocked = sandbox.root / "blocked"
        blocked.write_text("a file, not a directory", encoding="utf-8")
        config.write_text("[storage]\ndb_path = " + json.dumps(str(blocked / "h.db")) + "\n", encoding="utf-8")
        unavailable, _ = start()
        check("ping-with-unavailable-storage", result(unavailable, "ping", {})["result"]["structuredContent"]["message"] == "pong")
        failure = result(unavailable, "state_get_test", {"key": "x"})
        check("storage-failure-tool-envelope", failure["result"].get("isError") is True and "structuredContent" not in failure["result"] and "storage unavailable:" in failure["result"]["content"][0]["text"])
        unavailable.close()
        config.unlink()
        nongit = sandbox.root / "outside-git"
        nongit.mkdir()
        outside, _ = start(nongit)
        outside_info = result(outside, "diagnostics", {})["result"]["structuredContent"]
        check("non-git-diagnostics", outside_info["git_error"] == "project root is not inside a Git working tree" and not (nongit / ".harness").exists())
        outside.close()
        # Every stdout line has been parsed by Peer, including all shutdown responses.
        check("stdout-protocol-only", all(not isinstance(value, Exception) or isinstance(value, EOFError) for peer in peers for value in list(peer.output.queue)))
    finally:
        for peer in peers:
            if peer.process.poll() is None:
                peer.close()
        write_json(args.output, {"source_commit": "9cb401dd2de4f85dd089fea86a0b139d81b178c6", "passed": bool(cases) and all(case["pass"] for case in cases), "cases": cases})
        sandbox.close()
    print(json.dumps({"passed": sum(case["pass"] for case in cases), "total": len(cases)}))


if __name__ == "__main__":
    main()
