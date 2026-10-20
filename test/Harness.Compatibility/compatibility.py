"""Capture isolated executable contracts, or compare a candidate with saved Go fixtures."""

import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request

from baseline import COMMIT, ROOT


FIXTURES = ROOT / "test" / "fixtures" / "compatibility"
SCRIPT = Path(__file__).resolve().parent
MIGRATIONS = json.loads((ROOT / "doc/port/inventory/storage.json").read_text(encoding="utf-8"))["migrations"]
HISTORICAL = (1, 4, 5, 13, 19)
STAMP = "2026-01-02T03:04:05.000000000Z"


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\r\n") as stream:
        stream.write(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def historical_databases(directory):
    """Build synthetic databases exclusively from the frozen SQL, never personal DBs."""
    directory.mkdir(parents=True, exist_ok=True)
    manifest = []
    for version in HISTORICAL:
        path = directory / f"schema-{version:02}.db"
        if path.exists():
            raise ValueError(f"Refusing to overwrite existing database {path}")
        with closing(sqlite3.connect(path)) as database:
            database.execute("CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, "
                             "name TEXT NOT NULL, applied_at TEXT NOT NULL) STRICT")
            for migration in MIGRATIONS[:version]:
                database.executescript(migration["sql"])
                database.execute("INSERT INTO schema_migrations VALUES (?, ?, ?)",
                                 (int(Path(migration["path"]).name[:4]), Path(migration["path"]).stem, STAMP))
                database.commit()
            database.execute("INSERT INTO diagnostics VALUES (?, ?, ?)", ("fixture", "žluťoučký 🧪", STAMP))
            database.commit()
            if database.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
                raise ValueError("Synthetic fixture integrity check failed")
        manifest.append({"file": path.name, "schema_version": version,
                         "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    write_json(directory / "manifest.json", {"source_commit": COMMIT, "databases": manifest})
    return manifest


def stop(process):
    if process.poll() is None:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        else:
            import signal
            os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=5)


class Peer:
    """Small JSON-RPC peer with bounded reads and independent stderr draining."""

    def __init__(self, command, cwd, environment):
        options = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {"start_new_session": True}
        self.process = subprocess.Popen(command, cwd=cwd, env=environment, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, **options)
        self.output = queue.Queue()
        self.errors = bytearray()
        self.transcript = []
        self.sequence = 0
        self.threads = [threading.Thread(target=self._read, daemon=True),
                        threading.Thread(target=self._stderr, daemon=True)]
        for thread in self.threads:
            thread.start()

    def _read(self):
        for line in self.process.stdout:
            try:
                self.output.put(json.loads(line))
            except (ValueError, UnicodeError) as error:
                self.output.put(ValueError(f"Non-JSON stdout frame: {line!r}: {error}"))
        self.output.put(EOFError("Protocol process disconnected"))

    def _stderr(self):
        while data := self.process.stderr.read(4096):
            self.errors.extend(data)

    def send(self, message):
        self.transcript.append({"direction": "request", "message": message})
        self.process.stdin.write((json.dumps(message, ensure_ascii=False) + "\n").encode())
        self.process.stdin.flush()

    def receive(self, timeout=12):
        try:
            value = self.output.get(timeout=timeout)
        except queue.Empty as error:
            raise TimeoutError("Protocol response deadline exceeded") from error
        if isinstance(value, Exception):
            raise value
        self.transcript.append({"direction": "response", "message": value})
        return value

    def request(self, method, parameters=None, timeout=12):
        self.sequence += 1
        message = {"jsonrpc": "2.0", "id": self.sequence, "method": method}
        if parameters is not None:
            message["params"] = parameters
        self.send(message)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            response = self.receive(max(0.01, deadline - time.monotonic()))
            if response.get("id") == self.sequence:
                return response
        raise TimeoutError(method)

    def initialize(self):
        response = self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                               "clientInfo": {"name": "compatibility-fixture", "version": "1.0.0"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return response

    def call(self, name, arguments, timeout=12):
        return self.request("tools/call", {"name": name, "arguments": arguments}, timeout)

    def close(self):
        if not self.process.stdin.closed:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            stop(self.process)
        for thread in self.threads:
            thread.join(timeout=2)
        self.process.stdout.close()
        self.process.stderr.close()


class Normalizer:
    """Allowlisted nondeterminism; retain types, missing fields, arrays and relationships."""

    def __init__(self, replacements, version):
        self.replacements = sorted(replacements.items(), key=lambda pair: -len(pair[0]))
        self.version = version
        self.identities = {}

    def value(self, value, field=""):
        if isinstance(value, dict):
            return {key: self.value(item, key) for key, item in value.items()}
        if isinstance(value, list):
            return [self.value(item, field) for item in value]
        if field in ("duration_ms", "latency_ms") and type(value) in (float, int):
            return "<DURATION_MS>"
        if not isinstance(value, str):
            return value
        # MCP text content often duplicates structuredContent. Normalize its JSON structurally.
        if field == "text":
            try:
                decoded = json.loads(value)
            except ValueError:
                pass
            else:
                return json.dumps(self.value(decoded), ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        if field in ("go_version",):
            return "<RUNTIME_VERSION>"
        if field in ("version", "commit", "date") and value == self.version.get(field):
            return "<BUILD_" + field.upper() + ">"
        # Only full timestamp values are substituted; dates in prose and schema descriptions stay intact.
        if field in ("first_seen", "last_seen", "last_init", "last_mcp", "last_sync",
                     "created_at", "updated_at", "at", "applied_at", "synced_at") and re.fullmatch(
                         r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z", value):
            return "<TIME>"
        for original, replacement in self.replacements:
            for spelling in {original, original.replace("\\", "/"), original.replace("/", "\\")}:
                value = value.replace(spelling, replacement)
        def identity(match):
            actual = match[0]
            if actual not in self.identities:
                self.identities[actual] = f"<{actual.split('_')[0].upper()}_{len(self.identities) + 1}>"
            return self.identities[actual]
        if field in ("id", "project_id", "worktree_id", "copied_from", "stdout", "stderr", "text"):
            return re.sub(r"\b(?:prj|wt|wtr|mem|prop|tpr|clm|fpr)_[a-f0-9]{8,}\b", identity, value)
        return value


class Sandbox:
    def __init__(self, executable):
        self.temporary = tempfile.TemporaryDirectory(prefix="harness-compat-")
        self.root = Path(self.temporary.name)
        self.repository = self.root / "project žluťoučký 🧪"
        self.repository.mkdir()
        self.environment = {key: value for key, value in os.environ.items()
                            if not key.startswith(("HARNESS_", "GIT_"))}
        for key, relative in {"HARNESS_CONFIG_DIR": "config", "HOME": "home", "USERPROFILE": "home",
                              "APPDATA": "appdata", "LOCALAPPDATA": "local", "XDG_CONFIG_HOME": "xdg",
                              "XDG_CACHE_HOME": "cache", "TMP": "tmp", "TEMP": "tmp"}.items():
            target = self.root / relative
            target.mkdir(exist_ok=True)
            self.environment[key] = str(target)
        self.environment.update({"GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": str(self.root / "gitconfig"),
                                 "GIT_OPTIONAL_LOCKS": "0", "PYTHONIOENCODING": "utf-8", "PYTHONUTF8": "1"})
        self.executable = str(executable.resolve())
        self.git("init", "-b", "main")
        self.git("config", "user.name", "Compatibility Fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "core.autocrlf", "false")
        (self.repository / "Widget.cs").write_text("class Widget {}\n", encoding="utf-8")
        self.git("add", "Widget.cs")
        self.environment.update({"GIT_AUTHOR_DATE": "2026-01-02T03:04:05Z", "GIT_COMMITTER_DATE": "2026-01-02T03:04:05Z"})
        self.git("commit", "-m", "Synthetic fixture")
        self.peers = []

    def git(self, *arguments):
        return subprocess.run(["git", *arguments], cwd=self.repository, env=self.environment,
                              capture_output=True, check=True)

    def cli(self, *arguments):
        process = subprocess.run([self.executable, *arguments], cwd=self.repository, env=self.environment,
                                 capture_output=True, timeout=20)
        def decode(raw):
            value = raw.decode("utf-8").replace("\r\n", "\n")
            try:
                return "json", json.loads(value)
            except ValueError:
                return "text", value
        stdout_format, stdout = decode(process.stdout)
        stderr_format, stderr = decode(process.stderr)
        return {"exit_code": process.returncode, "stdout": stdout, "stderr": stderr,
                "stdout_format": stdout_format, "stderr_format": stderr_format}

    def peer(self):
        peer = Peer([self.executable, "mcp", "--project", str(self.repository)], self.repository, self.environment)
        self.peers.append(peer)
        return peer

    def close(self):
        for peer in self.peers:
            if peer.process.poll() is None:
                peer.close()
        self.temporary.cleanup()


def ui_cases(sandbox):
    options = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {"start_new_session": True}
    process = subprocess.Popen([sandbox.executable, "ui", "--no-open", "--addr", "127.0.0.1:0"],
                               cwd=sandbox.repository, env=sandbox.environment, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, **options)
    lines = queue.Queue()
    threading.Thread(target=lambda: [lines.put(line) for line in process.stdout], daemon=True).start()
    errors = bytearray()
    threading.Thread(target=lambda: errors.extend(process.stderr.read()), daemon=True).start()
    try:
        endpoint = None
        deadline = time.monotonic() + 10
        while endpoint is None and time.monotonic() < deadline:
            line = lines.get(timeout=10).decode("utf-8")
            if match := re.search(r"http://127\.0\.0\.1:\d+", line):
                endpoint = match[0]
        if endpoint is None:
            raise TimeoutError("UI did not publish its ephemeral address")
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        cases = []
        for name, path, body, headers in [
            ("projects", "/api/projects", None, {}),
            ("workspaces", "/api/workspaces", None, {}),
            ("missing_route", "/api/does-not-exist", None, {}),
            ("foreign_host", "/api/projects", None, {"Host": "attacker.invalid"}),
            ("unguarded_write", "/api/workspaces", b'{"name":"bad"}', {"Content-Type": "application/json"}),
            ("malformed_write", "/api/workspaces", b'{', {"Content-Type": "application/json",
              "Origin": endpoint, "X-Harness-UI": "1"}),
        ]:
            request = urllib.request.Request(endpoint + path, data=body, headers=headers)
            try:
                response = opener.open(request, timeout=10)
            except urllib.error.HTTPError as error:
                response = error
            except ConnectionResetError as error:
                raise RuntimeError(f"UI case {name} reset its connection; process status {process.poll()}") from error
            with response:
                raw = response.read().decode()
                cases.append({"name": name, "status": response.status,
                              "content_type": response.headers.get("Content-Type"), "body": json.loads(raw)})
        return cases
    finally:
        stop(process)
        process.stdout.close()
        process.stderr.close()


def provider_cases(sandbox):
    cases = []
    configuration = sandbox.repository / ".harness/config.toml"
    for mode in ("normal", "error", "timeout", "stderr", "disconnect", "generic"):
        log = sandbox.root / (mode + ".jsonl")
        actual_mode = "normal" if mode == "generic" else mode
        configuration.write_text(
            '[providers.fixture]\ntype = ' + json.dumps("mcp" if mode == "generic" else "harness") + '\n'
            + 'command = ' + json.dumps(sys.executable) + '\nargs = '
            + json.dumps([str(SCRIPT / "fake_provider.py"), "--mode", actual_mode, "--log", str(log)])
            + '\ntimeout_ms = 800\ndir = "project"\n'
            + ('[providers.fixture.capabilities."code.symbol_search"]\ntool = "find_symbols"\n'
               'args = { query = "{query}" }\nitems = "items"\n'
               'fields = { name = "name", path = "path", start_line = "start_line", snippet = "snippet" }\n'
               if mode == "generic" else ''), encoding="utf-8")
        peer = sandbox.peer()
        peer.initialize()
        response = peer.call("search_project", {"kind": "symbol", "query": "Widget"}, timeout=40)
        peer.close()
        messages = [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines()] if log.exists() else []
        cases.append({"name": mode, "response": response, "provider_messages": messages,
                      "mcp_stderr": peer.errors.decode("utf-8")})
    configuration.write_text("# isolated defaults\n", encoding="utf-8")
    return cases


def capture(executable):
    sandbox = Sandbox(executable)
    try:
        version = sandbox.cli("version", "--json")["stdout"]
        cases = {"cli": []}
        for name, arguments in [
            ("version", ["version", "--json"]), ("help", ["help"]),
            ("task_help", ["tasks", "complete", "--help"]),
            ("unknown_command", ["not-a-command"]), ("missing_argument", ["tasks", "show"]),
            ("unknown_flag", ["version", "--not-a-flag"]),
            ("init", ["init"]), ("status", ["status", "--json"]),
        ]:
            cases["cli"].append({"name": name, **sandbox.cli(*arguments)})
        peer = sandbox.peer()
        cases["mcp"] = [{"name": "initialize", "response": peer.initialize()},
                        {"name": "tools_list", "response": peer.request("tools/list", {})},
                        {"name": "ping", "response": peer.call("ping", {})},
                        {"name": "state_put", "response": peer.call("state_put_test", {"key": "Unicode 🧪", "value": "žluťoučký"})},
                        {"name": "state_get", "response": peer.call("state_get_test", {"key": "Unicode 🧪"})},
                        {"name": "invalid_arguments", "response": peer.call("get_context", {"path": "wrong"})},
                        {"name": "unknown_tool", "response": peer.call("not_a_tool", {})}]
        peer.process.stdin.write(b'{malformed\n')
        peer.process.stdin.flush()
        cases["mcp"].append({"name": "malformed_frame", "response": peer.receive()})
        cases["mcp"].append({"name": "after_malformed", "response": peer.call("ping", {})})
        peer.close()
        cases["mcp_stderr"] = peer.errors.decode("utf-8")
        peer = sandbox.peer()
        peer.initialize()
        cases["mcp"].append({"name": "state_after_restart", "response": peer.call("state_get_test", {"key": "Unicode 🧪"})})
        peer.close()
        cases["ui"] = ui_cases(sandbox)
        cases["providers"] = provider_cases(sandbox)
        cases["migrations"] = []
        for version_number in HISTORICAL:
            db_path = sandbox.repository / ".harness/harness.db"
            for suffix in ("", "-wal", "-shm"):
                target = Path(str(db_path) + suffix)
                if target.exists():
                    target.unlink()
            shutil.copyfile(FIXTURES / "sqlite" / f"schema-{version_number:02}.db", db_path)
            initialized = sandbox.cli("init")
            with closing(sqlite3.connect(db_path)) as database:
                migrated = database.execute("SELECT max(version) FROM schema_migrations").fetchone()[0]
                value = database.execute("SELECT value FROM diagnostics WHERE key='fixture'").fetchone()[0]
                integrity = database.execute("PRAGMA integrity_check").fetchone()[0]
            cases["migrations"].append({"from_schema": version_number, "to_schema": migrated,
                                        "diagnostic_value": value, "integrity": integrity, "init_exit": initialized["exit_code"]})
        normalizer = Normalizer({str(sandbox.root): "<SANDBOX>", str(SCRIPT): "<FIXTURE_TOOLS>",
                                 sandbox.executable: "<EXECUTABLE>", sys.executable: "<PYTHON>"}, version)
        return {"format_version": 1, "source_commit": COMMIT, "oracle_version": version,
                "executable_sha256": hashlib.sha256(Path(sandbox.executable).read_bytes()).hexdigest(),
                "cases": normalizer.value(cases)}
    finally:
        sandbox.close()


def differences(expected, actual, path="$"):
    if type(expected) in (int, float) and type(actual) in (int, float):
        return [] if expected == actual else [f"{path}: {expected!r} != {actual!r}"]
    if type(expected) is not type(actual):
        return [f"{path}: type {type(expected).__name__} != {type(actual).__name__}"]
    if isinstance(expected, dict):
        result = [f"{path}: missing field {key}" for key in sorted(expected.keys() - actual.keys())]
        result += [f"{path}: unexpected field {key}" for key in sorted(actual.keys() - expected.keys())]
        for key in sorted(expected.keys() & actual.keys()):
            result.extend(differences(expected[key], actual[key], path + "." + key))
        return result
    if isinstance(expected, list):
        if len(expected) != len(actual):
            return [f"{path}: array length {len(expected)} != {len(actual)}"]
        return [item for index, (left, right) in enumerate(zip(expected, actual))
                for item in differences(left, right, f"{path}[{index}]")]
    return [] if expected == actual else [f"{path}: {expected!r} != {actual!r}"]


def validate_recording(recording):
    """Refuse to bless a failed setup or fake failure that accidentally succeeded."""
    cases = recording["cases"]
    for case in cases["cli"]:
        expected = 2 if case["name"] in ("unknown_command", "missing_argument", "unknown_flag") else 0
        if case["exit_code"] != expected:
            raise ValueError(f"Unexpected CLI outcome: {case['name']}")
    calls = {case["name"]: case["response"] for case in cases["mcp"]}
    expected_tools = json.loads((ROOT / "doc/port/inventory/mcp.json").read_text(encoding="utf-8"))["tools"]
    tools = calls["tools_list"]["result"]["tools"]
    if {item["name"] for item in tools} != {item["name"] for item in expected_tools}:
        raise ValueError("Runtime MCP tools do not match the frozen source inventory")
    if any("inputSchema" not in item or "outputSchema" not in item for item in tools):
        raise ValueError("Missing SDK-generated input/output schema")
    if not calls["invalid_arguments"]["result"].get("isError"):
        raise ValueError("Invalid arguments unexpectedly succeeded")
    if calls["malformed_frame"]["error"]["code"] != -32700:
        raise ValueError("Malformed input did not return parse error")
    for name in ("state_get", "state_after_restart"):
        if calls[name]["result"]["structuredContent"]["value"] != "žluťoučký":
            raise ValueError("Unicode state failed to persist across processes")
    if calls["after_malformed"]["result"]["structuredContent"]["message"] != "pong":
        raise ValueError("Malformed frame terminated the MCP session")
    for case in cases["providers"]:
        data = case["response"]["result"]["structuredContent"]
        if case["name"] in ("error", "timeout", "disconnect"):
            if not data.get("errors") or data.get("results"):
                raise ValueError(f"Failure provider did not fail: {case['name']}")
        elif not data.get("results") or data["results"][0]["name"] != "Widget":
            raise ValueError(f"Provider did not produce its symbol: {case['name']}")
        if case["mcp_stderr"]:
            raise ValueError("Provider diagnostics unexpectedly leaked into harness stderr")
    statuses = {case["name"]: case["status"] for case in cases["ui"]}
    if statuses != {"projects": 200, "workspaces": 200, "missing_route": 404,
                    "foreign_host": 403, "unguarded_write": 403, "malformed_write": 400}:
        raise ValueError(f"Unexpected UI outcomes: {statuses}")
    for case in cases["migrations"]:
        if (case["to_schema"], case["integrity"], case["init_exit"], case["diagnostic_value"]) != (
                19, "ok", 0, "žluťoučký 🧪"):
            raise ValueError(f"Historical migration failed: {case}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    databases = commands.add_parser("databases")
    databases.add_argument("--output", type=Path, default=FIXTURES / "sqlite")
    for verb in ("capture", "compare"):
        sub = commands.add_parser(verb)
        sub.add_argument("--executable", type=Path, required=True)
        sub.add_argument("--fixture", type=Path, required=True)
    arguments = parser.parse_args()
    if arguments.command == "databases":
        historical_databases(arguments.output)
        print("Created five synthetic historical databases.")
        return 0
    result = capture(arguments.executable)
    if arguments.command == "capture":
        if result["oracle_version"]["commit"] != COMMIT:
            raise ValueError("Refusing to bless fixtures from an executable outside the source pin")
        provenance = json.loads(arguments.executable.with_name("oracle.json").read_text(encoding="utf-8"))
        if provenance["executable_sha256"] != result["executable_sha256"] or provenance["source_commit"] != COMMIT:
            raise ValueError("Executable does not match the pinned build provenance")
        result["oracle_build"] = {key: provenance[key] for key in ("compiler", "source_tree", "executable_sha256")}
        validate_recording(result)
        write_json(arguments.fixture, result)
        print("Captured isolated CLI, MCP, provider, UI and migration contracts.")
        return 0
    expected = json.loads(arguments.fixture.read_text(encoding="utf-8"))
    failures = differences(expected["cases"], result["cases"])
    for failure in failures[:40]:
        print(failure)
    print(f"{len(failures)} contract differences.")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
