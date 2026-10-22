"""Run a native instrumented release probe with no language runtime on its PATH."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import tempfile
import time

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--probe', type=Path, required=True)
p.add_argument('--archive', type=Path, required=True)
p.add_argument('--developer-archive', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
a.output.parent.mkdir(parents=True, exist_ok=True)
# WSL workspaces may live on a Windows mount without POSIX mode metadata.
# Keep Unix fixtures on the system temporary filesystem to test chmod honestly.
temporary_parent = a.output.parent.resolve() if os.name == 'nt' else None
with tempfile.TemporaryDirectory(prefix='harness-private-probe-', dir=temporary_parent) as temporary:
    base = Path(temporary)
    acl = None
    if os.name == 'nt':
        # A private inheritable owner-only DACL protects the bundle extraction
        # directory created by the child. No machine ACL or install is changed.
        ps = 'powershell.exe'
        script = "$ErrorActionPreference='Stop'; $p=$env:HARNESS_PROBE_PARENT; $sid=[System.Security.Principal.WindowsIdentity]::GetCurrent().User; $acl=New-Object System.Security.AccessControl.DirectorySecurity; $acl.SetOwner($sid); $acl.SetAccessRuleProtection($true,$false); $rule=New-Object System.Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'); $acl.AddAccessRule($rule); [System.IO.Directory]::SetAccessControl($p,$acl); [System.IO.Directory]::GetAccessControl($p).GetSecurityDescriptorSddlForm('All')"
        acl = subprocess.check_output([ps, '-NoProfile', '-NonInteractive', '-Command', script], env=dict(os.environ, HARNESS_PROBE_PARENT=str(base)), text=True).strip()
    else:
        base.chmod(0o700)
    env = dict(os.environ, PATH='', TEMP=str(base), TMP=str(base), TMPDIR=str(base), DOTNET_ROOT=str(base / 'no-runtime'), DOTNET_MULTILEVEL_LOOKUP='0')
    command = [str(a.probe.resolve()), '--archive', str(a.archive.resolve()), '--developer-archive', str(a.developer_archive.resolve())]
    started = time.monotonic()
    result = subprocess.run(command, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=330)
    elapsed = time.monotonic() - started
    log = a.output.with_suffix('.log')
    log.write_bytes(result.stdout)
    text = result.stdout.decode('utf-8', errors='replace')
    receipt = {'status': 'pass' if result.returncode == 0 else 'fail', 'exit_code': result.returncode, 'host': platform.platform(), 'machine': platform.machine(), 'environment': 'Native host with PATH empty, nonexistent DOTNET_ROOT, multilevel lookup disabled; private HOME/cache in probe. Host itself may have SDKs installed.', 'parent_acl_sddl': acl, 'elapsed_seconds': elapsed, 'passed_checks': sum(line.startswith('PASS ') for line in text.splitlines()), 'failures': [line for line in text.splitlines() if line.startswith('FAIL ')], 'notes': [line for line in text.splitlines() if line.startswith('NOTE ')], 'measurements': [line for line in text.splitlines() if line.startswith('MEASURE ')], 'files': {str(file.name): hashlib.sha256(file.read_bytes()).hexdigest() for file in (a.probe, a.archive, a.developer_archive, log)}}
    # Main and developer archives intentionally share a basename; bind each
    # explicitly instead of relying on the legacy basename-keyed files map.
    receipt['archive_sha256'] = {
        'main': hashlib.sha256(a.archive.read_bytes()).hexdigest(),
        'developer': hashlib.sha256(a.developer_archive.read_bytes()).hexdigest(),
    }
    a.output.write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print(text)
    print(a.output)
    raise SystemExit(result.returncode)
