"""Replay pinned Go developer-export and companion tests without editing the oracle."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

p = argparse.ArgumentParser(description=__doc__)
for name in ("source", "go", "modules", "output"):
    p.add_argument("--" + name, required=True, type=Path)
a = p.parse_args()
source = a.source.resolve()
env = dict(os.environ, GOTOOLCHAIN="local", GOENV="off", GOTELEMETRY="off",
           GOCACHE=str(source.parent.parent / "go-cache"), GOMODCACHE=str(a.modules.resolve()))
packages = ["./internal/devtelemetry", "./cmd/harness-collect/internal/collect", "./cmd/harness-share/internal/share"]
result = subprocess.run([str(a.go.resolve()), "test", "-mod=readonly", "-count=1", "-json", *packages], cwd=source, env=env, capture_output=True, timeout=240)
a.output.parent.mkdir(parents=True, exist_ok=True)
a.output.write_bytes(result.stdout)
a.output.with_suffix(".stderr.txt").write_bytes(result.stderr)
events = [json.loads(line) for line in result.stdout.splitlines()]
summary = {"schema": 1, "oracle_commit": "9cb401dd2de4f85dd089fea86a0b139d81b178c6",
           "packages": packages, "passed_tests": sum(e.get("Action") == "pass" and "Test" in e for e in events),
           "failed_tests": [e.get("Test", e.get("Package")) for e in events if e.get("Action") == "fail"],
           "result_sha256": hashlib.sha256(result.stdout).hexdigest(), "exit_code": result.returncode}
a.output.with_suffix(".summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
print(json.dumps(summary))
raise SystemExit(result.returncode)
