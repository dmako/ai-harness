"""Build the adapter oracle from the frozen Go source without changing the neighboring checkout."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ["source", "go", "modules", "output"]:
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    source = args.source.resolve()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, GOTOOLCHAIN="local", GOENV="off", GOTELEMETRY="off",
                       GOCACHE=str(source.parent.parent / "go-cache"), GOMODCACHE=str(args.modules.resolve()))
    with tempfile.TemporaryDirectory(prefix="port-graphify-", dir=source / "cmd") as directory:
        target = Path(directory)
        for name in ["adapter.go", "alive_other.go", "alive_unix.go"]:
            (target / name).write_bytes((source / "cmd/harness-graphify" / name).read_bytes())
        (target / "main.go").write_bytes(Path(__file__).with_name("main.go").read_bytes())
        result = subprocess.run([str(args.go.resolve()), "build", "-mod=readonly", "-buildvcs=false", "-trimpath", "-o",
                                 str(args.output.resolve()), "./cmd/" + target.name], cwd=source,
                                env=environment, stdin=subprocess.DEVNULL, capture_output=True, timeout=180)
        if result.returncode:
            raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    print(f"Set HARNESS_GO_GRAPHIFY_ORACLE={args.output.resolve()}")


if __name__ == "__main__":
    main()
