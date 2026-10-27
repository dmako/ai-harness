"""Exercise extracted native packages in private child environments without language runtimes on PATH."""
import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path
import platform
import queue
import re
import subprocess
import tarfile
import tempfile
import threading
import time
import urllib.error
import urllib.request
import zipfile


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class Child:
    def __init__(self, command, cwd, env):
        self.process = subprocess.Popen(command, cwd=cwd, env=env, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        text=True, encoding='utf-8', bufsize=1)
        self.lines = queue.Queue()
        self.errors = []
        self.output_thread = threading.Thread(target=self._read, daemon=True)
        self.error_thread = threading.Thread(target=self._errors, daemon=True)
        self.output_thread.start()
        self.error_thread.start()

    def _read(self):
        for line in self.process.stdout:
            self.lines.put(line)
        self.lines.put(None)

    def _errors(self):
        for line in self.process.stderr:
            self.errors.append(line)

    def line(self, timeout=30):
        value = self.lines.get(timeout=timeout)
        if value is None:
            raise RuntimeError('Child closed stdout: ' + ''.join(self.errors))
        return value

    def close(self, terminate=False):
        if self.process.poll() is None:
            if terminate:
                self.process.terminate()
            else:
                self.process.stdin.close()
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=10)
        self.output_thread.join(timeout=2)
        self.error_thread.join(timeout=2)
        self.process.stdout.close()
        self.process.stderr.close()
        if not self.process.stdin.closed:
            self.process.stdin.close()
        return self.process.returncode


class Peer(Child):
    def __init__(self, command, cwd, env):
        super().__init__(command, cwd, env)
        self.sequence = 0

    def request(self, method, params=None):
        self.sequence += 1
        message = {'jsonrpc': '2.0', 'id': self.sequence, 'method': method}
        if params is not None:
            message['params'] = params
        self.process.stdin.write(json.dumps(message) + '\n')
        self.process.stdin.flush()
        while True:
            response = json.loads(self.line())
            if response.get('id') == self.sequence:
                if 'error' in response:
                    raise RuntimeError(str(response['error']))
                return response['result']

    def initialize(self):
        result = self.request('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
                                             'clientInfo': {'name': 'harness-package-check', 'version': '1'}})
        self.process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.process.stdin.flush()
        return result

    def call(self, name, arguments=None):
        result = self.request('tools/call', {'name': name, 'arguments': arguments or {}})
        if result.get('isError'):
            raise RuntimeError(str(result))
        if 'structuredContent' in result:
            return result['structuredContent']
        return json.loads(next(item['text'] for item in result['content'] if item['type'] == 'text'))


class Sandbox:
    def __init__(self, root, binary):
        self.root = Path(root)
        self.binary = Path(binary).resolve()
        self.repository = self.root / 'repository'
        self.home = self.root / 'home'
        for path in [self.repository / '.git', self.home / '.claude', self.home / '.codex', self.home / '.tmp']:
            path.mkdir(parents=True, exist_ok=True)
        (self.repository / '.git/HEAD').write_text('ref: refs/heads/main\n', encoding='utf-8')
        self.env = dict(os.environ, PATH='', DOTNET_ROOT=str(self.root / 'no-runtime'), DOTNET_MULTILEVEL_LOOKUP='0',
                        HOME=str(self.home), USERPROFILE=str(self.home), HARNESS_CONFIG_DIR=str(self.home / 'config'),
                        XDG_CONFIG_HOME=str(self.home / '.config'), LOCALAPPDATA=str(self.home / '.localappdata'),
                        APPDATA=str(self.home / '.appdata'), TEMP=str(self.home / '.tmp'), TMP=str(self.home / '.tmp'),
                        TMPDIR=str(self.home / '.tmp'), DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(self.home / '.bundle'))

    def cli(self, *args):
        result = subprocess.run([str(self.binary), *args], cwd=self.repository, env=self.env,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', timeout=30)
        if result.returncode:
            raise RuntimeError(f'{args}: exit {result.returncode}: {result.stderr} {result.stdout}')
        return result.stdout

    def peer(self, binary=None, arguments=None):
        return Peer([str(binary or self.binary), *(arguments or ['mcp', '--project', str(self.repository)])],
                    self.repository, self.env)


def extract(archive, destination):
    destination.mkdir(parents=True)
    if archive.suffix == '.zip':
        with zipfile.ZipFile(archive) as source:
            if any(not (destination / name).resolve().is_relative_to(destination) for name in source.namelist()):
                raise ValueError('Unsafe archive path')
            source.extractall(destination)
    else:
        with tarfile.open(archive) as source:
            source.extractall(destination, filter='data')


def http(address, path, method='GET', body=None, headers=None):
    request = urllib.request.Request(address.rstrip('/') + path, data=body, method=method, headers=headers or {})
    try:
        response = urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.read()


def probe(main_archive, developer_archive, root, report):
    main = root / 'main'
    developer = root / 'developer'
    extract(main_archive, main)
    extract(developer_archive, developer)
    suffix = '.exe' if os.name == 'nt' else ''
    sandbox = Sandbox(root / 'state', main / ('harness' + suffix))

    def check(condition, name):
        report['passed_checks' if condition else 'failed_checks'] += 1
        report['log'].append(('PASS ' if condition else 'FAIL ') + name)
        if not condition:
            report['failures'].append(name)

    def cli(name, *arguments):
        started = time.monotonic()
        answer = sandbox.cli(*arguments)
        check(True, name)
        report['measurements'].append({'name': name, 'milliseconds': (time.monotonic() - started) * 1000})
        return answer

    library = 'e_sqlite3.dll' if os.name == 'nt' else 'libe_sqlite3.dylib' if platform.system() == 'Darwin' else 'libe_sqlite3.so'
    for directory in [main, developer]:
        check((directory / library).is_file(), directory.name + ' SQLite sidecar')
        check((directory / 'README.md').is_file(), directory.name + ' installation guide')
        check((directory / 'THIRD_PARTY_NOTICES.json').is_file(), directory.name + ' package notices')
    check('.NET ' in cli('version', 'version', '--json'), 'runtime metadata')
    cli('init', 'init')
    database = sandbox.repository / '.harness/harness.db'
    check(database.is_file(), 'SQLite state created')
    if os.name != 'nt':
        check(database.stat().st_mode & 0o077 == 0, 'SQLite owner-only permissions')
    foreign = 'title = "unrelated"\ncreated = 2026-10-22\nlocal_time = 07:35:00\n'
    (sandbox.home / '.codex/config.toml').write_text(foreign, encoding='utf-8')
    cli('client installation', 'install', '--client', 'all')
    cli('agent instructions', 'instructions')
    check('harness' in cli('embedded docs', 'docs', 'guide').lower(), 'documentation content')
    cli('provider packs', 'init', '--dry-run')
    cli('doctor', 'doctor')
    created = json.loads(cli('task creation', 'tasks', 'add', '--title', 'Package lifecycle',
                             '--accept', 'first', '--accept', 'second', '--json'))
    task_id = created['task']['id']
    cli('task claim alias', 'task', 'claim', task_id, '--json')
    completed = json.loads(cli('task completion', 'tasks', 'complete', task_id, '--summary', 'Verified package',
                               '--acc', '1=pass:verified', '--acc', '2=pass:verified',
                               '--check', 'harness version=pass', '--json'))
    check(completed['task']['status'] == 'done', 'task lifecycle persisted')
    backlog = str(sandbox.home / 'backlog.yaml')
    cli('YAML export', 'tasks', 'export', backlog)
    cli('YAML import', 'tasks', 'import', backlog, '--json')
    with contextlib.closing(sandbox.peer()) as peer:
        check(bool(peer.initialize()['serverInfo']), 'MCP initialize')
        tools = peer.request('tools/list')['tools']
        check(len(tools) == 32, 'MCP tool inventory')
        peer.call('memory_put', {'type': 'decision', 'content': 'Package checks persist across MCP sessions', 'force': True})
        check(True, 'MCP memory persistence')
        check('budget_bytes' in peer.call('get_context', {'task': 'Package checks', 'budget': 4000}), 'MCP context')
        check(task_id in json.dumps(peer.call('task_get', {'id': task_id})), 'MCP task lookup')
    check(not list((sandbox.home / '.bundle').rglob('*')), 'no managed native-library extraction')
    with contextlib.closing(sandbox.peer(main / ('harness-graphify' + suffix),
                                       ['--root', str(sandbox.repository), '--graphify', str(root / 'missing-backend')])) as peer:
        peer.initialize()
        check(bool(peer.request('tools/list')['tools']), 'Graphify adapter handshake without optional backend')

    def service(binary, *args):
        child = Child([str(binary), *args], sandbox.repository, sandbox.env)
        try:
            for _ in range(30):
                match = re.search(r'https?://(?:127\.0\.0\.1|localhost):\d+', child.line())
                if match:
                    return child, match.group()
            raise RuntimeError('Service did not announce its address')
        except BaseException:
            child.close(terminate=True)
            raise

    child, address = service(sandbox.binary, 'ui', '--no-open', '--addr', '127.0.0.1:0')
    try:
        for path in ['/', '/app.js', '/style.css', '/api/projects', '/api/machine']:
            check(http(address, path)[0] == 200, 'UI ' + path)
    finally:
        check(child.close(terminate=True) == 0 if os.name != 'nt' else child.close(terminate=True) is not None,
              'UI process cleanup')
    reports = sandbox.home / 'reports'
    child, address = service(developer / ('harness-collect' + suffix), '--dir', str(reports),
                             '--addr', '127.0.0.1:0', '--token', 'package-token')
    try:
        check(http(address, '/v1/report', 'POST', b'{}', {'Content-Type': 'application/json'})[0] == 401,
              'collector rejects unauthenticated reports')
        cli('telemetry enable', 'telemetry', 'enable', '--endpoint', address + '/v1/report', '--token', 'package-token')
        check(bool(cli('telemetry preview', 'telemetry', 'preview')), 'telemetry preview content')
        cli('telemetry send', 'telemetry', 'send')
        state = sandbox.home / 'config/telemetry.json'
        if os.name != 'nt':
            check(state.stat().st_mode & 0o077 == 0, 'telemetry token owner-only permissions')
        cli('telemetry disable', 'telemetry', 'disable')
        check(bool(list(reports.rglob('*.json'))), 'collector persisted report')
    finally:
        exit_code = child.close(terminate=True)
        check(os.name == 'nt' or exit_code == 0, 'collector process cleanup')
    child, address = service(developer / ('harness-share' + suffix), '--dir', str(main_archive.parent),
                             '--addr', '127.0.0.1:0')
    try:
        check(http(address, '/download/' + main_archive.name, 'HEAD')[0] == 200, 'share archive HEAD')
        check(http(address, '/download/' + main_archive.name, 'POST', b'bad')[0] == 405, 'share rejects writes')
        check(http(address, '/unlisted.txt')[0] == 404, 'share path allowlist')
    finally:
        exit_code = child.close(terminate=True)
        check(os.name == 'nt' or exit_code == 0, 'share process cleanup')
    cli('client uninstall', 'uninstall', '--all')
    check((sandbox.home / '.codex/config.toml').read_text(encoding='utf-8').strip() == foreign.strip(),
          'unrelated TOML values preserved')
    check(database.is_file(), 'uninstall retains project data')
    cli('explicit data deletion', 'uninstall', '--all', '--delete-data', '--project', str(sandbox.repository))
    check(not database.exists(), 'project data deleted only with explicit selection')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive', required=True, type=Path)
    parser.add_argument('--developer-archive', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = {'status': 'fail', 'host': platform.platform(), 'machine': platform.machine(),
              'environment': 'Child PATH empty, nonexistent DOTNET_ROOT; private HOME/config/cache. Host may have SDKs.',
              'passed_checks': 0, 'failed_checks': 0, 'failures': [], 'log': [], 'measurements': [],
              'archive_sha256': {'main': sha(args.archive), 'developer': sha(args.developer_archive)}}
    started = time.monotonic()
    # Native Linux temporary storage preserves POSIX permission semantics in WSL.
    with tempfile.TemporaryDirectory(prefix='harness-package-') as temporary:
        try:
            probe(args.archive.resolve(), args.developer_archive.resolve(), Path(temporary).resolve(), report)
        except Exception as error:
            report['failed_checks'] += 1
            report['failures'].append(str(error))
    report['elapsed_seconds'] = time.monotonic() - started
    report['status'] = 'fail' if report['failures'] else 'pass'
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    args.output.with_suffix('.log').write_text('\n'.join(report['log'] + report['failures']) + '\n', encoding='utf-8')
    print(f"{report['status']}: {report['passed_checks']} checks; {report['failures']}")
    return int(report['status'] != 'pass')


if __name__ == '__main__':
    raise SystemExit(main())
