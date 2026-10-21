"""Compare pinned Go and C# executables on the same synthetic workload, with raw samples.

No machine cache is flushed: cold means a new process, warm means an established
MCP session after five warmup calls. Calibration and verification are separate runs.
"""
import argparse
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import statistics
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "test/Harness.Compatibility"))
from compatibility import Sandbox


def memory(process):
    if os.name == "nt":
        from ctypes import wintypes
        class Counters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("faults", wintypes.DWORD)] + [(n, ctypes.c_size_t) for n in ("peak_rss", "rss", "peak_paged", "paged", "peak_nonpaged", "nonpaged", "pagefile", "peak_pagefile")]
        counters = Counters()
        counters.cb = ctypes.sizeof(counters)
        if not ctypes.windll.psapi.GetProcessMemoryInfo(wintypes.HANDLE(int(process._handle)), ctypes.byref(counters), counters.cb):
            raise ctypes.WinError()
        return {"rss_bytes": counters.rss, "peak_rss_bytes": counters.peak_rss}
    status = Path("/proc") / str(process.pid) / "status"
    if status.exists():
        rows = dict(line.split(":", 1) for line in status.read_text(encoding="utf-8").splitlines() if ":" in line)
        return {"rss_bytes": int(rows["VmRSS"].split()[0]) * 1024, "peak_rss_bytes": int(rows["VmHWM"].split()[0]) * 1024}
    rss = int(subprocess.check_output(["ps", "-o", "rss=", "-p", str(process.pid)], text=True).strip()) * 1024
    return {"rss_bytes": rss, "peak_rss_bytes": None}


def stats(values):
    ordered = sorted(values)
    median = statistics.median(values)
    return {"samples": values, "median": median, "p95": ordered[max(0, math.ceil(len(values) * .95) - 1)], "mad": statistics.median(abs(v - median) for v in values)}


def timed(call):
    start = time.perf_counter_ns()
    value = call()
    return (time.perf_counter_ns() - start) / 1e6, value


def run(executable, samples, warm_samples):
    sandbox = Sandbox(executable)
    try:
        for i in range(100):
            (sandbox.repository / ("Type" + str(i) + ".cs")).write_text("namespace Fixture; public sealed class Type" + str(i) + " {}\n", encoding="utf-8")
        sandbox.git("add", ".")
        sandbox.git("commit", "-m", "Synthetic benchmark corpus")
        def cli(*args):
            result = sandbox.cli(*args)
            if result["exit_code"]:
                raise RuntimeError(str(result))
            return result
        cli("init")
        cli("sync")
        startup = [timed(lambda: cli("version", "--json"))[0] for _ in range(samples)]
        cold = []
        for _ in range(samples):
            start = time.perf_counter_ns()
            peer = sandbox.peer()
            peer.initialize()
            cold.append((time.perf_counter_ns() - start) / 1e6)
            peer.close()
        contexts = {}
        for count in (0, 1, 4):
            configuration = sandbox.repository / ".harness/config.toml"
            logs = []
            lines = []
            for i in range(count):
                log = sandbox.root / ("provider-" + str(count) + "-" + str(i) + ".jsonl")
                logs.append(log)
                lines.append('[providers.fixture' + str(i) + ']\ntype = "harness"\ncommand = ' + json.dumps(sys.executable) + '\nargs = ' + json.dumps([str(ROOT / "test/Harness.Compatibility/fake_provider.py"), "--mode", "normal", "--log", str(log)]) + '\ntimeout_ms = 2000\ndir = "project"\n')
            configuration.write_text("\n".join(lines), encoding="utf-8")
            peer = sandbox.peer()
            peer.initialize()
            def call():
                response = peer.call("get_context", {"task": "Fix Widget", "budget": 8000, "depth": "light"}, timeout=30)
                if response.get("error") or response.get("result", {}).get("isError"):
                    raise RuntimeError(str(response))
                return response
            for _ in range(5):
                call()
            durations, sizes, used = [], [], []
            for _ in range(warm_samples):
                elapsed, response = timed(call)
                durations.append(elapsed)
                sizes.append(len(json.dumps(response["result"], ensure_ascii=False, separators=(",", ":")).encode("utf-8")))
                used.append(response["result"].get("structuredContent", {}).get("used_bytes", 0))
            mem = memory(peer.process)
            peer.close()
            requests = []
            for log in logs:
                events = [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines()]
                requests.append(sum(e.get("direction") == "request" and e.get("message", {}).get("method") == "tools/call" and e["message"].get("params", {}).get("name") != "harness_provider_info" for e in events))
            contexts[str(count)] = {"latency_ms": stats(durations), "response_bytes": stats(sizes), "used_bytes": stats(used), "memory": mem, "provider_requests": requests}
        (sandbox.repository / ".harness/config.toml").write_text("# No provider refresh in sync measurements\n", encoding="utf-8")
        cli("sync")
        noop = [timed(lambda: cli("sync"))[0] for _ in range(samples)]
        changed = []
        for i in range(samples):
            (sandbox.repository / "Widget.cs").write_text("class Widget { public int Value => " + str(i) + "; }\n", encoding="utf-8")
            changed.append(timed(lambda: cli("sync"))[0])
        return {"executable_sha256": hashlib.sha256(executable.read_bytes()).hexdigest(), "version": cli("version", "--json")["stdout"], "startup_version_ms": stats(startup), "cold_mcp_ms": stats(cold), "contexts": contexts, "sync_noop_ms": stats(noop), "sync_one_file_ms": stats(changed)}
    finally:
        sandbox.close()


def metrics(result):
    for key in ("startup_version_ms", "cold_mcp_ms", "sync_noop_ms", "sync_one_file_ms"):
        yield key, result[key]
    for count, context in result["contexts"].items():
        yield "context_" + count + "_ms", context["latency_ms"]
        yield "context_" + count + "_bytes", context["response_bytes"]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--go", type=Path, required=True, dest="oracle")
    p.add_argument("--candidate", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--samples", type=int, default=9)
    p.add_argument("--warm-samples", type=int, default=30)
    p.add_argument("--baseline", type=Path)
    a = p.parse_args()
    if a.samples < 5 or a.warm_samples < 10:
        p.error("Use at least 5 process samples and 10 warm samples")
    result = {"schema": 1, "host": {"platform": platform.platform(), "python": platform.python_version(), "cpu_count": os.cpu_count()}, "method": "101 C# files, no remote services; process-cold, filesystem-warm; five MCP warmups; independent isolated state", "samples": a.samples, "warm_samples": a.warm_samples}
    for name, executable in (("go", a.oracle.resolve()), ("csharp", a.candidate.resolve())):
        print("Measuring " + name, flush=True)
        result[name] = run(executable, a.samples, a.warm_samples)
    go_metrics = dict(metrics(result["go"]))
    result["ratios_csharp_over_go"] = {key: value["median"] / go_metrics[key]["median"] if go_metrics[key]["median"] else None for key, value in metrics(result["csharp"])}
    # Calibrate a candidate regression envelope from measured variation, never from guessed Go parity.
    result["limits"] = {key: {"median_max": value["p95"] + 3 * value["mad"], "basis": "measured C# p95 + 3 median absolute deviations; local regression guard, not a Go-parity claim"} for key, value in metrics(result["csharp"])}
    if a.baseline:
        baseline = json.loads(a.baseline.read_text(encoding="utf-8"))
        result["verification"] = {key: {"pass": value["median"] <= baseline["limits"][key]["median_max"], "actual": value["median"], "limit": baseline["limits"][key]["median_max"]} for key, value in metrics(result["csharp"])}
    a.output.parent.mkdir(parents=True, exist_ok=True)
    a.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result["ratios_csharp_over_go"], indent=2), flush=True)
    return int(any(not check["pass"] for check in result.get("verification", {}).values()))

if __name__ == "__main__":
    raise SystemExit(main())
