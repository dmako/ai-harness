"""Run the native regression suite with explicit frozen Go drivers and preserve TRX/binlogs."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[2]
ORACLES = {
    "STORAGE": "storage-oracle/storage-oracle",
    "MEMORY": "memory-oracle/memory-oracle-04",
    "PROVIDER": "provider-oracle/provider-oracle",
    "PROTOCOL": "provider-oracle/protocol-oracle",
    "GRAPHIFY": "graphify-oracle/graphify-oracle",
    "SYNC": "sync-oracle/sync-oracle",
    "RETRIEVAL": "retrieval-oracle/retrieval-oracle",
    "CONTEXT": "context-oracle/context-oracle",
    "WORKSPACE": "workspace-oracle/workspace-oracle",
    "ECOSYSTEM": "ecosystem-oracle/ecosystem-oracle",
    "ARCHITECTURE": "architecture-oracle/architecture-oracle",
    "CLONES": "clones-oracle/clones-oracle",
    "FINDINGS": "findings-oracle/findings-oracle-03",
    "TELEMETRY": "telemetry-oracle/telemetry-oracle-02",
    "CLIENTS": "clients-oracle/clients-oracle",
    "HOOKS": "hooks-oracle/hooks-oracle",
    "UI": "compatibility/oracle/harness",
    "FINDINGS_SERVER": "compatibility/oracle/harness",
}

def environment(oracle_map=None):
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", TESTINGPLATFORM_TELEMETRY_OPTOUT="1")
    suffix = ".exe" if os.name == "nt" else ""
    paths = {key: str(ROOT / "artifacts" / (value + suffix)) for key, value in ORACLES.items()}
    paths.update(oracle_map or {})
    missing = []
    for key, value in paths.items():
        if not Path(value).is_file():
            missing.append(key + "=" + value)
        env["HARNESS_GO_" + key + "_ORACLE" if key != "FINDINGS_SERVER" else "HARNESS_GO_FINDINGS_SERVER"] = value
    if missing:
        raise ValueError("Missing frozen drivers; build or provide --oracle-map: " + "; ".join(missing))
    return env

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--oracle-map", type=Path)
    p.add_argument("--built", action="store_true", help="Run built test DLLs, useful when only the runtime is installed")
    p.add_argument("--assembly", choices=["Harness.Unit", "Harness.Integration"], help="With --built, rerun just one native assembly")
    a = p.parse_args()
    output = a.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    env = environment(json.loads(a.oracle_map.read_text(encoding="utf-8")) if a.oracle_map else None)
    env.setdefault("DOTNET_CLI_HOME", str(ROOT / "artifacts/package-spike/cli-home"))
    env.setdefault("NUGET_PACKAGES", str(ROOT / "artifacts/package-spike/packages"))
    commands = [["dotnet", "test", "--solution", str(ROOT / "Harness.slnx"), "--no-restore", "-bl:" + str(output / ("regression-" + uuid.uuid4().hex + ".binlog")), "--report-trx", "--results-directory", str(output)]]
    if a.built:
        commands = [["dotnet", str(ROOT / "test" / name / "bin/Debug/net10.0" / (name + ".dll")), "--report-trx", "--results-directory", str(output)] for name in ([a.assembly] if a.assembly else ("Harness.Unit", "Harness.Integration"))]
    results = []
    for i, command in enumerate(commands):
        with (output / (str(i) + ".log")).open("wb") as log:
            result = subprocess.run(command, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=1800)
        results.append({"command": command, "exit_code": result.returncode})
        print(json.dumps(results[-1]), flush=True)
    (output / "commands.json").write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    return int(any(r["exit_code"] for r in results))

if __name__ == "__main__":
    raise SystemExit(main())
