"""Capture synthetic inputs from the pinned clone tests without editing the oracle tree."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ["source", "go", "output", "artifacts"]:
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    args.artifacts.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="clone-fixture-capture-", dir=args.artifacts.resolve()) as directory:
        root = Path(directory)
        clone_dir = root / "internal/clones"
        clone_dir.mkdir(parents=True)
        codefiles = root / "internal/codefiles"
        codefiles.mkdir()
        for src_dir, target in [(args.source / "internal/clones", clone_dir), (args.source / "internal/codefiles", codefiles)]:
            for source in src_dir.glob("*.go"):
                text = source.read_text(encoding="utf-8")
                if source.name == "clones_test.go":
                    needle = "func write(t *testing.T, root string, files map[string]string) []string {"
                    assert needle in text
                    text = text.replace(needle, needle + "\n captureFixture(t.Name(), files)", 1)
                (target / source.name).write_text(text, encoding="utf-8")
        (root / "go.mod").write_text("module harness\n\ngo 1.27.1\n", encoding="utf-8")
        (clone_dir / "capture_test.go").write_text('''package clones
import ("encoding/json"; "os"; "sync")
var captureMu sync.Mutex
func captureFixture(name string, files map[string]string) {
 captureMu.Lock(); defer captureMu.Unlock()
 f,err:=os.OpenFile(os.Getenv("HARNESS_CAPTURE_OUTPUT"),os.O_CREATE|os.O_APPEND|os.O_WRONLY,0600)
 if err!=nil {panic(err)};defer f.Close()
 if err=json.NewEncoder(f).Encode(map[string]any{"name":name,"files":files});err!=nil{panic(err)}
}
''', encoding="utf-8")
        captured = root / "inputs.jsonl"
        environment = dict(os.environ, GOTOOLCHAIN="local", GOENV="off", GOTELEMETRY="off",
                           GOCACHE=str(args.artifacts.resolve() / "clone-capture-go-cache"),
                           HARNESS_CAPTURE_OUTPUT=str(captured))
        # The source's TestSequences assumes map iteration puts cli before out.
        # Retry that original order-sensitive assertion; never alter its algorithm.
        for _ in range(5):
            captured.unlink(missing_ok=True)
            result = subprocess.run([str(args.go.resolve()), "test", "-count=1", "./internal/clones"], cwd=root,
                                    env=environment, capture_output=True, timeout=180)
            if result.returncode == 0:
                break
            if b"sequences_test.go:87: statements:" not in result.stdout:
                break
        if result.returncode:
            raise RuntimeError(result.stdout.decode("utf-8") + result.stderr.decode("utf-8"))
        cases = [json.loads(line) for line in captured.read_text(encoding="utf-8").splitlines()]
        counts = {}
        for case in cases:
            name = case["name"]
            counts[name] = counts.get(name, 0) + 1
            case["name"] = name + ":" + str(counts[name])
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps({"oracle_commit": "9cb401dd2de4f85dd089fea86a0b139d81b178c6",
                                          "cases": cases}, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"Captured {len(cases)} original synthetic clone inputs; original suites passed.")


if __name__ == "__main__":
    main()
