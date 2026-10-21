"""Build the generic adapter bridge inside the existing frozen Go source module."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--go", type=Path, required=True)
    parser.add_argument("--modules", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source = args.source.resolve()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, GOTOOLCHAIN="local", GOENV="off", GOTELEMETRY="off",
                       GOCACHE=str(source.parent.parent / "go-cache"), GOMODCACHE=str(args.modules.resolve()))
    with tempfile.TemporaryDirectory(prefix="port-generic-", dir=source / "cmd") as directory:
        (Path(directory) / "main.go").write_bytes(Path(__file__).with_name("oracle.go").read_bytes())
        result = subprocess.run([str(args.go.resolve()), "build", "-mod=readonly", "-buildvcs=false", "-trimpath", "-o",
                                 str(args.output.resolve()), "./cmd/" + Path(directory).name], cwd=source,
                                env=environment, stdin=subprocess.DEVNULL, capture_output=True, timeout=180)
        if result.returncode:
            raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    print(f"Set HARNESS_GO_PROVIDER_ORACLE={args.output.resolve()}")


if __name__ == "__main__":
    main()
