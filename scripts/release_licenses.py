"""Collect the licenses of packages actually present in a published deps graph."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def inventory(deps, packages, destination):
    graph = json.loads(deps.read_text(encoding="utf-8"))
    destination.mkdir(parents=True, exist_ok=True)
    entries = []
    for identity, library in sorted(graph["libraries"].items()):
        if library["type"] not in ("package", "runtimepack"):
            continue
        name, version = identity.removeprefix("runtimepack.").rsplit("/", 1)
        directory = packages / name.lower() / version.lower()
        specifications = list(directory.glob("*.nuspec"))
        if len(specifications) != 1:
            raise ValueError("Missing package license metadata: " + identity)
        xml = ET.parse(specifications[0])
        license = xml.find(".//{*}license")
        url = xml.find(".//{*}licenseUrl")
        if license is None and url is None:
            raise ValueError("No license metadata for " + identity)
        entry = {"package": name, "version": version, "kind": library["type"], "license_type": license.get("type") if license is not None else "url", "license": license.text if license is not None else url.text, "nuget_sha512": None, "notices": []}
        hashes = list(directory.glob("*.nupkg.sha512"))
        if hashes:
            entry["nuget_sha512"] = hashes[0].read_text(encoding="utf-8").strip()
        candidates = {p for p in directory.rglob("*") if p.is_file() and p.name.upper() in ("LICENSE", "LICENSE.TXT", "LICENSE.MD", "THIRD-PARTY-NOTICES.TXT", "THIRD_PARTY_NOTICES.TXT", "NOTICE", "NOTICE.TXT")}
        if license is not None and license.get("type") == "file":
            candidates.add(directory / (license.text or ""))
        for source in sorted(candidates):
            if not source.resolve().is_relative_to(directory.resolve()) or not source.is_file():
                raise ValueError("Invalid package license file")
            relative = Path(name + "-" + version) / source.relative_to(directory)
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(source.read_bytes())
            entry["notices"].append({"file": "LICENSES/" + relative.as_posix(), "sha256": hashlib.sha256(source.read_bytes()).hexdigest()})
        entries.append(entry)
    return entries
