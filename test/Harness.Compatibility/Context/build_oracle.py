"""Build a driver around the frozen context budgeting and rendering implementation."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ["source", "go", "modules", "output"]:
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--goos", choices=["windows", "linux", "darwin"])
    args = parser.parse_args()
    source = args.source.resolve()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, GOTOOLCHAIN="local", GOENV="off", GOTELEMETRY="off",
                       GOCACHE=str(source.parent.parent / "go-cache"), GOMODCACHE=str(args.modules.resolve()))
    if args.goos:
        environment.update(GOOS=args.goos, GOARCH="amd64", CGO_ENABLED="0")
    bridge = source / "internal/mcp/server/port_context_oracle.go"
    if bridge.exists():
        raise RuntimeError("Oracle export bridge already exists")
    bridge.write_bytes(Path(__file__).with_name("export.go").read_bytes())
    try:
        build(args, source, environment)
    finally:
        bridge.unlink()


def build(args, source, environment):
    with tempfile.TemporaryDirectory(prefix="port-context-", dir=source / "cmd") as directory:
        target = Path(directory)
        (target / "main.go").write_bytes(Path(__file__).with_name("main.go").read_bytes())
        result = subprocess.run([str(args.go.resolve()), "build", "-mod=readonly", "-buildvcs=false", "-trimpath", "-o",
                                 str(args.output.resolve()), "./cmd/" + target.name], cwd=source,
                                env=environment, stdin=subprocess.DEVNULL, capture_output=True, timeout=180)
        if result.returncode:
            raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    print(f"Set HARNESS_GO_CONTEXT_ORACLE={args.output.resolve()}")


if __name__ == "__main__":
    main()
