"""Format C# sources with the pinned local tool, or check them with --check."""
import argparse
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Report formatting differences without writing files')
    args = parser.parse_args()
    names = subprocess.check_output(
        ['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard', '--', '*.cs', '*.csx'],
        cwd=ROOT,
    ).decode('utf-8').split('\0')
    files = sorted({name for name in names if name and (ROOT / name).is_file()})
    # Batches stay below Windows command-line limits and touch only C# sources;
    # ignored build outputs, binary assets, and build/config XML are excluded.
    for index in range(0, len(files), 40):
        options = ['--no-msbuild-check'] if args.check else ['--no-msbuild-check', '--no-cache']
        result = subprocess.run(
            ['dotnet', 'csharpier', 'check' if args.check else 'format',
             *files[index:index + 40], *options],
            cwd=ROOT,
        )
        if result.returncode:
            return result.returncode
    print(f'{"Checked" if args.check else "Formatted"} {len(files)} C# files.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
