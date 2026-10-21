"""Account every frozen inventory row without claiming statement-level code coverage."""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
INVENTORY = ROOT / "doc/port/inventory"

def read(name):
    return json.loads((INVENTORY / (name + ".json")).read_text(encoding="utf-8"))

def account():
    baseline, scopes = read("baseline"), read("scope-map")
    receipts = {}
    for path in sorted(INVENTORY.glob("*validation.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        if data.get("task"):
            receipts[data["task"]] = path.relative_to(ROOT).as_posix()
    def plans(source):
        parts = source.split(":")[0].split("/")
        if parts[0] == "internal":
            return scopes["subsystems"][parts[1]]
        if parts[0] == "cmd":
            return scopes["executables"][parts[1]]
        if parts[0] in ("go.mod", "go.sum"):
            return ["PORT-003"]
        if parts[0] == "docs":
            return ["PORT-027"]
        raise ValueError("Unmapped source: " + source)
    def row(key, source, declared_plans=None):
        keys = declared_plans or plans(source)
        evidence = sorted({receipts[scopes["tasks"][key]["id"]] for key in keys if scopes["tasks"][key]["id"] in receipts})
        if not evidence:
            raise ValueError("No implementation acceptance receipt for " + source)
        return {"key": key, "source": source, "plans": keys, "implementation_receipts": evidence}
    matrix = {
        "subsystems": [row(name, "internal/" + name) for name in baseline["subsystems"]],
        "executables": [row(item["name"], item["source"]) for item in baseline["companions"]],
        "contract_sources": [row(path, path) for path in baseline["contract_source_hashes"]],
        "cli_functions": [row(item["function"], item["source"]) for item in read("cli")["functions"]],
        "http_routes": [row(item["pattern"], item["source"]) for item in read("http")["routes"]],
        "mcp_tools": [row(item["name"], item["registration"]) for item in read("mcp")["tools"]],
        "schema_types": [row(item["package"] + "." + item["name"], item["source"]) for item in read("schema-types")["types"]],
        "provider_packs": [row(item["path"], item["path"]) for item in read("providers")["packs"]],
        "migrations": [row(item["path"], item["path"]) for item in read("storage")["migrations"]],
    }
    return {"schema": 1, "oracle_commit": baseline["source_commit"], "coverage_kind": "Complete frozen-row accountability mapped to area acceptance receipts; not a statement/branch coverage assertion and not proof that every platform has run.", "totals": {key: len(value) for key, value in matrix.items()}, "matrix": matrix}

if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--output", type=Path, default=INVENTORY / "parity-accountability.json")
    p.add_argument("--check", action="store_true")
    a = p.parse_args()
    result = account()
    text = json.dumps(result, indent=2) + "\n"
    if a.check:
        if a.output.read_text(encoding="utf-8") != text:
            raise SystemExit("Stale parity accountability")
    else:
        a.output.write_text(text, encoding="utf-8", newline="\r\n")
    print(json.dumps(result["totals"]))
