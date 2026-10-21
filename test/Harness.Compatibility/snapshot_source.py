"""Materialize and verify the immutable Go oracle source, including evaluation cases."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[2]
BASELINE = json.loads((ROOT / "doc/port/inventory/baseline.json").read_text(encoding="utf-8"))

def snapshot(repository, output):
    output = output.resolve()
    if output.exists():
        raise ValueError("Use a fresh source output directory")
    local = Path(repository)
    with tempfile.TemporaryDirectory(prefix="harness-oracle-fetch-", dir=output.parent) as temporary:
        source = local.resolve()
        if not (source / ".git").exists():
            source = Path(temporary) / "repository"
            subprocess.run(["git", "clone", "--no-checkout", repository, str(source)], check=True, timeout=300)
        prefix = ["git", "-c", "safe.directory=" + source.as_posix(), "-C", str(source)]
        archive = subprocess.check_output([*prefix, "archive", BASELINE["source_commit"]], timeout=120)
        output.mkdir()
        with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
            for entry in tar.getmembers():
                if not entry.isfile():
                    continue
                target = (output / entry.name).resolve()
                if not target.is_relative_to(output):
                    raise ValueError("Archive path escapes source directory")
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(tar.extractfile(entry).read())
        for name, expected in BASELINE["contract_source_hashes"].items():
            if hashlib.sha256((output / name).read_bytes()).hexdigest() != expected:
                raise ValueError("Frozen contract hash mismatch: " + name)
    print("Verified " + str(len(BASELINE["contract_source_hashes"])) + " contract sources at " + BASELINE["source_commit"])

if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--repository", required=True, help="Local original checkout or authorized Git clone URL")
    p.add_argument("--output", required=True, type=Path)
    a = p.parse_args()
    a.output.parent.mkdir(parents=True, exist_ok=True)
    snapshot(a.repository, a.output)
