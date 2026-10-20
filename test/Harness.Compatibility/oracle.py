"""Build the pinned Go oracle in an isolated directory; never build the working copy."""

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile

from baseline import COMMIT, ROOT, git


def build_environment(output):
    environment = os.environ.copy()
    environment.update({"GOWORK": "off", "GOTOOLCHAIN": "local", "GOFLAGS": "", "GOENV": "off",
                        "GOMODCACHE": str(output / "modules"), "GOCACHE": str(output / "cache"),
                        "GOPATH": str(output / "gopath"), "GOTELEMETRY": "off", "CGO_ENABLED": "0"})
    for key in ("HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "TMP", "TEMP"):
        path = output / "environment" / key.lower()
        path.mkdir(parents=True, exist_ok=True)
        environment[key] = str(path)
    return environment


def build(source, compiler, output):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    snapshot = output / "source"
    if snapshot.exists():
        raise ValueError("Use a fresh output directory so stale source cannot enter the oracle")
    snapshot.mkdir()
    with tarfile.open(fileobj=io.BytesIO(git(source.resolve(), "archive", COMMIT))) as archive:
        for entry in archive.getmembers():
            if not entry.isfile() or not (
                entry.name.startswith(("internal/", "cmd/", "docs/"))
                or entry.name in ("go.mod", "go.sum", "LICENSE")
            ):
                continue
            target = (snapshot / entry.name).resolve()
            if not target.is_relative_to(snapshot):
                raise ValueError("Archive entry escapes snapshot")
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.extractfile(entry).read())
    environment = build_environment(output)
    executable = output / ("harness.exe" if os.name == "nt" else "harness")
    command = [str(compiler.resolve()), "build", "-mod=readonly", "-buildvcs=false", "-trimpath",
               "-ldflags", f"-X harness/internal/buildinfo.Version=compat-{COMMIT[:12]} "
               f"-X harness/internal/buildinfo.Commit={COMMIT} "
               "-X harness/internal/buildinfo.Date=2026-10-20T00:00:00Z",
               "-o", str(executable), "./cmd/harness"]
    subprocess.run(command, cwd=snapshot, env=environment, check=True)
    version = json.loads(subprocess.run([str(executable), "version", "--json"],
                                       check=True, capture_output=True).stdout)
    if version["commit"] != COMMIT or version["modified"]:
        raise ValueError("Oracle identity does not match the immutable source pin")
    metadata = {"source_commit": COMMIT,
                "source_tree": git(source.resolve(), "rev-parse", f"{COMMIT}^{{tree}}").decode().strip(),
                "executable_sha256": hashlib.sha256(executable.read_bytes()).hexdigest(),
                "version": version, "build_command": command[1:],
                "compiler": subprocess.run([str(compiler), "version"], check=True, env=environment,
                                           capture_output=True, text=True).stdout.strip()}
    (output / "oracle.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(metadata, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--go", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "compatibility" / "oracle")
    arguments = parser.parse_args()
    build(arguments.source, arguments.go, arguments.output)
