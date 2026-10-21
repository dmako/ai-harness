"""Record completed UI validation; --check verifies normalized source hashes."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
RECEIPT = ROOT / "doc/port/inventory/ui-validation.json"


def digest(path, normalize=False):
    data = path.read_bytes()
    return hashlib.sha256(data.replace(b"\r\n", b"\n") if normalize else data).hexdigest()


def results(directory, assemblies=1):
    paths = sorted((ROOT / directory).glob("*.trx"))
    assert len(paths) == assemblies, "The requested test run has not completed: " + directory
    counts = Counter()
    for path in paths:
        summary = ET.parse(path).find("{*}ResultSummary/{*}Counters")
        assert summary is not None, path
        for key in ["total", "passed", "failed", "notExecuted"]:
            counts[key] += int(summary.attrib.get(key, 0))
    assert counts["failed"] == 0 and counts["total"] == counts["passed"] + counts["notExecuted"], counts
    return dict(counts)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    if args.check:
        receipt = json.loads(RECEIPT.read_text(encoding="utf-8"))
        for name, expected in receipt["source_sha256_lf"].items():
            assert digest(ROOT / name, True) == expected, name
        for name, expected in receipt["embedded_asset_sha256"].items():
            assert digest(ROOT / name) == expected, name
        print("UI validation source and embedded asset hashes verified.")
        return
    changed = subprocess.check_output(["git", "diff", "HEAD", "--name-only", "--", ".gitattributes", "src", "test", "doc"], cwd=ROOT, text=True).splitlines()
    added = subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "--", "src", "test", "doc"], cwd=ROOT, text=True).splitlines()
    sources = sorted(set(changed + added) - {RECEIPT.relative_to(ROOT).as_posix()})
    go_path = ROOT / "artifacts/ui-original-go-linux-tests.jsonl"
    go_events = [json.loads(line) for line in go_path.read_text().splitlines()]
    go_tests = [e for e in go_events if e.get("Action") == "pass" and e.get("Test")]
    assert go_tests and not any(e.get("Action") == "fail" for e in go_events)
    test_directories = {"windows_ui": "artifacts/ui-tests-final", "linux_ui": "artifacts/ui-linux-tests-final", "windows_full": "artifacts/ui-full-tests-01", "windows_docs": "artifacts/ui-doc-contract-verified", "linux_docs": "artifacts/ui-linux-doc-contract-verified"}
    published = ROOT / "artifacts/ui-publish-smoke.json"
    smoke = json.loads(published.read_text())
    assert smoke["result"] == "pass"
    receipt = {
        "task": "TASK-0027", "base_commit": "b47c64760104fd92519a73a57d2c9c76d9f89ea4",
        "oracle_commit": "9cb401dd2de4f85dd089fea86a0b139d81b178c6",
        "generated_at": datetime.now(timezone.utc).isoformat(), "default_address": "127.0.0.1:17477",
        "source_sha256_lf": {name: digest(ROOT / name, True) for name in sources},
        "embedded_asset_sha256": {p.relative_to(ROOT).as_posix(): digest(p) for p in sorted((ROOT / "src/Harness.Web/Assets").iterdir()) if p.is_file()},
        "tests": {name: results(directory, 2 if name == "windows_full" else 1) for name, directory in test_directories.items()},
        "original_go_linux_race": {"passed_tests": len(go_tests), "packages": sorted(set(e["Package"] for e in go_tests))},
        "published_smoke": smoke,
        "limits": ["No interactive browser surface was available; local HTTP and frozen asset delivery were verified.",
                   "Mutation routes and SSE/live updates belong to TASK-0028.",
                   "Go's default 7477 intentionally changed to 17477 by user request; runtime health diagnostics use the C# services."],
        "artifacts_sha256": {},
    }
    files = [go_path, published, *[p for directory in test_directories.values() for p in sorted((ROOT / directory).glob("*.trx"))]]
    receipt["artifacts_sha256"] = {p.relative_to(ROOT).as_posix(): digest(p) for p in files}
    RECEIPT.write_text(json.dumps(receipt, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(receipt["tests"], indent=2))


if __name__ == "__main__":
    main()
