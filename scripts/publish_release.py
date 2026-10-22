"""Publish locked self-contained binaries and deterministic archives; no language runtime ships separately.

Python/.NET SDK are build tools only. Native release smoke tests deliberately hide
them from the application. No remote release or Git push is performed.
"""
import argparse
from datetime import datetime, timezone
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import uuid
import zipfile

from release_licenses import inventory

ROOT = Path(__file__).resolve().parents[1]
RIDS = {"win-x64": ("windows", "amd64"), "linux-x64": ("linux", "amd64"), "linux-arm64": ("linux", "arm64"), "osx-x64": ("darwin", "amd64"), "osx-arm64": ("darwin", "arm64")}
PRODUCTS = {"Cli": "harness", "Graphify": "harness-graphify", "Collect": "harness-collect", "Share": "harness-share"}


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT).decode("utf-8").strip()


def sha(path):
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def execute(command, log, environment):
    with log.open("wb") as stream:
        result = subprocess.run(command, cwd=ROOT, env=environment, stdout=stream, stderr=subprocess.STDOUT, timeout=900)
    if result.returncode:
        print(log.read_text(encoding="utf-8", errors="replace")[-14000:])
        raise RuntimeError("Build failed; see " + str(log))


def archive(directory, output, epoch, windows):
    files = sorted(p for p in directory.rglob("*") if p.is_file())
    if windows:
        stamp = datetime.fromtimestamp(max(epoch, 315532800), timezone.utc).timetuple()[:6]
        with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as zip:
            for path in files:
                info = zipfile.ZipInfo(path.relative_to(directory).as_posix(), stamp)
                info.create_system = 3
                info.external_attr = (0o100755 if path.name in PRODUCTS.values() or path.suffix == ".exe" else 0o100644) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                zip.writestr(info, path.read_bytes())
    else:
        with output.open("wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=epoch, compresslevel=6) as compressed, tarfile.open(fileobj=compressed, mode="w") as tar:
            for path in files:
                info = tarfile.TarInfo(path.relative_to(directory).as_posix())
                info.size = path.stat().st_size
                info.mode = 0o755 if path.name in PRODUCTS.values() else 0o644
                info.mtime = epoch
                tar.addfile(info, io.BytesIO(path.read_bytes()))


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--version", required=True)
    p.add_argument("--output", type=Path)
    p.add_argument("--rids", nargs="+", choices=list(RIDS), default=list(RIDS))
    p.add_argument("--packages", type=Path)
    p.add_argument("--refresh-locks", action="store_true", help="Bootstrap changed publish lockfiles; review/commit the diff before locked CI")
    p.add_argument("--verify-repeat", action="store_true", help="Repeat each publish and require identical executable hashes")
    a = p.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+\-]{0,79}", a.version):
        p.error("Version must be a safe archive component")
    output = (a.output or ROOT / "dist" / a.version).resolve()
    if output.exists():
        p.error("Use a fresh output directory")
    output.mkdir(parents=True)
    build = ROOT / "artifacts" / ("release-" + uuid.uuid4().hex)
    build.mkdir(parents=True)
    commit = git("rev-parse", "HEAD")
    epoch = int(os.environ.get("SOURCE_DATE_EPOCH") or git("show", "-s", "--format=%ct", "HEAD"))
    stamp = datetime.fromtimestamp(epoch, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    modified = bool(git("status", "--porcelain"))
    environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", SOURCE_DATE_EPOCH=str(epoch))
    environment.setdefault("DOTNET_CLI_HOME", str(ROOT / "artifacts/package-spike/cli-home"))
    packages = (a.packages or Path(environment.get("NUGET_PACKAGES", ROOT / "artifacts/package-spike/packages"))).resolve()
    environment["NUGET_PACKAGES"] = str(packages)
    common = ["-p:PublishProfile=Release", "-p:VERSION_LABEL=" + a.version, "-p:SourceRevisionId=" + commit, "-p:BuildTimestamp=" + stamp, "-p:RepositoryModified=" + str(modified).lower(), "-p:PathMap=" + str(ROOT) + "=/_/"]
    source_files = git("ls-files", "--cached", "--others", "--exclude-standard").splitlines()
    source_files = [name for name in source_files if name.split("/")[0] in ("src", "configuration", "scripts") or name in ("global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.config", ".editorconfig", "doc/port/inventory/baseline.json")]
    source_hashes = {name: sha(ROOT / name) for name in source_files if (ROOT / name).is_file()}
    manifest = {"schema": 1, "version": a.version, "commit": commit, "modified": modified, "source_date_epoch": epoch, "sdk": subprocess.check_output(["dotnet", "--version"], cwd=ROOT, env=environment).decode().strip(), "build_directory": build.relative_to(ROOT).as_posix(), "self_contained": True, "single_file": True, "trimmed": False, "native_aot": False, "source_sha256": source_hashes, "companion_policy": "Main archives contain harness and harness-graphify. Optional collect/share developer archives are separate and never installed by harness.", "repository_license_declaration": None, "artifacts": []}
    notes = "# Harness " + a.version + "\n\nSelf-contained .NET 10 binaries. No preinstalled .NET, Node or Python is required by the core. Linux still needs supported OS native dependencies, including OpenSSL 3. Git is needed for sync/history; semantic providers remain optional external tools.\n\nMain archives: harness + harness-graphify. Developer archives: harness-collect + harness-share, not installed with the main CLI.\n\nCross-compilation does not imply native qualification. See doc/port/releases.md and the validation receipt for host/architecture execution status. Trimming and NativeAOT are disabled.\n"
    (output / "RELEASE_NOTES.md").write_text(notes, encoding="utf-8")
    upstream = json.loads((ROOT / "doc/port/inventory/baseline.json").read_text(encoding="utf-8"))["source_license"]
    for rid in a.rids:
        stage = build / rid
        binaries = {}
        for project, executable in PRODUCTS.items():
            print("Publishing " + rid + " " + executable, flush=True)
            csproj = ROOT / "src" / ("Harness." + project) / ("Harness." + project + ".csproj")
            properties = [*common, "-p:RuntimeIdentifier=" + rid]
            restore = ["dotnet", "restore", str(csproj), *properties]
            restore.append("--force-evaluate" if a.refresh_locks else "--locked-mode")
            execute([*restore, "-bl:" + str(build / (rid + "-" + project + "-restore-" + uuid.uuid4().hex + ".binlog"))], build / (rid + "-" + project + "-restore.log"), environment)
            destination = stage / project
            execute(["dotnet", "publish", str(csproj), "--no-restore", *properties, "-o", str(destination), "-bl:" + str(build / (rid + "-" + project + "-publish-" + uuid.uuid4().hex + ".binlog"))], build / (rid + "-" + project + "-publish.log"), environment)
            binary = destination / (executable + (".exe" if rid.startswith("win-") else ""))
            if not binary.is_file():
                raise ValueError("Missing single-file apphost")
            if a.verify_repeat:
                repeat = stage / (project + "-repeat")
                execute(["dotnet", "publish", str(csproj), "--no-restore", *properties, "-o", str(repeat), "-bl:" + str(build / (rid + "-" + project + "-repeat-" + uuid.uuid4().hex + ".binlog"))], build / (rid + "-" + project + "-repeat.log"), environment)
                if sha(binary) != sha(repeat / binary.name):
                    raise ValueError("Non-reproducible executable: " + rid + " " + executable)
            deps = ROOT / "src" / ("Harness." + project) / "bin/Release/net10.0" / rid / (executable + ".deps.json")
            license_stage = stage / (project + "-licenses")
            entries = inventory(deps, packages, license_stage / "LICENSES")
            binaries[project] = (binary, entries, license_stage)
        for group, projects in (("main", ("Cli", "Graphify")), ("developer", ("Collect", "Share"))):
            tree = stage / (group + "-archive")
            tree.mkdir()
            licenses = {}
            for project in projects:
                binary, entries, license_stage = binaries[project]
                shutil.copyfile(binary, tree / binary.name)
                shutil.copytree(license_stage / "LICENSES", tree / "LICENSES", dirs_exist_ok=True)
                for entry in entries:
                    licenses[entry["package"] + "/" + entry["version"]] = entry
            (tree / "LICENSE-GO.txt").write_text(upstream, encoding="utf-8")
            (tree / "NOTICE.md").write_text("Contains MIT-licensed code and assets derived from the frozen Go harness; LICENSE-GO.txt preserves its notice. Package/runtime licenses are listed in THIRD_PARTY_NOTICES.json and LICENSES. This repository has no separate top-level license declaration for new C# contributions; no additional license is inferred by the packaging script.\n", encoding="utf-8")
            (tree / "RELEASE_NOTES.md").write_text(notes, encoding="utf-8")
            write_json(tree / "THIRD_PARTY_NOTICES.json", sorted(licenses.values(), key=lambda e: e["package"]))
            write_json(tree / "BUILD.json", {k: v for k, v in manifest.items() if k not in ("artifacts", "build_directory")})
            osname, arch = RIDS[rid]
            filename = "harness-" + a.version + "-" + osname + "-" + arch + (".zip" if osname == "windows" else ".tar.gz")
            target = output / ("developer-tools" if group == "developer" else "") / filename
            target.parent.mkdir(parents=True, exist_ok=True)
            archive(tree, target, epoch, osname == "windows")
            manifest["artifacts"].append({"rid": rid, "group": group, "file": target.relative_to(output).as_posix(), "bytes": target.stat().st_size, "sha256": sha(target), "binaries": [{"file": binaries[project][0].name, "bytes": binaries[project][0].stat().st_size, "sha256": sha(binaries[project][0]), "repeat_publish_verified": a.verify_repeat} for project in projects], "licenses": [{"package": e["package"], "version": e["version"], "license_type": e["license_type"], "license": e["license"]} for e in sorted(licenses.values(), key=lambda e: e["package"]) ]})
    write_json(output / "RELEASE.json", manifest)
    sums = [sha(path) + "  " + path.relative_to(output).as_posix() for path in sorted(output.rglob("*")) if path.is_file()]
    (output / "SHA256SUMS").write_text("\n".join(sums) + "\n", encoding="utf-8")
    print("Published " + str(output), flush=True)

if __name__ == "__main__":
    main()
