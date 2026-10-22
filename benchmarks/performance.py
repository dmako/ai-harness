"""Measure one harness executable with repeatable cold startup and warm MCP workloads."""
import argparse
import contextlib
import json
import math
from pathlib import Path
import platform
import statistics
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'test/Harness.Packaging'))
from run_probe import Sandbox, sha


def measured(call):
    started = time.perf_counter_ns()
    call()
    return (time.perf_counter_ns() - started) / 1_000_000


def stats(samples):
    ordered = sorted(samples)
    median = statistics.median(samples)
    return {'samples_ms': samples, 'median_ms': median,
            'p95_ms': ordered[max(0, math.ceil(len(ordered) * .95) - 1)],
            'mad_ms': statistics.median(abs(value - median) for value in samples)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--samples', type=int, default=10)
    parser.add_argument('--warm-samples', type=int, default=50)
    args = parser.parse_args()
    if min(args.samples, args.warm_samples) < 1:
        parser.error('Sample counts must be positive')
    with tempfile.TemporaryDirectory(prefix='harness-benchmark-') as directory:
        sandbox = Sandbox(directory, args.candidate)
        sandbox.cli('init')
        startup = [measured(lambda: sandbox.cli('version', '--json')) for _ in range(args.samples)]
        handshakes = []
        for _ in range(args.samples):
            started = time.perf_counter_ns()
            with contextlib.closing(sandbox.peer()) as peer:
                peer.initialize()
                handshakes.append((time.perf_counter_ns() - started) / 1_000_000)
        with contextlib.closing(sandbox.peer()) as peer:
            peer.initialize()
            call = lambda: peer.call('get_context', {'task': 'Review project decisions', 'budget': 4000, 'depth': 'light'})
            for _ in range(5):
                call()
            context = [measured(call) for _ in range(args.warm_samples)]
    result = {'schema': 1, 'host': platform.platform(), 'machine': platform.machine(),
              'candidate': str(args.candidate.resolve()), 'sha256': sha(args.candidate),
              'conditions': 'New process for cold samples; one initialized MCP session after five warmups; private state, no providers, empty child PATH.',
              'startup': stats(startup), 'mcp_initialize': stats(handshakes), 'warm_get_context': stats(context)}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(args.output)


if __name__ == '__main__':
    main()
