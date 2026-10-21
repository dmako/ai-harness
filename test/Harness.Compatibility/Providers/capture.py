"""Regenerate the provider fixture inside the already frozen oracle module."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--go", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source = args.source.resolve()
    environment = dict(os.environ, GOTOOLCHAIN="local")
    environment["GOCACHE"] = str(source.parent.parent / "go-cache")
    with tempfile.TemporaryDirectory(prefix="port-provider-", dir=source / "cmd") as directory:
        (Path(directory) / "main.go").write_bytes(Path(__file__).with_name("main.go").read_bytes())
        result = subprocess.run([str(args.go.resolve()), "run", "./cmd/" + Path(directory).name], cwd=source,
                                env=environment, stdin=subprocess.DEVNULL, capture_output=True, timeout=120)
        if result.returncode:
            raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(result.stdout.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n"))
    print(f"Captured provider fixture: {args.output}")


if __name__ == "__main__":
    main()
