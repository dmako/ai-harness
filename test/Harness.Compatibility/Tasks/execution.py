"""PORT-011 task execution, nested validation, proposals and independent MCP processes."""
import argparse
from contextlib import closing
from datetime import datetime
import json
from pathlib import Path
import re
import sqlite3
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from compatibility import Peer, Sandbox, write_json

TOOLS = {"task_next", "task_get", "task_claim", "task_update", "task_complete", "task_propose"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--oracle", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    binary = args.candidate.resolve()
    commands = [[str(args.oracle.resolve())], [str(binary)] if binary.suffix == ".exe" else ["dotnet", str(binary)]]
    sandboxes = [Sandbox(args.oracle), Sandbox(args.oracle)]
    peers = []
    checks = []
    identities = [{}, {}]
    aliases = [{}, {}]

    def check(name, ok, **evidence):
        checks.append({"name": name, "pass": bool(ok), **evidence})
        if not ok:
            print("FAIL " + name + ": " + json.dumps(evidence, ensure_ascii=True)[:5000], file=sys.stderr)

    def canonical(value, index, field=""):
        if isinstance(value, dict):
            return {k: canonical(v, index, k) for k, v in sorted(value.items())}
        if isinstance(value, list):
            return [canonical(v, index, field) for v in value]
        if not isinstance(value, str):
            return value
        if field == "text":
            try:
                return canonical(json.loads(value), index)
            except ValueError:
                pass
        if field in {"at", "created_at", "updated_at", "claimed_at", "lease_until", "completed_at", "proposed_at", "decided_at"} and re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z", value):
            return "<TIME>"
        value = value.replace(str(sandboxes[index].root), "<ROOT>")
        value = re.sub(r"(?<=lease until )\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", "<LEASE>", value)
        value = re.sub(r"\[\d{4}-\d\d-\d\d \d\d:\d\d (?=[^\]]+\])", "[<NOTE_TIME> ", value)
        if field == "stdout":
            value = re.sub(r"(?m)(^Claimed .+ until |^  completed:  |^  )\d{4}-\d\d-\d\d \d\d:\d\d", r"\1<TIME>", value)
        def identity(match):
            raw = match[0]
            if raw not in identities[index]:
                identities[index][raw] = f"<{raw.split('_')[0]}_{len(identities[index])}>"
            return identities[index][raw]
        return re.sub(r"\b(?:clm|tpr|mem|prop|prj|wt)_[a-f0-9]{16,}\b", identity, value)

    def compare(name, values):
        normalized = [canonical(v, i) for i, v in enumerate(values)]
        check(name, normalized[0] == normalized[1], oracle=normalized[0], candidate=normalized[1])

    def cli(index, *arguments):
        s = sandboxes[index]
        p = subprocess.run([*commands[index], "tasks", *arguments, "--project", str(s.repository)], cwd=s.repository, env=s.environment, capture_output=True, timeout=30)
        out = p.stdout.decode("utf-8").replace("\r\n", "\n")
        try:
            out = json.loads(out)
        except ValueError:
            pass
        return {"exit": p.returncode, "stdout": out, "stderr": p.stderr.decode("utf-8").replace("\r\n", "\n")}

    def cli_pair(name, *arguments):
        values = [cli(i, *[aliases[i][a[1:]] if a.startswith("$") else a for a in arguments]) for i in range(2)]
        compare(name, values)
        return values

    def start(index, name):
        s = sandboxes[index]
        p = Peer([*commands[index], "mcp", "--project", str(s.repository)], s.repository, s.environment)
        peers.append(p)
        p.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": name, "version": "fixture"}})
        p.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return p

    def resolve(value, index):
        if isinstance(value, str) and value.startswith("$"): return aliases[index][value[1:]]
        if isinstance(value, list): return [resolve(v, index) for v in value]
        if isinstance(value, dict): return {k: resolve(v, index) for k, v in value.items()}
        return value

    def call(name, tool, arguments, clients=None, save=None, exact=True):
        values = [(clients or primary)[i].call(tool, resolve(arguments, i))["result"] for i in range(2)]
        for i, result in enumerate(values):
            if "structuredContent" in result:
                check(name + f" canonical text {i}", json.loads(result["content"][0]["text"]) == result["structuredContent"])
                if save:
                    data = result["structuredContent"]
                    aliases[i][save] = data.get("claim_token") or data.get("proposal", {}).get("id")
        if exact: compare(name, values)
        return values

    def snapshot(index):
        with closing(sqlite3.connect(sandboxes[index].repository / ".harness" / "harness.db")) as db:
            return {t: db.execute("SELECT * FROM " + t + " ORDER BY rowid").fetchall() for t in
                    ("tasks", "task_dependencies", "task_acceptance", "task_events", "task_proposals", "memories", "memory_events", "memory_proposals")}

    def rejected(name, tool, arguments, exact=True):
        before = [snapshot(i) for i in range(2)]
        values = call(name, tool, arguments, exact=exact)
        check(name + " rejected", all(v.get("isError") is True for v in values), values=values)
        check(name + " no mutation", all(snapshot(i) == before[i] for i in range(2)))

    try:
        for i, s in enumerate(sandboxes):
            p = subprocess.run([*commands[i], "init", "--project", str(s.repository)], cwd=s.repository, env=s.environment, capture_output=True, timeout=30)
            if p.returncode: raise RuntimeError(p.stderr.decode("utf-8"))
            (s.repository / "plan.yaml").write_bytes(b"version: 1\ntasks: [{id: T-1, title: First, acceptance: [build, verify]}, {id: T-2, title: Second, depends_on: [T-1]}, {id: T-3, title: Third}]\n")
            assert cli(i, "import", "plan.yaml")["exit"] == 0
        primary = [start(i, "claude-code") for i in range(2)]
        secondary = [start(i, "codex") for i in range(2)]
        tools = [p.request("tools/list")["result"]["tools"] for p in primary]
        for tool in sorted(TOOLS):
            compare(tool + " schema", [next(t for t in items if t["name"] == tool) for items in tools])
        check("twenty tools", len(tools[1]) == 20)
        call("get missing", "task_get", {"id": "T-99"})
        call("peek next", "task_next", {})
        claimed = call("claim via next", "task_next", {"claim": True, "lease_minutes": 1}, save="token")
        for i, result in enumerate(claimed):
            claim = result["structuredContent"]["task"]["claim"]
            check("one minute lease " + str(i), (datetime.fromisoformat(claim["lease_until"]) - datetime.fromisoformat(claim["claimed_at"])).total_seconds() == 60)
        call("claimed refusal", "task_claim", {"id": "T-1"}, clients=secondary)
        rejected("wrong token update", "task_update", {"id": "T-1", "claim_token": "wrong", "note": "lost", "acceptance": [{"index": 1, "result": "pass"}]})
        rejected("missing nested result", "task_update", {"id": "T-1", "claim_token": "$token", "acceptance": [{"index": 1}]})
        rejected("extra nested field", "task_update", {"id": "T-1", "claim_token": "$token", "acceptance": [{"index": 1, "result": "pass", "extra": 1}]})
        rejected("wrong nested type", "task_update", {"id": "T-1", "claim_token": "$token", "acceptance": [{"index": "1", "result": "pass"}]})
        rejected("invalid validation union", "task_complete", {"id": "T-1", "claim_token": "$token", "summary": "done", "validation": [False]})
        rejected("unknown validation field", "task_complete", {"id": "T-1", "claim_token": "$token", "summary": "done", "validation": [{"command": "build", "result": "pass", "extra": 1}]})
        call("progress and partial evidence", "task_update", {"id": " t-1 ", "claim_token": "$token", "status": "IN_PROGRESS", "note": "API_KEY='synthetic'", "acceptance": [{"index": 1, "result": " PASS ", "evidence": "checked"}]}, clients=secondary)
        rejected("invalid transition rolls note back", "task_update", {"id": "T-1", "claim_token": "$token", "status": "done", "note": "must roll back", "acceptance": [{"index": 2, "result": "pass"}]})
        for name, evidence in [
            ("missing acceptance", [{"index": 1, "result": "pass"}]),
            ("duplicate acceptance", [{"index": 1, "result": "pass"}, {"index": 1, "result": "pass"}, {"index": 2, "result": "pass"}]),
            ("unknown acceptance", [{"index": 9, "result": "pass"}]),
            ("unexplained n/a", [{"index": 1, "result": "pass"}, {"index": 2, "result": "n/a"}]),
        ]:
            rejected(name, "task_complete", {"id": "T-1", "claim_token": "$token", "summary": "done", "acceptance": evidence})
        acceptance = [{"index": 1, "result": "pass", "evidence": "tests"}, {"index": 2, "result": "n/a", "evidence": "not applicable"}]
        rejected("failed validation", "task_complete", {"id": "T-1", "claim_token": "$token", "summary": "done", "acceptance": acceptance, "validation": [{"command": "build", "result": "fail"}]})
        rejected("bad commit", "task_complete", {"id": "T-1", "claim_token": "$token", "summary": "done", "commit": "wrong", "acceptance": acceptance})
        call("review", "task_update", {"id": "T-1", "claim_token": "$token", "status": "review"})
        call("complete unlock and best-effort knowledge", "task_complete", {"id": "T-1", "claim_token": "$token", "summary": "done", "commit": "abcdef1", "acceptance": acceptance,
             "validation": ["test command", {"command": "build", "result": " PASS ", "output": "password='synthetic'"}],
             "proposals": [{"type": "warning", "content": "Keep task evidence with its completion"}, {"type": "unknown", "content": "invalid proposal"}]}, clients=secondary)
        call("complete audit", "task_get", {"id": "T-1", "events": True})
        call("memory proposal provenance", "memory_proposals", {})
        call("dependency now ready", "task_get", {"id": "T-2"})
        call("bounded lease", "task_claim", {"id": "T-2", "lease_minutes": 999999}, save="second")
        for i, s in enumerate(sandboxes):
            with closing(sqlite3.connect(s.repository / ".harness" / "harness.db")) as db:
                claim = db.execute("SELECT claimed_at,lease_until FROM tasks WHERE id='T-2'").fetchone()
                check("lease capped at 24h " + str(i), (datetime.fromisoformat(claim[1]) - datetime.fromisoformat(claim[0])).total_seconds() == 86400)
                db.execute("UPDATE tasks SET lease_until='2000-01-01T00:00:00.000000000Z' WHERE id='T-2'"); db.commit()
        call("reclaim expired", "task_claim", {"id": "T-2"}, clients=secondary, save="reclaimed")
        rejected("old token rejected after reclaim", "task_update", {"id": "T-2", "claim_token": "$second", "note": "lost"})
        call("release through progress", "task_update", {"id": "T-2", "claim_token": "$reclaimed", "status": "ready"}, clients=secondary)
        call("proposal", "task_propose", {"title": " New task ", "goal": " bounded outcome ", "acceptance": ["checked"], "depends_on": [" t-1 "], "reason": "follow-up", "scope": ["src"]}, save="proposal")
        cli_pair("proposals JSON", "proposals", "--json")
        cli_pair("proposals text", "proposals")
        cli_pair("approval", "approve", "$proposal", "--json")
        cli_pair("approval closed", "approve", "$proposal", "--json")
        call("proposal audit", "task_get", {"id": "T-4", "events": True})
        call("proposal to reject", "task_propose", {"title": "Rejected task", "goal": "outcome", "acceptance": ["checked"]}, save="rejection")
        cli_pair("reject proposal", "reject", "$rejection", "--note", " no ", "--json")
        cli_pair("all proposals", "proposals", "--status", "all", "--json")
        rejected("invalid task proposal", "task_propose", {"title": "", "goal": "outcome", "acceptance": ["check"]})
        rejected("unknown dependency proposal", "task_propose", {"title": "task", "goal": "outcome", "acceptance": ["check"], "depends_on": ["T-99"]})
        cli_pair("human claim", "claim", "T-2", "--lease", "1h30m", "--json")
        cli_pair("human release", "release", "T-2", "--json")
        call("agent claims before force release", "task_claim", {"id": "T-2"})
        cli_pair("force release needs reason", "release", "T-2", "--json")
        cli_pair("force release", "release", "T-2", "--reason", "stopped client", "--json")
        cli_pair("human claims to finish", "claim", "T-2", "--json")
        cli_pair("human completes", "complete", "T-2", "--summary", "finished", "--check", "build=pass", "--json")
        # Independent candidate processes: atomic next/claim must drain the remaining two tasks.
        racers = [start(1, "same-agent") for _ in range(4)]
        with ThreadPoolExecutor(max_workers=4) as pool:
            races = list(pool.map(lambda p: p.call("task_next", {"claim": True})["result"], racers))
        winners = [v["structuredContent"]["task"]["id"] for v in races if not v.get("isError") and v["structuredContent"]["task"]]
        check("atomic next across independent processes", all(not v.get("isError") for v in races) and sorted(winners) == ["T-3", "T-4"], winners=winners, results=races)
        proposal = primary[1].call("task_propose", {"title": "Concurrent approval", "goal": "Create once", "acceptance": ["one task"]})["result"]["structuredContent"]["proposal"]["id"]
        with ThreadPoolExecutor(max_workers=4) as pool:
            reviews = list(pool.map(lambda _: cli(1, "approve", proposal, "--json"), range(4)))
        check("independent CLI approvals create once", sum(r["exit"] == 0 for r in reviews) == 1, results=reviews)
        with closing(sqlite3.connect(sandboxes[1].repository / ".harness" / "harness.db")) as db:
            made = db.execute("SELECT id FROM tasks WHERE source=?", ("proposal:" + proposal,)).fetchall()
            status, resulting = db.execute("SELECT status,result FROM task_proposals WHERE id=?", (proposal,)).fetchone()
            check("proposal and created task commit together", len(made) == 1 and status == "approved" and made[0][0] == resulting)
    finally:
        for p in peers:
            if p.process.poll() is None: p.close()
        for s in sandboxes: s.close()
    write_json(args.output, {"task": "TASK-0011", "checks": checks, "passed": sum(c["pass"] for c in checks), "total": len(checks)})
    print(f"{sum(c['pass'] for c in checks)}/{len(checks)} checks passed")
    return 0 if all(c["pass"] for c in checks) else 1


if __name__ == "__main__":
    raise SystemExit(main())
