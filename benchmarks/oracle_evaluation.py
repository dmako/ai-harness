"""Run the frozen evaluation engine against a C# harness executable; keep native result formats.

This is test-only tooling, never a release dependency. It does not run a paid agent
unless levelb/judge is explicitly requested. Arguments after -- go to the engine.
"""
import argparse
import os
from pathlib import Path
import subprocess

p = argparse.ArgumentParser(description=__doc__)
p.add_argument("--source", type=Path, required=True)
p.add_argument("--go", type=Path, required=True)
p.add_argument("--modules", type=Path, required=True)
p.add_argument("--output", type=Path, required=True)
p.add_argument("--engine", choices=["bench", "e2e"], default="bench")
p.add_argument("arguments", nargs=argparse.REMAINDER)
a = p.parse_args()
source = a.source.resolve()
output = a.output.resolve()
output.mkdir(parents=True, exist_ok=True)
exe = output / ("harness-" + a.engine + (".exe" if os.name == "nt" else ""))
env = dict(os.environ, GOTOOLCHAIN="local", GOENV="off", GOTELEMETRY="off",
           GOCACHE=str(source.parent.parent / "go-cache"), GOMODCACHE=str(a.modules.resolve()))
subprocess.run([str(a.go.resolve()), "build", "-mod=readonly", "-buildvcs=false", "-trimpath", "-o", str(exe), "./cmd/harness-" + a.engine], cwd=source, env=env, check=True, timeout=240)
args = a.arguments[1:] if a.arguments[:1] == ["--"] else a.arguments
raise SystemExit(subprocess.run([str(exe), *args], env=env, check=False).returncode)
