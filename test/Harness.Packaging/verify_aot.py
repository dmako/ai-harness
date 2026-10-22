"""Verify a native host release and bind its runtime probe to both exact archives."""
import argparse
import hashlib
import json
from pathlib import Path
import tarfile
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def verify(release, smoke):
    manifest = json.loads((release / 'RELEASE.json').read_text(encoding='utf-8'))
    for flag, expected in {'native_aot': True, 'trimmed': True, 'self_contained': True, 'single_file': False}.items():
        if manifest.get(flag) is not expected:
            raise ValueError('Unexpected deployment flag: ' + flag)
    for line in (release / 'SHA256SUMS').read_text(encoding='utf-8').splitlines():
        expected, name = line.split('  ', 1)
        path = (release / name).resolve()
        if not path.is_relative_to(release) or digest(path.read_bytes()) != expected:
            raise ValueError('Checksum mismatch: ' + name)
    if smoke['status'] != 'pass' or smoke['failures']:
        raise ValueError('Runtime probe did not pass')
    artifacts = []
    for artifact in manifest['artifacts']:
        path = release / artifact['file']
        if digest(path.read_bytes()) != artifact['sha256']:
            raise ValueError('Archive hash mismatch')
        if smoke['archive_sha256'][artifact['group']] != artifact['sha256']:
            raise ValueError('Runtime probe belongs to another archive')
        if path.suffix == '.zip':
            archive = zipfile.ZipFile(path)
            names = archive.namelist()
            read = archive.read
        else:
            archive = tarfile.open(path)
            names = archive.getnames()
            read = lambda name: archive.extractfile(name).read()
        with archive:
            if len(names) != len(set(names)) or any('..' in Path(name).parts for name in names):
                raise ValueError('Unsafe or duplicate archive entry')
            if digest(read('README.md')) != manifest['source_sha256']['README.md']:
                raise ValueError('Installation README mismatch')
            for entry in json.loads(read('THIRD_PARTY_NOTICES.json')):
                for notice in entry['notices']:
                    if digest(read(notice['file'])) != notice['sha256']:
                        raise ValueError('License notice mismatch')
            binaries = []
            for binary in artifact['binaries']:
                data = read(binary['file'])
                if digest(data) != binary['sha256'] or not binary['repeat_publish_verified']:
                    raise ValueError('Executable hash/repeat check failed')
                # The Native AOT runtime exports this debugger header. Ordinary
                # managed PE assemblies/apphosts are not accepted on size alone.
                if b'DotNetRuntimeDebugHeader\0' not in data:
                    raise ValueError('Missing Native AOT runtime marker: ' + binary['file'])
                magic = (b'MZ' if artifact['rid'].startswith('win-') else
                         b'\xcf\xfa\xed\xfe' if artifact['rid'].startswith('osx-') else b'\x7fELF')
                if not data.startswith(magic):
                    raise ValueError('Unexpected executable format')
                binaries.append(binary)
            library = ('e_sqlite3.dll' if artifact['rid'].startswith('win-') else
                       'libe_sqlite3.dylib' if artifact['rid'].startswith('osx-') else 'libe_sqlite3.so')
            payload = read(library)
            unexpected = [name for name in names if '/' not in name and
                          (name.endswith(('.pdb', '.dbg', '.xml', '.deps.json', '.runtimeconfig.json')) or
                           name.endswith('.dll') and name != library)]
            if unexpected:
                raise ValueError('Unexpected managed/debug build output: ' + repr(unexpected))
            artifacts.append({'rid': artifact['rid'], 'group': artifact['group'], 'file': artifact['file'],
                              'bytes': artifact['bytes'], 'sha256': artifact['sha256'], 'binaries': binaries,
                              'native_library': {'file': library, 'bytes': len(payload), 'sha256': digest(payload)}})
    return {'status': 'pass', 'version': manifest['version'], 'manifest_sha256': digest((release / 'RELEASE.json').read_bytes()),
            'build_commit': manifest['commit'], 'modified': manifest['modified'], 'sdk': manifest['sdk'],
            'artifacts': artifacts, 'runtime_probe': smoke}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release', required=True, type=Path)
    parser.add_argument('--smoke', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    receipt = verify(args.release.resolve(), json.loads(args.smoke.read_text(encoding='utf-8')))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print('Verified Native AOT archives, symbols, licenses, checksums, repeat hashes and runtime probe: ' + str(args.output))


if __name__ == '__main__':
    main()
