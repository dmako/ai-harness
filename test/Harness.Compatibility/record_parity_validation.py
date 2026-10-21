"""Record parity evidence honestly, keeping missing platforms and failures visible."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def trx(directory, unit_override=None, web_override=None):
    result = {"status": "pending", "assemblies": []}
    if not directory:
        return result
    paths = {p.name: p for p in directory.glob("*.trx")}
    if unit_override:
        paths.update({p.name: p for p in unit_override.glob("*.trx")})
    for path in sorted(paths.values()):
        xml = ET.parse(path)
        counters = xml.find(".//{*}Counters").attrib
        skipped = [r.get("testName") for r in xml.findall(".//{*}UnitTestResult") if r.get("outcome") == "NotExecuted"]
        assembly = {"path": path.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "counters": counters, "skips": skipped}
        if "Integration" in path.name and web_override:
            cases = {r.get("testName"): r.get("outcome") for r in xml.findall(".//{*}UnitTestResult")}
            retests = []
            for retry in sorted(web_override.glob("*.trx")):
                for case in ET.parse(retry).findall(".//{*}UnitTestResult"):
                    name = case.get("testName")
                    if name not in cases:
                        raise ValueError("Retest case not in original suite: " + name)
                    cases[name] = case.get("outcome")
                retests.append({"path": retry.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(retry.read_bytes()).hexdigest()})
            if retests:
                assembly["initial_counters"] = dict(counters)
                assembly["retests"] = retests
                assembly["counters"] = dict(counters, passed=str(sum(v == "Passed" for v in cases.values())), failed=str(sum(v == "Failed" for v in cases.values())), notExecuted=str(sum(v == "NotExecuted" for v in cases.values())))
        result["assemblies"].append(assembly)
    if len(result["assemblies"]) == 2:
        result["status"] = "fail" if any(int(a["counters"]["failed"]) for a in result["assemblies"]) else "pass"
    return result


def go_tests(path):
    if not path.exists():
        return {"status": "pending"}
    events = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    if not events:
        return {"status": "pending"}
    active = set()
    failed = []
    for event in events:
        if event["Action"] == "start":
            active.add(event["Package"])
        if event["Action"] in ("pass", "fail", "skip") and "Test" not in event:
            active.discard(event["Package"])
        if event["Action"] == "fail":
            failed.append({"package": event["Package"], "test": event.get("Test")})
    return {"status": "running" if active else "fail" if failed else "pass", "passed_tests": sum(e["Action"] == "pass" and "Test" in e for e in events), "failures": failed, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--windows", type=Path, default=ROOT / "artifacts/parity-windows-final")
    p.add_argument("--windows-unit", type=Path, default=ROOT / "artifacts/parity-windows-unit-final")
    p.add_argument("--windows-web", type=Path, default=ROOT / "artifacts/parity-web-final-02")
    p.add_argument("--linux-web", type=Path, default=ROOT / "artifacts/parity-linux-web-final")
    p.add_argument("--linux", type=Path, default=ROOT / "artifacts/parity-linux-final")
    p.add_argument("--macos", type=Path)
    p.add_argument("--output", type=Path, default=ROOT / "doc/port/inventory/parity-validation.json")
    p.add_argument("--require-all-platforms", action="store_true")
    a = p.parse_args()
    platforms = {"windows": trx(a.windows.resolve(), a.windows_unit.resolve(), a.windows_web.resolve()), "linux": trx(a.linux.resolve(), web_override=a.linux_web.resolve()), "macos": trx(a.macos.resolve() if a.macos else None)}
    performance = {}
    for platform in ("windows", "linux"):
        path = ROOT / ("artifacts/parity-performance-" + platform + "-metadata-verification.json")
        if path.exists():
            data = json.loads(path.read_text(encoding="utf-8"))
            performance[platform] = {"path": path.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "host": data["host"], "status": "pass" if all(v["pass"] for v in data["verification"].values()) else "fail", "verification": data["verification"], "go": {k: v for k, v in data["go"].items() if k != "contexts"}, "csharp": {k: v for k, v in data["csharp"].items() if k != "contexts"}, "contexts": {lang: data[lang]["contexts"] for lang in ("go", "csharp")}}
    sources = ["src/Harness.Cli/Telemetry/ExportCommands.cs", "src/Harness.Infrastructure/Git/GitRepository.cs", "src/Harness.Infrastructure/Git/GitRepository.Head.cs", "src/Harness.Infrastructure/Git/GitRepository.Retrieval.cs", "src/Harness.Infrastructure/Projects/HarnessStateSession.cs", "src/Harness.Infrastructure/Projects/HarnessStateSession.Context.cs", "src/Harness.Infrastructure/Retrieval/RecentChangesCache.cs", "src/Harness.Web/Assets/app.js", "test/Harness.Integration/Web/UiBrowserTests.cs", "test/fixtures/compatibility/ui-review-port-changes.json"]
    result = {"schema": 1, "task": "TASK-0030", "oracle_commit": "9cb401dd2de4f85dd089fea86a0b139d81b178c6", "qualification": "pass" if all(p["status"] == "pass" for p in platforms.values()) else "pending", "platforms": platforms, "performance": performance, "original_go_linux": go_tests(ROOT / "artifacts/parity-original-go-linux.jsonl"), "original_go_race_linux": go_tests(ROOT / "artifacts/parity-original-go-race-linux.jsonl"), "source_sha256": {p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in sources}, "limitations": ["macOS not executed locally; prepared workflow has not run", "Measured candidate regression guards do not imply Go-equivalent cold startup, sync or memory", "Linux self-contained measurements use isolated OpenSSL 3 on a WSL Ubuntu 26.10/OpenSSL 4 host", "Performance samples include the mounted-Windows-filesystem cost for WSL executable loading"]}
    a.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\r\n")
    print(json.dumps({"qualification": result["qualification"], "platforms": {k: v["status"] for k, v in platforms.items()}, "original_go_linux": result["original_go_linux"]["status"]}))
    return int(a.require_all_platforms and result["qualification"] != "pass")

if __name__ == "__main__":
    raise SystemExit(main())
