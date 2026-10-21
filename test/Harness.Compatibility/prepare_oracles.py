"""Build every pinned Go driver for the current host, sequentially (bridges share source)."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser(description=__doc__)
for name in ("source", "go", "modules", "output"):
    p.add_argument("--" + name, required=True, type=Path)
a = p.parse_args()
source, go, modules, output = [getattr(a, k).resolve() for k in ("source", "go", "modules", "output")]
output.mkdir(parents=True, exist_ok=True)
suffix = ".exe" if os.name == "nt" else ""
env = dict(os.environ, GOENV="off", GOTOOLCHAIN="local", GOTELEMETRY="off", GOWORK="off", CGO_ENABLED="0", GOMODCACHE=str(modules), GOCACHE=str(output / "go-cache"))
paths = {}
for key, directory in [("STORAGE", "../Harness.Integration/Storage/Go"), ("MEMORY", "../Harness.Integration/Memory/Go"), ("PROVIDER", "Providers"), ("PROTOCOL", "Providers"), ("GRAPHIFY", "Graphify"), ("SYNC", "Sync"), ("RETRIEVAL", "Retrieval"), ("CONTEXT", "Context"), ("WORKSPACE", "Workspaces"), ("ECOSYSTEM", "Ecosystem"), ("ARCHITECTURE", "Architecture"), ("CLONES", "Clones"), ("FINDINGS", "Findings"), ("TELEMETRY", "Telemetry"), ("CLIENTS", "Clients"), ("HOOKS", "Hooks")]:
    exe = output / (key.lower() + suffix)
    folder = Path(__file__).parent / directory
    builder = folder / "build_oracle.py"
    if key in ("STORAGE", "MEMORY", "PROVIDER", "PROTOCOL"):
        bridge = folder / ({"PROVIDER": "oracle.go", "PROTOCOL": "protocol_oracle.go"}.get(key, "main.go"))
        command = [sys.executable, str(Path(__file__).parent / "Providers/build_oracle.py"), "--source", str(source), "--go", str(go), "--modules", str(modules), "--output", str(exe), "--bridge", str(bridge)]
    else:
        command = [sys.executable, str(builder), "--source", str(source), "--go", str(go), "--modules", str(modules), "--output", str(exe)]
    subprocess.run(command, env=env, check=True, timeout=300)
    paths[key] = str(exe)
exe = output / ("harness" + suffix)
subprocess.run([str(go), "build", "-mod=readonly", "-buildvcs=false", "-trimpath", "-ldflags", "-X harness/internal/buildinfo.Version=compat-9cb401dd2de4 -X harness/internal/buildinfo.Commit=9cb401dd2de4f85dd089fea86a0b139d81b178c6 -X harness/internal/buildinfo.Date=2026-10-20T00:00:00Z", "-o", str(exe), "./cmd/harness"], cwd=source, env=env, check=True, timeout=300)
paths.update(UI=str(exe), FINDINGS_SERVER=str(exe))
(output / "oracle-map.json").write_text(json.dumps(paths, indent=2) + "\n", encoding="utf-8")
print("Built all drivers: " + str(output / "oracle-map.json"), flush=True)
