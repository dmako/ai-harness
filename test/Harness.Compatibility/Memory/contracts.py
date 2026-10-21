"""PORT-009 isolated Go/C# memory MCP and CLI differential contracts."""
import argparse
from contextlib import closing
import json
from pathlib import Path
import re
import sqlite3
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from compatibility import Peer, Sandbox, write_json

TOOLS = {"memory_put", "memory_get", "memory_search", "memory_supersede", "goals_current",
         "constraints_current", "decisions_for", "memory_propose", "memory_proposals", "memory_review"}


class Canonical:
    def __init__(self, sandbox):
        self.sandbox = sandbox
        self.ids = {}
        self.retired = set()

    def value(self, value, field=""):
        if isinstance(value, dict):
            result = {key: self.value(value[key], key) for key in sorted(value)}
            identity = value.get("id", value.get("ID"))
            if isinstance(identity, str) and identity in self.retired:
                for key in ("valid_until", "ValidUntil"):
                    if key in result and result[key] is not None:
                        result[key] = "<RETIREMENT_TIME>"
            return result
        if isinstance(value, list):
            return [self.value(item, field) for item in value]
        if not isinstance(value, str):
            return value
        if field == "text":
            try:
                return self.value(json.loads(value))
            except ValueError:
                pass
            if value.startswith("project not initialized (run 'harness init'):"):
                return "project not initialized (run 'harness init'): <PLATFORM_FILE_ERROR>"
        if field in {"created_at", "updated_at", "at", "CreatedAt", "UpdatedAt", "DecidedAt"} and re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z", value):
            return "<TIME>"
        value = value.replace(str(self.sandbox.root), "<ROOT>").replace(str(self.sandbox.root).replace("\\", "/"), "<ROOT>")
        if field == "stdout":
            # Only CLI's labeled provenance timestamp and audit-line timestamp are volatile.
            value = re.sub(r"(?m)(^  scope .* · )\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", r"\1<TIME>", value)
            value = re.sub(r"(?m)^  \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ(?=  )", "  <TIME>", value)
        def identity(match):
            raw = match[0]
            if raw not in self.ids:
                self.ids[raw] = f"<{raw.split('_')[0]}_{len(self.ids)}>"
            return self.ids[raw]
        return re.sub(r"\b(?:mem|prop|prj|wt)_[a-f0-9]{16,}\b", identity, value)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--oracle", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    binary = args.candidate.resolve()
    candidate = [str(binary)] if binary.suffix.lower() == ".exe" else ["dotnet", str(binary)]
    sandboxes = [Sandbox(args.oracle), Sandbox(args.oracle)]
    commands = [[str(args.oracle.resolve())], candidate]
    normal = [Canonical(sandbox) for sandbox in sandboxes]
    peers = []
    checks = []
    aliases = [{}, {}]

    def check(name, condition, **evidence):
        checks.append({"name": name, "pass": bool(condition), **evidence})
        if not condition:
            print("FAIL " + name + ": " + json.dumps(evidence, ensure_ascii=True)[:7000], file=sys.stderr)

    def compare(name, values):
        left, right = [normal[index].value(value) for index, value in enumerate(values)]
        check(name, left == right, oracle=left, candidate=right)

    def start(index, name="claude-code", project=None):
        sandbox = sandboxes[index]
        peer = Peer([*commands[index], "mcp", "--project", str(project or sandbox.repository)], sandbox.repository, sandbox.environment)
        peers.append(peer)
        try:
            response = peer.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": name, "version": "fixture"}})
        except (EOFError, TimeoutError) as error:
            raise RuntimeError(f"Peer {index} startup failed: {peer.errors.decode('utf-8', errors='replace')}") from error
        assert "result" in response, response
        peer.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return peer

    def cli(index, *arguments, project=None):
        sandbox = sandboxes[index]
        result = subprocess.run([*commands[index], *arguments, "--project", str(project or sandbox.repository)], cwd=sandbox.repository,
                                env=sandbox.environment, capture_output=True, timeout=30)
        def decoded(data):
            text = data.decode("utf-8").replace("\r\n", "\n")
            try:
                return json.loads(text)
            except ValueError:
                return text
        return {"exit": result.returncode, "stdout": decoded(result.stdout), "stderr": decoded(result.stderr)}

    def payload(value):
        if isinstance(value, str) and value.startswith("$"):
            return lambda index: aliases[index][value[1:]]
        return lambda _: value

    def call(name, tool, arguments, agents=None, alias=None):
        results = []
        for index in range(2):
            values = {key: payload(value)(index) for key, value in arguments.items()}
            if "supersedes" in values and isinstance(values["supersedes"], list):
                values["supersedes"] = [payload(value)(index) for value in values["supersedes"]]
            envelope = (agents or current)[index].call(tool, values)
            result = envelope.get("result", envelope)
            if "structuredContent" in result:
                assert json.loads(result["content"][0]["text"]) == result["structuredContent"], result
            results.append(result)
            if alias and "structuredContent" in result:
                data = result["structuredContent"]
                entity = data.get("proposal") or data.get("memory")
                aliases[index][alias] = entity["id"]
        compare(name, results)
        return results

    def data(results, index=0):
        return results[index]["structuredContent"]

    def snapshot(index):
        path = sandboxes[index].repository / ".harness/harness.db"
        paths = {"project": path} if path.exists() else {}
        knowledge = Path(sandboxes[index].environment["HARNESS_CONFIG_DIR"]) / "knowledge"
        paths.update({entry.name: entry for entry in knowledge.glob("*.db") if entry.is_file()})
        result = {}
        for label, file in sorted(paths.items()):
            with closing(sqlite3.connect(file)) as database:
                result[label] = {table: database.execute(f"SELECT * FROM {table} ORDER BY rowid").fetchall()
                                 for table in ("memories", "memory_events", "memory_proposals")}
        return result

    def rejected(name, tool, arguments):
        before = [snapshot(index) for index in range(2)]
        results = call(name, tool, arguments)
        check(name + "-error", all(result.get("isError") for result in results))
        check(name + "-no-write", before == [snapshot(index) for index in range(2)])

    try:
        current = [start(index) for index in range(2)]
        toolsets = [peer.request("tools/list")["result"] for peer in current]
        compare("ten-exact-memory-schemas", [{tool["name"]: tool for tool in toolset["tools"] if tool["name"] in TOOLS} for toolset in toolsets])
        check("memory-bootstrap-tools-present", {t["name"] for t in toolsets[1]["tools"]}.issuperset(TOOLS | {"ping", "diagnostics", "state_put_test", "state_get_test"}))
        call("uninitialized-read", "memory_search", {})
        check("uninitialized-no-project-state", all(not (sandbox.repository / ".harness").exists() for sandbox in sandboxes))
        user = call("uninitialized-user-write", "memory_put", {"type": "fact", "content": "User prefers isolated fixture execution", "scope": "user"}, alias="user")
        check("user-write-without-project", all(not (sandbox.repository / ".harness").exists() for sandbox in sandboxes))
        for index in range(2):
            check(f"init-{index}", cli(index, "init")["exit"] == 0)
        other_agents = [start(index, "codex") for index in range(2)]
        call("all-current-goals-empty", "goals_current", {})
        call("all-current-constraints-empty", "constraints_current", {})
        call("source-and-confidence", "memory_put", {"type": "decision", "content": "Use JSON for task import", "scope": "path:src/tasks", "source_ref": "TASK-0009 review", "source_type": "user", "confidence": 0.9}, alias="old")
        call("cross-agent-decision-read", "decisions_for", {"scope": "path:src/tasks/import.cs"}, other_agents)
        call("cross-agent-supersede", "memory_supersede", {"id": "$old", "content": "Use YAML for task import", "reason": "hand-edited tasks"}, other_agents, alias="new")
        call("history-new", "memory_get", {"id": "$new", "history": True})
        call("history-old", "memory_get", {"id": "$old", "history": True})
        call("duplicate", "memory_put", {"type": "decision", "content": " use YAML for task import. ", "scope": "path:src/tasks"})
        call("near-duplicate", "memory_put", {"type": "decision", "content": "Task import uses YAML files"})
        call("force-near-duplicate", "memory_put", {"type": "decision", "content": "Task import uses YAML files", "force": True})
        call("expired", "memory_put", {"type": "constraint", "content": "Freeze deploys during migration", "valid_until": "2000-01-01"}, alias="expired")
        call("current-expiry-filter", "constraints_current", {})
        call("inactive-search", "memory_search", {"types": ["constraint"], "include_inactive": True, "limit": 1000})
        call("nullable-inputs", "memory_put", {"type": "fact", "content": "Nullable schema inputs remain optional", "confidence": None, "supersedes": None})
        call("nullable-types", "memory_search", {"query": "nullable", "types": None})
        call("integral-number-limit", "memory_search", {"limit": 1.0})
        call("goal", "memory_put", {"type": "goal", "content": "Ship the memory workflow", "scope": "subsystem:web"})
        call("scope-current", "goals_current", {"scope": "subsystem:web"})
        call("branch-rule", "memory_put", {"type": "warning", "content": "On main commit per task", "scope": "branch:main"})
        call("branch-application", "decisions_for", {"scope": "path:src/tasks/import.cs"})
        call("undefined-convention", "memory_search", {"types": ["convention"], "query": "serializer naming conventions"})
        call("undefined-wordless", "memory_search", {"types": ["convention"], "query": "!!!"})

        for name, tool, values in [
            ("missing-type", "memory_put", {"content": "x"}),
            ("unknown-property", "memory_put", {"type": "fact", "content": "x", "path": "src"}),
            ("wrong-bool", "memory_put", {"type": "fact", "content": "x", "force": "yes"}),
            ("wrong-array-item", "memory_search", {"types": [1]}),
            ("null-string", "memory_put", {"type": "fact", "content": None}),
            ("wrong-number", "memory_put", {"type": "fact", "content": "x", "confidence": "yes"}),
            ("fractional-limit", "memory_search", {"limit": 1.5}),
            ("overflow-integer", "memory_search", {"limit": 9223372036854775808}),
            ("negative-boundary-integer", "memory_search", {"limit": -9223372036854775808}),
            ("negative-overflow-integer", "memory_search", {"limit": -9223372036854775809}),
            ("rounded-positive-boundary", "memory_search", {"limit": 9223372036854775807}),
            ("invalid-type", "memory_put", {"type": "wish", "content": "x"}),
            ("invalid-scope", "memory_put", {"type": "fact", "content": "x", "scope": "team:a"}),
            ("invalid-expiry", "memory_put", {"type": "fact", "content": "x", "valid_until": "soon"}),
            ("oversize-content", "memory_put", {"type": "fact", "content": "ž" * 2001}),
            ("secret-content", "memory_put", {"type": "fact", "content": "DB password=synthetic on staging"}),
            ("invalid-source-type", "memory_put", {"type": "fact", "content": "x", "source_type": "rumour"}),
            ("invalid-confidence", "memory_put", {"type": "fact", "content": "x", "confidence": 1.5}),
            ("empty-content", "memory_put", {"type": "fact", "content": " "}),
            ("escaping-path", "memory_put", {"type": "fact", "content": "x", "scope": "path:../outside"}),
            ("supersede-no-reason", "memory_supersede", {"id": "$new", "content": "x", "reason": " "}),
            ("missing-memory", "memory_get", {"id": "mem_missing"}),
            ("empty-decision-scope", "decisions_for", {"scope": " "}),
        ]:
            rejected(name, tool, values)

        call("original-rule", "memory_put", {"type": "convention", "content": "Template enums are stored as integers"}, alias="enum")
        call("propose", "memory_propose", {"type": "convention", "content": "Template enums are stored as names", "source_ref": "review", "rationale": "API_KEY='synthetic fixture'"}, other_agents, alias="proposal")
        call("proposal-invisible", "memory_search", {"query": "names"})
        call("pending-list", "memory_proposals", {})
        call("already-proposed", "memory_propose", {"type": "convention", "content": "template enums are stored as names."})
        before = [snapshot(index) for index in range(2)]
        conflict = call("approval-conflict", "memory_review", {"id": "$proposal", "action": "approve"})
        check("approval-conflict-no-write", before == [snapshot(index) for index in range(2)])
        call("approve-supersedes", "memory_review", {"id": "$proposal", "action": "approve", "supersedes": ["$enum"], "note": "confirmed by user"}, alias="approved-proposal")
        call("approved-list", "memory_proposals", {"status": "approved"})
        call("already-memory", "memory_propose", {"type": "convention", "content": "Template enums are stored as names"})
        call("reject-proposal", "memory_propose", {"type": "warning", "content": "Never deploy on Fridays"}, alias="reject")
        call("reject-review", "memory_review", {"id": "$reject", "action": "reject", "note": "not a rule"})
        rejected("closed-review", "memory_review", {"id": "$reject", "action": "approve"})
        rejected("invalid-action", "memory_review", {"id": "$proposal", "action": "maybe"})
        rejected("invalid-review-scope", "memory_review", {"id": "$proposal", "action": "approve", "scope": "user"})
        rejected("invalid-list-status", "memory_proposals", {"status": "bad"})
        rejected("proposal-secret", "memory_propose", {"type": "fact", "content": "password=synthetic"})
        call("keep-both-proposal", "memory_propose", {"type": "convention", "content": "Template enums always use readable names"}, alias="keep")
        call("keep-both-review", "memory_review", {"id": "$keep", "action": "approve", "keep_both": True})
        call("pending-wider-edit", "memory_propose", {"type": "fact", "content": "Only approve this project local suggestion"}, alias="local-proposal")
        rejected("pending-review-cannot-widen", "memory_review", {"id": "$local-proposal", "action": "approve", "scope": "user"})
        call("approve-edited-scope-type", "memory_review", {"id": "$local-proposal", "action": "approve", "content": "A reviewed rule for the API subsystem", "type": "decision", "scope": "subsystem:api", "note": "edited by user"})

        # CLI output includes both intentionally different Go JSON shapes and human-readable text.
        for verb, alias in [("show", "new"), ("show", "user")]:
            compare("cli-memory-" + alias, [cli(index, "memory", verb, aliases[index][alias], "--json") for index in range(2)])
        compare("cli-memory-history-text", [cli(index, "memory", "show", aliases[index]["new"]) for index in range(2)])
        compare("cli-list-raw-json", [cli(index, "proposals", "list", "--status", "all", "--json") for index in range(2)])
        call("cli-review-proposal", "memory_propose", {"type": "decision", "content": "Use stable package lock files", "rationale": "package review"}, alias="cli")
        compare("cli-show-proposal", [cli(index, "proposals", "show", aliases[index]["cli"], "--json") for index in range(2)])
        compare("cli-proposal-text", [cli(index, "proposals", "show", aliases[index]["cli"]) for index in range(2)])
        compare("cli-approve-edited", [cli(index, "proposals", "approve", aliases[index]["cli"], "--content", "Use stable reviewed package lock files", "--note", "user confirmed", "--json") for index in range(2)])
        call("cli-reject-proposal", "memory_propose", {"type": "warning", "content": "Stop all Friday development"}, alias="cli-reject")
        compare("cli-reject", [cli(index, "proposals", "reject", aliases[index]["cli-reject"], "--note", "no", "--json") for index in range(2)])
        call("cli-conflict-proposal", "memory_propose", {"type": "convention", "content": "Template enum values use readable names"}, alias="cli-conflict")
        before = [snapshot(index) for index in range(2)]
        compare("cli-conflict-json", [cli(index, "proposals", "approve", aliases[index]["cli-conflict"], "--json") for index in range(2)])
        check("cli-conflict-no-write", before == [snapshot(index) for index in range(2)])
        compare("cli-conflict-text", [cli(index, "proposals", "approve", aliases[index]["cli-conflict"]) for index in range(2)])
        compare("cli-keep-both", [cli(index, "proposals", "approve", aliases[index]["cli-conflict"], "--keep-both", "--json") for index in range(2)])
        call("merge-predecessor-one", "memory_put", {"type": "warning", "content": "Process cleanup owns local handles"}, alias="merge-one")
        call("merge-predecessor-two", "memory_put", {"type": "warning", "content": "Worker cancellation has a bounded deadline"}, alias="merge-two")
        call("cli-merge-proposal", "memory_propose", {"type": "warning", "content": "Process lifetime policy combines cleanup and cancellation"}, alias="merge")
        compare("cli-repeatable-supersede", [cli(index, "proposals", "approve", aliases[index]["merge"], "--supersede", aliases[index]["merge-one"], "--supersede", aliases[index]["merge-two"], "--json") for index in range(2)])
        compare("cli-retire-missing-reason", [cli(index, "memory", "retire", aliases[index]["new"], "--json") for index in range(2)])
        for index in range(2):
            normal[index].retired.add(aliases[index]["new"])
        retired = [cli(index, "memory", "retire", aliases[index]["new"], "--reason", "obsolete format", "--json") for index in range(2)]
        compare("cli-retire", retired)
        check("retirement-time-matches-audit", all(result["stdout"]["memory"]["valid_until"] == result["stdout"]["events"][-1]["at"] for result in retired))
        call("retirement-visible", "memory_get", {"id": "$new", "history": True})

        # Workspace file changes must be visible to the existing MCP sessions on their next request.
        project_ids = []
        for index, sandbox in enumerate(sandboxes):
            with closing(sqlite3.connect(sandbox.repository / ".harness/harness.db")) as database:
                project_id = database.execute("SELECT id FROM projects").fetchone()[0]
            project_ids.append(project_id)
            write_json(Path(sandbox.environment["HARNESS_CONFIG_DIR"]) / "workspaces.json", {"version": 1, "workspaces": [
                {"id": "ws_0000000000000001", "name": "home", "members": [project_id]},
                {"id": "ws_0000000000000002", "name": "outside", "members": []},
            ]})
        call("workspace-write", "memory_put", {"type": "decision", "content": "Store project memory in the SQLite project database.", "scope": "workspace:home"}, alias="workspace")
        call("layered-current", "decisions_for", {"scope": "project"})
        call("workspace-read-history", "memory_get", {"id": "$workspace", "history": True})
        rejected("workspace-nonmember", "memory_put", {"type": "fact", "content": "must not write", "scope": "workspace:outside"})
        check("nonmember-no-store", all(not (Path(sandbox.environment["HARNESS_CONFIG_DIR"]) / "knowledge/workspace-ws_0000000000000002.db").exists() for sandbox in sandboxes))
        rejected("wider-move-rejected", "memory_supersede", {"id": "$workspace", "content": "replacement", "reason": "test", "scope": "user"})
        call("workspace-supersede", "memory_supersede", {"id": "$workspace", "content": "Store project memory in a shared SQLite project database.", "reason": "shared state", "scope": "workspace:home"}, alias="workspace-new")
        compare("cli-wider-history", [cli(index, "memory", "show", aliases[index]["workspace-new"], "--json") for index in range(2)])
        call("override-hint", "memory_put", {"type": "decision", "content": "Store project memory in the local SQLite project database."}, alias="project-override")
        call("layered-overrides", "memory_search", {"query": "SQLite", "scope": "project"})
        rejected("proposal-wider-scope", "memory_propose", {"type": "fact", "content": "must not propose wider", "scope": "user"})
        rejected("project-supersede-cannot-promote", "memory_supersede", {"id": "$project-override", "content": "replacement", "reason": "test", "scope": "user"})
        # Other projects supply evidence and suggestions through read-only connections.
        extra = []
        for index, sandbox in enumerate(sandboxes):
            directory = sandbox.root / "library"
            directory.mkdir()
            subprocess.run(["git", "init", "-b", "main", str(directory)], env=sandbox.environment, check=True, capture_output=True)
            subprocess.run(["git", "-C", str(directory), "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "Synthetic library"], env=sandbox.environment, check=True, capture_output=True)
            check(f"init-library-{index}", cli(index, "init", project=directory)["exit"] == 0)
            extra.append(directory)
        library = [start(index, "library-agent", extra[index]) for index in range(2)]
        call("other-project-convention", "memory_put", {"type": "convention", "content": "Serializer identifiers use camelCase naming"}, library)
        call("undefined-convention-elsewhere", "memory_search", {"types": ["convention"], "query": "serializer naming conventions"})
        call("other-project-evidence", "memory_put", {"type": "fact", "content": "Use short source statements for provenance"}, library)
        other_before = []
        for directory in extra:
            with closing(sqlite3.connect(directory / ".harness/harness.db")) as database:
                other_before.append(database.execute("SELECT * FROM memories ORDER BY rowid").fetchall())
        call("promotion-source", "memory_put", {"type": "fact", "content": "Use short source statements for provenance"}, alias="promote")
        for index in range(2):
            normal[index].retired.add(aliases[index]["promote"])
        compare("cli-promote-user", [cli(index, "memory", "promote", aliases[index]["promote"], "--to", "user", "--json") for index in range(2)])
        other_after = []
        for directory in extra:
            with closing(sqlite3.connect(directory / ".harness/harness.db")) as database:
                other_after.append(database.execute("SELECT * FROM memories ORDER BY rowid").fetchall())
        check("promotion-evidence-project-unchanged", other_before == other_after)
        promoted_source = call("promoted-source-retired", "memory_get", {"id": "$promote", "history": True})
        check("promotion-retirement-time-matches-audit", all(data(promoted_source, index)["memory"]["valid_until"] == data(promoted_source, index)["events"][-1]["at"] for index in range(2)))
        call("promoted-layer-search", "memory_search", {"types": ["fact"], "query": "provenance"})
        # Promotion conflicts and scope/authorization errors retain the source memory.
        before = [snapshot(index) for index in range(2)]
        compare("cli-promote-conflict", [cli(index, "memory", "promote", aliases[index]["project-override"], "--to", "workspace:home", "--json") for index in range(2)])
        check("cli-promote-conflict-no-write", before == [snapshot(index) for index in range(2)])
        compare("cli-promote-nonmember", [cli(index, "memory", "promote", aliases[index]["project-override"], "--to", "workspace:outside", "--json") for index in range(2)])
        check("cli-promote-nonmember-no-write", before == [snapshot(index) for index in range(2)])
        for index in range(2):
            normal[index].retired.add(aliases[index]["project-override"])
        compare("cli-promote-force", [cli(index, "memory", "promote", aliases[index]["project-override"], "--to", "workspace:home", "--force", "--json") for index in range(2)])
        compare("cli-promote-already-retired", [cli(index, "memory", "promote", aliases[index]["project-override"], "--to", "user", "--json") for index in range(2)])
        # Invalid membership or malformed workspace files must not create project-external stores.
        for index, sandbox in enumerate(sandboxes):
            write_json(Path(sandbox.environment["HARNESS_CONFIG_DIR"]) / "workspaces.json", {"version": 1, "workspaces": []})
        call("membership-refresh", "memory_search", {"query": "SQLite"})
        # Candidate-only containment correction: corrupt metadata is a tool error, without writing a path derived from it.
        before = snapshot(1)
        write_json(Path(sandboxes[1].environment["HARNESS_CONFIG_DIR"]) / "workspaces.json", {"version": 1, "workspaces": [
            {"id": "../escape", "name": "unsafe", "members": [project_ids[1]]},
        ]})
        corrupt = current[1].call("memory_put", {"type": "fact", "content": "must not store via corrupt identifier", "scope": "workspace:unsafe"})
        check("corrupt-workspace-id-tool-error", corrupt["result"].get("isError") and "Invalid workspace identifier" in corrupt["result"]["content"][0]["text"])
        check("corrupt-workspace-id-no-write", before == snapshot(1))
        (Path(sandboxes[1].environment["HARNESS_CONFIG_DIR"]) / "workspaces.json").write_text("{malformed", encoding="utf-8")
        malformed = current[1].call("memory_put", {"type": "fact", "content": "must not store via corrupt metadata", "scope": "user"})
        check("malformed-workspace-metadata-tool-error", malformed["result"].get("isError"))
        check("malformed-workspace-metadata-no-write", before == snapshot(1))
        check("all-contracts-completed", True, count=len(checks))
    finally:
        for peer in peers:
            peer.close()
        for sandbox in sandboxes:
            sandbox.close()
        write_json(args.output, {"source_commit": "9cb401dd2de4f85dd089fea86a0b139d81b178c6", "cases": checks,
                                 "passed": sum(case["pass"] for case in checks), "failed": sum(not case["pass"] for case in checks)})
    failures = [case["name"] for case in checks if not case["pass"]]
    if failures:
        raise AssertionError("Failed checks: " + ", ".join(failures))
    print(f"{len(checks)} memory contract checks passed")


if __name__ == "__main__":
    main()
