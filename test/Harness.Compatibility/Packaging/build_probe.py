"""Instrument the pinned Go fresh-machine runner; build a runtime-free test executable.

The original scenarios are retained. Instrumentation supplies normal writable OS
cache variables (the Go fixture only supplies Unix TMPDIR), adds companion
extraction, bounds the entire process, and runs the additional packaging checks.
This is an instrumented compatibility probe, not an unmodified Go test result.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[3]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--source', type=Path, required=True)
p.add_argument('--go', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
p.add_argument('--os', choices=['windows', 'linux', 'darwin'], required=True)
p.add_argument('--arch', choices=['amd64', 'arm64'], required=True)
a = p.parse_args()
source = a.source.resolve() / 'cmd/harness-e2e/main.go'
original = source.read_bytes()
baseline = json.loads((ROOT / 'doc/port/inventory/baseline.json').read_text(encoding='utf-8'))
if hashlib.sha256(original).hexdigest() != baseline['contract_source_hashes']['cmd/harness-e2e/main.go']:
    raise ValueError('Pinned upstream runner hash mismatch')
text = original.decode('utf-8').replace('\r\n', '\n')
text = text.replace(r'model = \"x\"\n\n[mcp_servers.other]', r'model = \"x\"\ncreated = 2026-10-22\nlocal_time = 07:35:00\n\n[mcp_servers.other]', 1)
text = text.replace('flag.Parse()', 'developer := flag.String("developer-archive", "", "explicit optional developer archive")\n\tflag.Parse()\n\ttime.AfterFunc(5*time.Minute, func() { fmt.Fprintln(os.Stderr, "probe deadline exceeded"); os.Exit(124) })', 1)
text = text.replace('check(n >= 2,', 'if *developer != "" { _, err = unpack(*developer, bin); must(err) }\n\tcheck(n >= 2,', 1)
text = text.replace('for _, tool := range []string{"node",', 'env = privateEnvironment(env, home)\n\tfor _, tool := range []string{"dotnet", "node",', 1)
text = text.replace('run("harness uninstall --all",', 'releaseChecks(harness, bin, home, repo, env, *archive, *developer != "")\n\trun("harness uninstall --all",', 1)
text = text.replace('cmd := exec.Command(harness, "mcp", "--project", repo)', 'cmd := protocolCommand(harness, repo)', 1)
text = text.replace('out, err := cmd.CombinedOutput()', 'started := time.Now()\n\t\tout, err := cmd.CombinedOutput()\n\t\tfmt.Printf("MEASURE %s %.2f ms\\n", what, float64(time.Since(started).Microseconds())/1000)', 1)
text = text.replace('if base != "harness"', 'if base != "e_sqlite3.dll" && base != "libe_sqlite3.so" && base != "libe_sqlite3.dylib" && base != "harness-collect" && base != "harness-share" && base != "harness-collect.exe" && base != "harness-share.exe" && base != "harness"', 1)
output = a.output.resolve()
output.parent.mkdir(parents=True, exist_ok=True)
stage = output.parent / ('probe-source-' + a.os + '-' + a.arch)
stage.mkdir(exist_ok=True)
(stage / 'main.go').write_text(text, encoding='utf-8', newline='\n')
extra = Path(__file__).with_name('checks.go')
(stage / 'checks.go').write_bytes(extra.read_bytes())
env = dict(os.environ, GOENV='off', GOTOOLCHAIN='local', GOTELEMETRY='off', GOWORK='off', CGO_ENABLED='0', GOOS=a.os, GOARCH=a.arch, GOCACHE=str(ROOT / 'artifacts/compatibility/go-cache'))
subprocess.run([str(a.go.resolve()), 'build', '-trimpath', '-o', str(output), str(stage / 'main.go'), str(stage / 'checks.go')], cwd=ROOT, env=env, check=True, timeout=300)
receipt = {'baseline_commit': baseline['source_commit'], 'upstream_runner_sha256': hashlib.sha256(original).hexdigest(), 'instrumented_runner_sha256': hashlib.sha256(text.encode()).hexdigest(), 'checks_sha256': hashlib.sha256(extra.read_bytes()).hexdigest(), 'binary_sha256': hashlib.sha256(output.read_bytes()).hexdigest(), 'os': a.os, 'arch': a.arch}
output.with_suffix(output.suffix + '.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
print(output)
