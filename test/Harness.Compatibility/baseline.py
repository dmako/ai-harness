"""Capture and verify the pinned Go source contract without running or changing it.

This is a source inventory, not the differential execution harness (PORT-002).
Only the Python standard library and Git are needed for this documentation tool.
"""

import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import tarfile


COMMIT = "9cb401dd2de4f85dd089fea86a0b139d81b178c6"
ROOT = Path(__file__).resolve().parents[2]
DESTINATION = ROOT / "doc" / "port" / "inventory"


def git(source, *arguments):
    return subprocess.run(
        ["git", "-c", f"safe.directory={source.as_posix()}", "-C", str(source), *arguments],
        check=True, capture_output=True,
    ).stdout


def block(text, opening):
    """Read balanced Go braces, respecting comments and quoted/raw strings."""
    depth = 0
    index = opening
    quote = None
    while index < len(text):
        char = text[index]
        if quote:
            if char == "\\" and quote != "`":
                index += 2
                continue
            if char == quote:
                quote = None
        elif text.startswith("//", index):
            end = text.find("\n", index)
            index = len(text) if end < 0 else end
            continue
        elif text.startswith("/*", index):
            end = text.find("*/", index + 2)
            if end < 0:
                raise ValueError("Unclosed Go comment")
            index = end + 2
            continue
        elif char in ('"', "'", "`"):
            quote = char
        elif char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return text[opening:index + 1]
        index += 1
    raise ValueError("Unclosed Go block")


def location(path, text, position):
    return f"{path}:{text.count(chr(10), 0, position) + 1}"


def collect(source):
    archived = git(source, "archive", "--format=tar", COMMIT)
    with tarfile.open(fileobj=io.BytesIO(archived)) as archive:
        blobs = {entry.name: archive.extractfile(entry).read()
                 for entry in archive.getmembers() if entry.isfile()}
    text = {path: body.decode("utf-8") for path, body in blobs.items()
            if path.endswith((".go", ".sql", ".toml", ".md", ".yaml", ".sh"))}
    production = {path: body for path, body in text.items()
                  if path.endswith(".go") and not path.endswith("_test.go")
                  and "/testdata/" not in path}
    server = {path: body for path, body in production.items()
              if path.startswith("internal/mcp/server/")}
    types = []
    functions = {}
    for path, body in production.items():
        for match in re.finditer(r"^type (\w+) struct\s*\{", body, re.M):
            declaration = "type " + match[1] + " struct " + block(body, match.end() - 1)
            fields = []
            for field in re.finditer(r'^\s*(\w+)\s+([^\n`]+)`([^`]*json:"([^\"]+)"[^`]*)`', declaration, re.M):
                name, *options = field[4].split(",")
                fields.append({"name": name, "go_type": field[2].strip(),
                               "json_options": options, "tags": field[3]})
            types.append({"package": str(Path(path).parent).replace("\\", "/"),
                          "name": match[1], "source": location(path, body, match.start()),
                          "fields": fields, "declaration": declaration})
        for match in re.finditer(r"^func (?:\([^\n]+?\) )?(\w+)(?:\[[^\n]*?\])?\(", body, re.M):
            # All named functions in this baseline start their body on the signature line.
            end = body.find("\n", match.start())
            signature = body[match.start():end]
            opening = body.find("{", match.end(), end)
            if opening >= 0:
                # Anonymous struct parameters may precede the function body's brace.
                if "struct{}" in signature:
                    opening = body.rfind("{", match.end(), end)
                functions[(str(Path(path).parent).replace("\\", "/"), match[1])] = {
                    "source": location(path, body, match.start()), "signature": signature,
                    "declaration": body[match.start():opening] + block(body, opening)}

    tools = []
    for path, body in server.items():
        for match in re.finditer(r'addTool\(s, &mcp.Tool\s*\{', body):
            declaration = block(body, match.end() - 1)
            name = re.search(r'Name:\s*"([^\"]+)"', declaration)
            tail = body[match.end() - 1 + len(declaration):]
            handler = re.match(r",\s*s\.(\w+)(?:\([^\n]*?\))?\)", tail)
            if name is None or handler is None:
                raise ValueError(f"Unrecognized tool registration at {path}")
            function = functions[("internal/mcp/server", handler[1])]
            signature = function["signature"]
            shape = re.search(r",\s*(?:\w+\s+)?(\w+|struct\{\})\)\s*\(\*mcp.CallToolResult,\s*([\w.]+),\s*error\)", signature)
            generic = re.search(r"mcp.ToolHandlerFor\[(\w+),\s*(\w+)\]", signature)
            if shape is None and generic is None:
                raise ValueError(f"Unrecognized tool signature: {signature}")
            tools.append({"name": name[1], "handler": handler[1],
                          "registration": location(path, body, match.start()),
                          "input_type": shape[1] if shape else generic[1],
                          "output_type": shape[2] if shape else generic[2],
                          "schema_override": "taskCompleteSchema" if name[1] == "task_complete" else None})

    routes = []
    for path, body in production.items():
        if not path.startswith(("internal/gui/", "cmd/harness-collect/", "cmd/harness-share/")):
            continue
        for match in re.finditer(r'\b(?:mux|m)\.Handle(?:Func)?\("([^\"]+)"\s*,', body):
            end = body.find("\n", match.end())
            routes.append({"pattern": match[1], "handler_expression": body[match.end():end].strip(),
                           "source": location(path, body, match.start())})

    cli = []
    for (package, name), function in functions.items():
        if package == "internal/cli" and (
            "*flag.FlagSet" in function["signature"]
            or name.endswith("Command") or name in ("commands", "dispatch", "Run", "isHelp", "parseInterspersed")
        ):
            flags = []
            for match in re.finditer(r'fs\.(Bool|String|Int|Duration|Var|BoolVar|StringVar|IntVar)\(([^\n]+)', function["declaration"]):
                flags.append({"kind": match[1], "arguments": match[2].strip()})
            cli.append({"function": name, **function, "flag_declarations": flags})
    companions = []
    for path, body in production.items():
        if re.fullmatch(r"cmd/[^/]+/main.go", path):
            companions.append({"name": path.split("/")[1], "source": path,
                               "flag_declarations": re.findall(r'fs\.(?:String|Bool|Int|Duration)\([^\n]+', body)})
    companion_parsers = []
    for path, body in production.items():
        if path.startswith("cmd/"):
            declarations = []
            for match in re.finditer(r'\b(?:fs|flag)\.(?:String|Bool|Int|Float64|Duration|Var)\([^\n]+', body):
                declarations.append({"source": location(path, body, match.start()), "declaration": match[0]})
            if declarations:
                companion_parsers.append({"path": path, "flags": declarations})

    migrations = [{"path": path, "sha256": hashlib.sha256(blobs[path]).hexdigest(),
                   "sql": body} for path, body in text.items()
                  if re.fullmatch(r"internal/storage/migrations/\d{4}_[a-z0-9_]+.sql", path)]
    subsystems = sorted({path.split("/")[1] for path in production if path.startswith("internal/")})
    packs = [{"path": path, "toml": body} for path, body in text.items()
             if path.startswith("internal/config/packs/") and path.endswith(".toml")]
    # No database/config contents or user secrets are captured from the working tree.
    changes = []
    for path in ("scripts/e2e.sh", "scripts/release.sh"):
        actual = (source / path).read_bytes()
        pinned = blobs[path]
        changes.append({"path": path, "baseline_sha256": hashlib.sha256(pinned).hexdigest(),
                        "working_sha256": hashlib.sha256(actual).hexdigest(),
                        "bytes_equal": pinned == actual,
                        "equal_after_crlf_normalization": pinned.replace(b"\r\n", b"\n") == actual.replace(b"\r\n", b"\n"),
                        "baseline_crlf_count": pinned.count(b"\r\n"),
                        "working_crlf_count": actual.count(b"\r\n")})
    status = git(source, "status", "--porcelain=v1").decode().splitlines()
    return {
        "baseline.json": {
            "format_version": 1, "source_commit": COMMIT,
            "source_tree": git(source, "rev-parse", f"{COMMIT}^{{tree}}").decode().strip(),
            "source_repository": "C:/repos/personal/ai-harness",
            "source_license": blobs["LICENSE"].decode("utf-8"),
            "authority": "Git objects at source_commit, never the working tree or installed executable",
            "capture_kind": "static source contract inventory; runtime oracle fixtures belong to PORT-002",
            "source_status_at_capture": status, "script_differences": changes,
            "subsystems": subsystems, "companions": companions,
            "migrations": [{k: v for k, v in item.items() if k != "sql"} for item in migrations],
            "schema_version": len(migrations),
            "contract_source_hashes": {path: hashlib.sha256(body).hexdigest() for path, body in sorted(blobs.items())
                                       if path in production or path.endswith(".sql") or path == "go.mod"},
        },
        "mcp.json": {"tools": sorted(tools, key=lambda item: item["name"]),
                     "schema_kind": "Exact Go schema source declarations, not SDK-generated JSON Schema; resolve root types in schema-types.json. PORT-002 captures tools/list from a binary built at the pin.",
                     "custom_schema": functions[("internal/mcp/server", "taskCompleteSchema")],
                     "argument_hints": functions[("internal/mcp/server", "argumentHint")]},
        "schema-types.json": {"types": sorted(types, key=lambda item: (item["package"], item["name"]))},
        "cli.json": {"exit_codes": {"success": 0, "operational_error": 1, "usage_error": 2},
                     "functions": sorted(cli, key=lambda item: item["function"]), "companions": companions,
                     "companion_parsers": companion_parsers},
        "http.json": {"routes": sorted(routes, key=lambda item: (item["source"].split(":")[0], item["pattern"])),
                      "share_dispatch_source": functions[("cmd/harness-share/internal/share", "Handler")]},
        "storage.json": {"migrations": sorted(migrations, key=lambda item: item["path"])},
        "providers.json": {"protocol": "harness-provider/1", "packs": sorted(packs, key=lambda item: item["path"]),
                           "protocol_source": text["internal/providers/protocol/protocol.go"],
                           "capability_source": text["internal/providers/providers.go"]},
    }


def validate(products):
    baseline = products["baseline.json"]
    assert baseline["source_commit"] == COMMIT
    tools = products["mcp.json"]["tools"]
    assert len(tools) == 30 and len({item["name"] for item in tools}) == 30
    assert baseline["source_license"].startswith("MIT License")
    types = {(item["package"], item["name"]) for item in products["schema-types.json"]["types"]}
    for tool in tools:
        for key in ("input_type", "output_type"):
            name = tool[key]
            package = "internal/mcp/server"
            if "." in name:
                namespace, name = name.split(".", 1)
                package = "internal/" + namespace
            assert name == "struct{}" or (package, name) in types, tool
    migrations = products["storage.json"]["migrations"]
    assert [int(Path(item["path"]).name[:4]) for item in migrations] == list(range(1, 20))
    assert len(products["http.json"]["routes"]) >= 60
    assert len(baseline["companions"]) == 6
    assert len(baseline["subsystems"]) >= 40
    for item in migrations:
        assert hashlib.sha256(item["sql"].encode()).hexdigest() == item["sha256"]
    print(f"Validated {len(tools)} MCP roots, {len(migrations)} migrations, "
          f"{len(products['http.json']['routes'])} HTTP registrations, "
          f"{len(baseline['subsystems'])} subsystems and 6 executables.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--source", type=Path)
    mode.add_argument("--check-source", type=Path)
    arguments = parser.parse_args()
    if arguments.source:
        products = collect(arguments.source.resolve())
        validate(products)
        DESTINATION.mkdir(parents=True, exist_ok=True)
        for name, product in products.items():
            with (DESTINATION / name).open("w", encoding="utf-8", newline="\r\n") as stream:
                stream.write(json.dumps(product, ensure_ascii=False, indent=2) + "\n")
        print("Captured pinned source inventories in doc/port/inventory.")
    else:
        products = {path.name: json.loads(path.read_text(encoding="utf-8"))
                    for path in DESTINATION.glob("*.json")}
        validate(products)
        scope = json.loads((DESTINATION / "scope-map.json").read_text(encoding="utf-8"))
        assert set(scope["subsystems"]) == set(products["baseline.json"]["subsystems"])
        assert set(scope["executables"]) == {item["name"] for item in products["baseline.json"]["companions"]}
        for category in ("subsystems", "executables"):
            for references in scope[category].values():
                assert references and all(key in scope["tasks"] for key in references)
        print("Validated complete subsystem/executable coverage and approved task references.")
        for path in (ROOT / "doc" / "port").glob("*.md"):
            content = path.read_text(encoding="utf-8")
            for target in re.findall(r"\[[^\]]+\]\(([^)]+)\)", content):
                if "://" not in target and not target.startswith("#"):
                    assert (path.parent / target.split("#")[0]).exists(), (path, target)
        print("Validated local documentation links.")
        if arguments.check_source:
            expected = collect(arguments.check_source.resolve())
            for name, product in expected.items():
                assert products[name] == product, f"Pinned inventory mismatch: {name}"
            print("All generated inventories match the pinned Git source objects and captured script differences.")


if __name__ == "__main__":
    main()
