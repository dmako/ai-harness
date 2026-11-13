"""Describe the Avalonia UI's WebAssembly bundle (the publish output's wwwroot) and check its size budget.

Its size is what a browser downloads the first time: each file's Brotli copy (.br) when the publish wrote one,
else the file itself; .gz copies are not counted. The budget is BUDGET_MB (TASK-0163: 25 % over the 4.81 MB the
TASK-0144 spike measured); a bundle above it fails, so growth is a decision, not an accident.
"""
import argparse
import hashlib
import json
import sys
from pathlib import Path

BUDGET_MB = 6.0


def describe(root):
    """The bundle's files, raw and Brotli bytes, and a hash of its tree; ValueError when it is not a bundle."""
    root = Path(root)
    if not (root / "index.html").is_file() or not (root / "_framework").is_dir():
        raise ValueError(str(root) + " is not a published Avalonia UI bundle (no index.html and _framework)")
    files, raw, brotli = 0, 0, 0
    tree = hashlib.sha256()
    for path in sorted(p for p in root.rglob("*") if p.is_file()):
        if path.suffix in (".br", ".gz"):
            continue
        size = path.stat().st_size
        compressed = path.with_name(path.name + ".br")
        files += 1
        raw += size
        brotli += compressed.stat().st_size if compressed.is_file() else size
        tree.update(path.relative_to(root).as_posix().encode() + b"\0" + hashlib.sha256(path.read_bytes()).digest())
    return {"files": files, "bytes": raw, "brotli_bytes": brotli, "tree_sha256": tree.hexdigest()}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("bundle", type=Path, help="the publish output's wwwroot")
    p.add_argument("--budget-mb", type=float, default=BUDGET_MB, help="the most the Brotli total may be (MB)")
    p.add_argument("--markdown", action="store_true", help="also print a Markdown line (for a CI step summary)")
    a = p.parse_args()
    try:
        d = describe(a.bundle)
    except ValueError as error:
        print(error, file=sys.stderr)
        return 2
    mb = d["brotli_bytes"] / 1e6
    line = f"Avalonia UI bundle: {d['files']} files, {d['bytes'] / 1e6:.2f} MB raw, {mb:.2f} MB Brotli (budget {a.budget_mb:.2f} MB)"
    print(line)
    print(json.dumps(d))
    if a.markdown:
        print(("**" if mb > a.budget_mb else "") + line + ("** over budget" if mb > a.budget_mb else ""))
    if mb > a.budget_mb:
        print(f"The bundle grew past its budget by {mb - a.budget_mb:.2f} MB: shrink it or raise BUDGET_MB on purpose.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
