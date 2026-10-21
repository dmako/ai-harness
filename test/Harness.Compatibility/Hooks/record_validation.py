"""Record the completed hook validation; --check verifies its normalized source hashes."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[3]
RECEIPT = ROOT / "doc/port/inventory/hooks-validation.json"


def digest(path, normalize=False):
    data = path.read_bytes()
    return hashlib.sha256(data.replace(b"\r\n", b"\n") if normalize else data).hexdigest()


def test_results(directory):
    paths = sorted((ROOT / directory).glob("*.trx"))
    assert paths, directory
    counts = Counter()
    for path in paths:
        document = ET.parse(path)
        summary = document.find("{*}ResultSummary/{*}Counters")
        assert summary is not None, path
        for key in ["total", "passed", "failed", "notExecuted"]:
            counts[key] += int(summary.attrib.get(key, 0))
    assert counts["failed"] == 0 and counts["total"] == counts["passed"] + counts["notExecuted"], counts
    return dict(counts)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--full-results", default="artifacts/hooks-full-tests-02")
    parser.add_argument("--linux-unit", default="artifacts/hooks-unit-linux-final")
    parser.add_argument("--linux-integration", default="artifacts/hooks-integration-linux-final")
    args = parser.parse_args()
    if args.check:
        receipt = json.loads(RECEIPT.read_text(encoding="utf-8"))
        for name, expected in receipt["source_sha256_lf"].items():
            assert digest(ROOT / name, True) == expected, name
        print("Hook validation source hashes verified.")
        return
    changed = subprocess.check_output(["git", "diff", "--name-only", "--", "src", "test", "doc"], cwd=ROOT, text=True).splitlines()
    added = subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "--", "src", "test", "doc"], cwd=ROOT, text=True).splitlines()
    sources = sorted(set(changed + added) - {"doc/port/inventory/hooks-validation.json"})
    oracle = ROOT / "artifacts/compatibility/oracle/source"
    originals = ["internal/hooks/hooks.go", "internal/hooks/hooks_test.go", "internal/clients/hooks.go",
                 "internal/clients/hooks_test.go", "internal/clients/hookpayload.go", "internal/clients/hookpayload_test.go",
                 "internal/cli/hooks.go", "internal/cli/hookevent.go", "internal/cli/hookevent_test.go",
                 "internal/cli/completion.go", "internal/cli/completion_test.go", "internal/cli/hookpick.go"]
    go_events = [json.loads(line) for line in (ROOT / "artifacts/hooks-original-linux-tests-final.jsonl").read_text().splitlines()]
    go_tests = [event for event in go_events if event.get("Action") == "pass" and event.get("Test")]
    assert go_tests and not any(event.get("Action") == "fail" for event in go_events)
    assert len(list((ROOT / args.full_results).glob("*.trx"))) == 2, "The full solution run has not completed."
    receipt = {
        "task": "TASK-0026", "base_commit": "aa7aafe979232806ec96071aea0ad4d1ca03a7fa",
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "oracle_commit": "9cb401dd2de4f85dd089fea86a0b139d81b178c6",
        "source_sha256_lf": {name: digest(ROOT / name, True) for name in sources},
        "oracle_source_sha256_lf": {name: digest(oracle / name, True) for name in originals},
        "tests": {"windows_full": test_results(args.full_results), "linux_hooks_unit": test_results(args.linux_unit),
                  "linux_hooks_integration": test_results(args.linux_integration),
                  "original_go_linux_race": {"passed_tests": len(go_tests), "packages": sorted(set(event["Package"] for event in go_tests))}},
        "published_smoke": json.loads((ROOT / "artifacts/hooks-published-smoke.json").read_text()),
        "published_executable_sha256": digest(ROOT / "artifacts/hooks-publish-verified/harness.exe"),
        "wizard_smoke": json.loads((ROOT / "artifacts/clients-wizard-terminal.json").read_text()),
        "artifacts_sha256": {},
        "limits": ["macOS, native zsh and native fish execution unavailable; zsh/fish scripts match frozen Go byte-for-byte",
                   "completion lists the actual C# command model; descriptions need not duplicate Go wording"],
    }
    files = [*sorted((ROOT / args.full_results).glob("*.trx")), *sorted((ROOT / args.linux_unit).glob("*.trx")),
             *sorted((ROOT / args.linux_integration).glob("*.trx")), ROOT / "artifacts/hooks-original-linux-tests-final.jsonl",
             ROOT / "artifacts/hooks-published-smoke.json", ROOT / "artifacts/clients-wizard-terminal.json"]
    for path in files:
        receipt["artifacts_sha256"][path.relative_to(ROOT).as_posix()] = digest(path)
    RECEIPT.write_text(json.dumps(receipt, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(receipt["tests"], indent=2))


if __name__ == "__main__":
    main()
