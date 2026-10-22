"""Verify local release artifacts and record executed and pending qualification."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import tarfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[3]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--release', type=Path, required=True)
p.add_argument('--output', type=Path, default=ROOT / 'doc/port/inventory/release-validation.json')
a = p.parse_args()
release = a.release.resolve()

def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def log_receipt(path, platform):
    log = ROOT / path
    body = log.read_text(encoding='utf-8')
    passed = sum(line.startswith('PASS ') for line in body.splitlines())
    failures = [line for line in body.splitlines() if line.startswith('FAIL ')]
    success = 'All checks passed on ' + platform in body and not failures
    if not success:
        raise ValueError('Incomplete/failed probe: ' + path)
    return {'status': 'pass', 'log': path, 'log_sha256': sha(log), 'passed_checks': passed, 'failures': failures, 'measurements': [line for line in body.splitlines() if line.startswith('MEASURE ')]}

def tests(folder):
    files = list((ROOT / folder).glob('*.trx'))
    if len(files) != 1:
        raise ValueError('Missing CLI test results')
    counters = next(e for e in ET.parse(files[0]).iter() if e.tag.endswith('Counters'))
    if int(counters.get('failed', '0')) or counters.get('passed') != '2':
        raise ValueError('CLI tests did not pass')
    return {'status': 'pass', 'passed': 2, 'trx': files[0].relative_to(ROOT).as_posix(), 'sha256': sha(files[0])}

manifest = json.loads((release / 'RELEASE.json').read_text(encoding='utf-8'))

def verify_notices(read):
    entries = json.loads(read('THIRD_PARTY_NOTICES.json'))
    for entry in entries:
        if entry['kind'] == 'runtimepack' and not entry['notices']:
            raise ValueError('Missing runtime license texts')
        for notice in entry['notices']:
            if hashlib.sha256(read(notice['file'])).hexdigest() != notice['sha256']:
                raise ValueError('License text hash mismatch')

for line in (release / 'SHA256SUMS').read_text(encoding='utf-8').splitlines():
    expected, name = line.split('  ', 1)
    path = (release / name).resolve()
    if not path.is_relative_to(release) or sha(path) != expected:
        raise ValueError('Checksum mismatch: ' + name)
for artifact in manifest['artifacts']:
    path = release / artifact['file']
    if sha(path) != artifact['sha256'] or not all(b['repeat_publish_verified'] for b in artifact['binaries']):
        raise ValueError('Unverified artifact: ' + str(path))
    if path.suffix == '.zip':
        with zipfile.ZipFile(path) as archive:
            names = archive.namelist()
            verify_notices(archive.read)
    else:
        with tarfile.open(path) as archive:
            members = archive.getmembers()
            names = [entry.name for entry in members]
            verify_notices(lambda name: archive.extractfile(name).read())
            for entry in members:
                if entry.name in {b['file'] for b in artifact['binaries']} and entry.mode != 0o755:
                    raise ValueError('Archive executable permissions missing')
    for required in ['BUILD.json', 'THIRD_PARTY_NOTICES.json', 'LICENSE-GO.txt', 'NOTICE.md', 'RELEASE_NOTES.md', *[b['file'] for b in artifact['binaries']]]:
        if required not in names:
            raise ValueError('Missing archive entry: ' + required)
    artifact['archive_contents_verified'] = True
windows = log_receipt('artifacts/release-windows-smoke-final.log', 'windows/amd64')
windows['host_receipt'] = json.loads((ROOT / 'artifacts/release-windows-smoke-final.json').read_text(encoding='utf-8'))
linux = log_receipt('artifacts/release-linux-x64-container-final.log', 'linux/amd64')
arm = log_receipt('artifacts/release-linux-arm64-container-final.log', 'linux/arm64')
environment = ROOT / 'artifacts/release-container-environment.log'
environment_text = environment.read_text(encoding='utf-8')
if not all('ABSENT ' + name in environment_text for name in ['dotnet', 'node', 'python', 'python3', 'npm', 'go', 'git']):
    raise ValueError('Clean container environment not proven')
sources = ['configuration/Release.pubxml', 'src/Harness.Cli/AuxiliaryCommands.cs', 'src/Harness.Cli/CliApplication.cs', 'scripts/publish_release.py', 'scripts/release_licenses.py', 'test/Harness.Compatibility/Packaging/build_probe.py', 'test/Harness.Compatibility/Packaging/checks.go', 'test/Harness.Compatibility/Packaging/run_probe.py', '.github/workflows/release.yml']
result = {'schema': 1, 'task': 'TASK-0031', 'recorded_utc': datetime.now(timezone.utc).isoformat(), 'qualification': 'pending', 'version': manifest['version'], 'release_directory': release.relative_to(ROOT).as_posix(), 'manifest_sha256': sha(release / 'RELEASE.json'), 'checksum_file_sha256': sha(release / 'SHA256SUMS'), 'build_commit': manifest['commit'], 'working_tree_modified': manifest['modified'], 'sdk': manifest['sdk'], 'self_contained': True, 'single_file': True, 'trimmed': False, 'native_aot': False, 'artifacts': manifest['artifacts'], 'tests': {'windows_cli': tests('artifacts/release-cli-tests-final'), 'linux_cli': tests('artifacts/release-cli-tests-linux-final')}, 'execution': {'windows_x64_isolated_host': windows, 'linux_x64_clean_container': linux, 'linux_arm64_emulated_clean_container': arm, 'macos_x64': {'status': 'pending', 'reason': 'No macOS host/runner available'}, 'macos_arm64': {'status': 'pending', 'reason': 'No macOS host/runner available'}, 'linux_arm64_native_hardware': {'status': 'pending', 'reason': 'Local Docker CPU emulation only'}, 'windows_console_signals': {'status': 'pending', 'reason': 'Services were forcibly cleaned up on Windows; Unix SIGTERM was verified'}}, 'container': {'image': 'mcr.microsoft.com/dotnet/runtime-deps:10.0-noble', 'digest': 'sha256:1c20bc9ebda297198be069d82b6c5d1716faeeec8b8dca53ef963f9c92d3e2e3', 'network': 'none', 'user': '65532:65532', 'tmpfs': '/tmp:rw,exec,mode=1777', 'environment_log_sha256': sha(environment)}, 'ci': {'workflow': '.github/workflows/release.yml', 'status': 'prepared_not_executed', 'reason': 'Repository has no configured remote/runner'}, 'source_sha256': {name: sha(ROOT / name) for name in sources}, 'limitations': ['Cross-publishing and emulation do not qualify native macOS/ARM64 hosts', 'Windows environment isolation is not a clean Windows VM', 'Startup values are single smoke samples, not benchmark distributions; ARM64 emulation is not a performance baseline', 'Frozen Go fresh-machine runner is instrumented for private OS temp/cache variables and additional packaging checks; original scenario bodies retained', 'Initial unmodified Windows Go runner could not start the .NET bundle without writable cache variables; initial Linux tmpfs was noexec; corrected fixtures are recorded separately', 'Core requires supported native OS dependencies; writable executable extraction storage is required for bundled native SQLite', 'Top-level license for new C# contributions is undeclared; packaging preserves upstream MIT and actual package/runtime notices without inventing a license']}
a.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8', newline='\r\n')
print(json.dumps({'qualification': result['qualification'], 'artifacts': len(result['artifacts']), 'executables_repeat_verified': sum(len(a['binaries']) for a in result['artifacts']), 'windows_checks': windows['passed_checks'], 'linux_x64_checks': linux['passed_checks'], 'linux_arm64_checks': arm['passed_checks']}))
