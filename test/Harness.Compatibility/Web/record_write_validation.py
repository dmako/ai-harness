"""Record PORT-028 checks, or verify their saved source and asset hashes."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import json
import subprocess
import xml.etree.ElementTree as ET

from record_validation import ROOT, digest, results

RECEIPT = ROOT / "doc/port/inventory/ui-writes-validation.json"


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
        print("UI writes validation source and asset hashes verified.")
        return

    full = "artifacts/ui-writes-full-01"
    windows = "artifacts/ui-writes-windows-final"
    linux = "artifacts/ui-writes-linux-final"
    full_results = results(full, 2)
    linux_results = results(linux)
    windows_results = results(windows)
    nodes = [node for path in (ROOT / windows).glob("*.trx")
             for node in ET.parse(path).findall(".//{*}UnitTestResult")
             if node.attrib["testName"].startswith("Harness.Integration.Web.")]
    assert nodes and all(node.attrib["outcome"] == "Passed" for node in nodes)
    assert any("UiBrowserTests." in node.attrib["testName"] for node in nodes)
    assert any("UiWriteGoContractTests." in node.attrib["testName"] for node in nodes)
    web = dict(Counter(node.attrib["outcome"] for node in nodes))
    smoke_paths = [ROOT / "artifacts/ui-writes-publish-smoke.json", ROOT / "artifacts/ui-writes-publish-read-smoke.json"]
    smoke = [json.loads(path.read_text(encoding="utf-8")) for path in smoke_paths]
    assert all(item["result"] == "pass" for item in smoke)
    assert smoke[0]["executable_sha256"] == smoke[1]["executable_sha256"]
    changed = subprocess.check_output(["git", "diff", "HEAD", "--name-only", "--", "src", "test", "doc"], cwd=ROOT, text=True).splitlines()
    added = subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "--", "src", "test", "doc"], cwd=ROOT, text=True).splitlines()
    sources = sorted(set(changed + added) - {RECEIPT.relative_to(ROOT).as_posix()})
    artifacts = [*smoke_paths, *[path for directory in [full, windows, linux] for path in (ROOT / directory).glob("*.trx")]]
    receipt = {
        "task": "TASK-0028",
        "base_commit": "ca6fbac64715ee388b4661bc56900c0b29fcbf6c",
        "oracle_commit": "9cb401dd2de4f85dd089fea86a0b139d81b178c6",
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "default_address": "127.0.0.1:17477",
        "tests": {"windows_full": full_results, "windows_final_web": windows_results, "windows_browser_and_web": web, "linux_final_web": linux_results},
        "validation_order": "The full regression run preceded the final field-type and session-error-envelope fixes; the final Windows and Linux Web suites cover those fixes and their two additional regression tests.",
        "browser": {"engine": "installed headless Chrome", "memory_conflict_choice": "pass",
                    "task_confirmation_and_edit": "pass", "sse_refresh": "pass",
                    "blocked_sse_polling": "pass", "return_to_live": "pass"},
        "published_writes_smoke": smoke[0], "published_read_smoke": smoke[1],
        "intentional_differences": [
            "Strict JSON writes reject non-object, trailing, invalid UTF-8 and repeated-field bodies.",
            "Frontend polls every fifteen seconds while SSE is unavailable; existing review forms are preserved.",
            "The listener retains the user-requested default port 17477."
        ],
        "limits": ["Linux browser review is skipped when HARNESS_UI_BROWSER is not supplied; Windows runs the actual browser flow.",
                   "Like frozen Go, live SQLite watches cover registered project databases."],
        "source_sha256_lf": {name: digest(ROOT / name, True) for name in sources},
        "embedded_asset_sha256": {path.relative_to(ROOT).as_posix(): digest(path) for path in sorted((ROOT / "src/Harness.Web/Assets").iterdir()) if path.is_file()},
        "artifacts_sha256": {path.relative_to(ROOT).as_posix(): digest(path) for path in artifacts},
    }
    RECEIPT.write_text(json.dumps(receipt, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(receipt["tests"], indent=2))


if __name__ == "__main__":
    main()
