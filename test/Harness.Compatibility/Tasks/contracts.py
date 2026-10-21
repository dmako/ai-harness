"""PORT-010 isolated task/backlog contracts against the frozen Go executable."""
import argparse
from contextlib import closing
import json
from pathlib import Path
import re
import sqlite3
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from compatibility import Sandbox, write_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--oracle", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    candidate = args.candidate.resolve()
    commands = [[str(args.oracle.resolve())], [str(candidate)] if candidate.suffix == ".exe" else ["dotnet", str(candidate)]]
    sandboxes = [Sandbox(args.oracle), Sandbox(args.oracle)]
    checks = []

    def check(name, ok, **evidence):
        checks.append({"name": name, "pass": bool(ok), **evidence})
        if not ok:
            print("FAIL " + name + ": " + json.dumps(evidence, ensure_ascii=True)[:5000], file=sys.stderr)

    def normalize(value, index, key=""):
        if isinstance(value, dict):
            return {k: normalize(v, index, k) for k, v in sorted(value.items())}
        if isinstance(value, list):
            return [normalize(v, index, key) for v in value]
        if not isinstance(value, str):
            return value
        if key in {"at", "created_at", "updated_at", "completed_at"} and re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z", value):
            return "<TIME>"
        value = value.replace(str(sandboxes[index].root), "<ROOT>")
        if key == "stdout":
            value = re.sub(r"(?m)(^  completed:  |^  )\d{4}-\d\d-\d\d \d\d:\d\d(?= by |  )", r"\1<TIME>", value)
        return value

    def cli(index, *arguments):
        s = sandboxes[index]
        process = subprocess.run([*commands[index], "tasks", *arguments, "--project", str(s.repository)], cwd=s.repository, env=s.environment, capture_output=True, timeout=30)
        out = process.stdout.decode().replace("\r\n", "\n")
        try:
            out = json.loads(out)
        except ValueError:
            pass
        return {"exit": process.returncode, "stdout": out, "stderr": process.stderr.decode().replace("\r\n", "\n")}

    def compare(name, *arguments):
        values = [cli(i, *arguments) for i in range(2)]
        canon = [normalize(v, i) for i, v in enumerate(values)]
        check(name, canon[0] == canon[1], oracle=canon[0], candidate=canon[1])
        return values

    def write(text):
        for s in sandboxes:
            (s.repository / "plan.yaml").write_bytes(text.encode("utf-8"))

    def snapshot(index):
        path = sandboxes[index].repository / ".harness" / "harness.db"
        with closing(sqlite3.connect(path)) as db:
            return {table: db.execute("SELECT * FROM " + table + " ORDER BY 1,2").fetchall() for table in
                    ("tasks", "task_dependencies", "task_acceptance", "task_events", "milestones")}

    def rejected(name, text, exact_error=True):
        write(text)
        before = [snapshot(i) for i in range(2)]
        values = [cli(i, "import", "plan.yaml", "--json") for i in range(2)]
        check(name + " rejected", all(v["exit"] == 1 for v in values), values=values)
        if exact_error:
            canon = [normalize(v, i) for i, v in enumerate(values)]
            check(name + " error", canon[0] == canon[1], oracle=canon[0], candidate=canon[1])
        check(name + " no writes", all(snapshot(i) == before[i] for i in range(2)))

    try:
        for i, s in enumerate(sandboxes):
            p = subprocess.run([*commands[i], "init", "--project", str(s.repository)], cwd=s.repository, env=s.environment, capture_output=True, timeout=30)
            if p.returncode:
                raise RuntimeError("init: " + p.stderr.decode())
        initial = """# planning comment
version: 1
milestones:
  - id: M1
    title: Foundation
    goal: Ship together
    exit: [Verified, 'Ĺ˝luĹĄouÄŤkĂ˝ đź§Ş <&>']
tasks:
  - id: T-1
    title: First
    milestone: M1
    status: done
    notes: preserve execution notes
    acceptance: [build, check]
  - id: T-2
    title: Second
    milestone: M1
    depends_on: [T-1, T-1]
    scope: [src, '', src, test]
    acceptance: [test]
  - id: T-3
    title: Third
    milestone: Undefined
    depends_on: [T-2]
  - id: T-4
    title: Urgent
    priority: critical
"""
        write(initial)
        before = [snapshot(i) for i in range(2)]
        compare("dry import", "import", "plan.yaml", "--dry-run", "--json")
        check("dry import no writes", all(snapshot(i) == before[i] for i in range(2)))
        compare("initial import", "import", "plan.yaml", "--json")
        compare("list all JSON", "list", "--all", "--json")
        compare("list default text", "list")
        compare("milestones JSON", "milestones", "--json")
        compare("milestones text", "milestones")
        compare("next priority", "next", "--json")
        compare("next milestone text", "next", "--milestone", "M1")
        compare("show imported done", "show", "t-1", "--events", "--json")
        compare("show imported done text", "show", "T-1", "--events")
        compare("repeat import", "import", "plan.yaml", "--json")
        for name, text in [
            ("duplicate id", "version: 1\ntasks: [{id: T-9, title: A}, {id: T-9, title: B}]"),
            ("invalid id", "version: 1\ntasks: [{id: bad, title: A}]"),
            ("self edge", "version: 1\ntasks: [{id: T-9, title: A, depends_on: [T-9]}]"),
            ("unknown edge", "version: 1\ntasks: [{id: T-9, title: A, depends_on: [T-99]}]"),
            ("cycle across orphan", "version: 1\ntasks: [{id: T-1, title: A, depends_on: [T-2]}]"),
            ("initial deferred", "version: 1\ntasks: [{id: T-9, title: A, status: deferred}]"),
            ("invalid priority", "version: 1\ntasks: [{id: T-9, title: A, priority: urgent}]"),
            ("invalid type", "version: 1\ntasks: [{id: T-9, title: A, type: todo}]"),
            ("missing title", "version: 1\ntasks: [{id: T-9}]"),
            ("unsupported version", "version: 2\ntasks: []"),
        ]:
            rejected(name, text)
        for name, text in [
            ("unknown field", "version: 1\ntasks: [{id: T-9, title: A, claimant: x}]"),
            ("duplicate key", "version: 1\ntasks: [{id: T-9, id: T-8, title: A}]"),
            ("wrong list type", "version: 1\ntasks: [{id: T-9, title: A, scope: wrong}]"),
        ]:
            rejected(name, text, exact_error=False)  # parser diagnostics are library-specific
        # Seed only synthetic execution data, identically, to exercise history without task-011 tools.
        for s in sandboxes:
            with closing(sqlite3.connect(s.repository / ".harness" / "harness.db")) as db:
                db.executescript("""UPDATE tasks SET status='review',claimant='agent',claim_token='private-token',claimed_at='2026-01-01T00:00:00.000000001Z',lease_until='2099-01-01T00:00:00.000000001Z' WHERE id='T-2';
                    UPDATE task_acceptance SET result='pass',evidence='prior evidence' WHERE task_id='T-1' AND idx=2;""")
        write("version: 1\ntasks: [{id: T-1, title: Revised, status: blocked, notes: ignored, acceptance: [check, new, build]}, {id: T-2, title: Still claimed, status: done, acceptance: [test]}]\n")
        compare("reimport execution preserved", "import", "plan.yaml", "--json")
        compare("completion preserved", "show", "T-1", "--events", "--json")
        values = compare("claim preserved and private", "show", "T-2", "--events", "--json")
        check("token never exposed", all("private-token" not in json.dumps(v) for v in values))
        compare("block privileged review", "block", "T-2", "--reason", "waiting", "--json")
        compare("defer blocked", "defer", "T-2", "--reason", "after release", "--json")
        compare("deferred default hidden", "list", "--json")
        compare("deferred explicit", "list", "--status", "done,deferred", "--status", "review", "--json")
        compare("resume deferred", "ready", "T-2", "--note", "now", "--json")
        compare("block dependency waiting", "block", "T-3", "--reason", "wait", "--json")
        compare("unblock dependency waiting", "ready", "T-3", "--json")
        compare("terminal transition rejected", "defer", "T-1", "--reason", "why", "--json")
        compare("missing reason usage", "block", "T-4", "--json")
        compare("bad status usage", "list", "--status", "nope", "--json")
        compare("add origin and next id", "add", "--title", "Manual", "--accept", "reviewed", "--depends", "T-1,T-2", "--scope", "src,test", "--json")
        compare("create audit", "show", "T-5", "--events", "--json")
        # Existing content remains a byte prefix; generated YAML may differ in scalar presentation.
        write(initial)
        compare("dry export", "export", "plan.yaml", "--dry-run", "--json")
        check("dry export file unchanged", all((s.repository / "plan.yaml").read_bytes() == initial.encode("utf-8") for s in sandboxes))
        compare("append missing export", "export", "plan.yaml", "--json")
        check("existing bytes retained", all((s.repository / "plan.yaml").read_bytes().startswith(initial.encode()) for s in sandboxes))
        compare("export idempotent", "export", "plan.yaml", "--json")
        compare("new backlog export", "export", "new.yaml", "--json")
        compare("new backlog reimport", "import", "new.yaml", "--json")
        compare("after roundtrip", "list", "--all", "--json")
        write("version: 1\ntasks: []\nmilestones: []\n")
        values = [cli(i, "export", "plan.yaml", "--json") for i in range(2)]
        check("nonfinal list export rejected", all(v["exit"] == 1 for v in values), values=values)
        check("failed export no file writes", all((s.repository / "plan.yaml").read_bytes() == b"version: 1\ntasks: []\nmilestones: []\n" for s in sandboxes))
        # CLI imports tasks first; milestone validation is a separate transaction.
        write("version: 1\ntasks: [{id: T-20, title: Partial}]\nmilestones: [{id: M2, title: A}, {id: M2, title: B}]\n")
        compare("milestone failure after tasks commit", "import", "plan.yaml", "--json")
        compare("partial task retained", "show", "T-20", "--json")
        write('{"version":1,"tasks":[{"id":"T-21","title":"JSON task"}]}')
        compare("JSON import", "import", "plan.yaml", "--json")
        compare("missing task", "show", "T-999", "--json")
        write("version: 1\ntasks: []\n")
        compare("empty inline export", "export", "plan.yaml", "--json")
        compare("inline reimport", "import", "plan.yaml", "--json")
        # Explicit Windows correction: the Go export regex rejects CRLF task keys.
        crlf = b"# Windows planning file\r\nversion: 1\r\ntasks:\r\n  - id: T-1\r\n    title: Existing\r\n"
        for s in sandboxes:
            (s.repository / "plan.yaml").write_bytes(crlf)
        crlf_results = [cli(i, "export", "plan.yaml", "--json") for i in range(2)]
        check("documented CRLF export correction", crlf_results[0]["exit"] == 1 and crlf_results[1]["exit"] == 0, values=crlf_results)
        check("CRLF existing bytes preserved", (sandboxes[0].repository / "plan.yaml").read_bytes() == crlf and (sandboxes[1].repository / "plan.yaml").read_bytes().startswith(crlf))
        # A copied task database is interchangeable in both directions.
        states = [snapshot(i) for i in range(2)]
        # Compare semantic tables, retaining all execution values except known variable timestamps.
        for table in ("task_dependencies", "task_acceptance"):
            check(table + " database parity", states[0][table] == states[1][table])
        for s in sandboxes:
            with closing(sqlite3.connect(s.repository / ".harness" / "harness.db")) as source, closing(sqlite3.connect(s.root / "exchange.db")) as destination:
                source.backup(destination)
        before_exchange = [cli(i, "list", "--all", "--json") for i in range(2)]
        for i, s in enumerate(sandboxes):
            with closing(sqlite3.connect(sandboxes[1-i].root / "exchange.db")) as source, closing(sqlite3.connect(s.repository / ".harness" / "harness.db")) as destination:
                source.backup(destination)
        for i in range(2):
            after = cli(i, "list", "--all", "--json")
            check(("Go reads C#" if i == 0 else "C# reads Go") + " database", normalize(after, i) == normalize(before_exchange[1-i], 1-i), after=normalize(after, i))
        values = compare("exchanged Unicode milestones reimport", "import", "new.yaml", "--json")
        check("Unicode milestone JSON is Go canonical", all(v["stdout"].get("milestones_changed") == 0 for v in values))
    finally:
        for s in sandboxes:
            s.close()
    write_json(args.output, {"task": "TASK-0010", "checks": checks, "passed": sum(c["pass"] for c in checks), "total": len(checks)})
    print(f"{sum(c['pass'] for c in checks)}/{len(checks)} checks passed")
    return 0 if all(c["pass"] for c in checks) else 1


if __name__ == "__main__":
    raise SystemExit(main())
