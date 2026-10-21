"""Validate published adoption/concern tools on isolated synthetic repositories."""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from compatibility import Peer


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    args = parser.parse_args()
    executable = args.executable.resolve()
    artifacts = args.artifacts.resolve()
    root = artifacts / "evidence-publish-smoke" / uuid.uuid4().hex
    root.mkdir(parents=True)
    env = dict(os.environ, HARNESS_CONFIG_DIR=str(root / "global"),
               DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(artifacts / "evidence-bundle"),
               GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(root / "absent.gitconfig"))
    for key in ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]:
        env.pop(key, None)
    provider = Path(__file__).resolve().parents[1] / "fake_provider.py"
    repos = [root / name for name in ["alpha", "beta", "gamma"]]

    def run(command):
        result = subprocess.run(command, env=env, capture_output=True, text=True, encoding="utf-8", timeout=45)
        assert result.returncode == 0, (command, result.stdout, result.stderr)
        return result.stdout

    def call(peer, name, arguments):
        result = peer.call(name, arguments)["result"]
        assert not result.get("isError"), result
        assert json.loads(result["content"][0]["text"]) == result["structuredContent"]
        return result["structuredContent"]

    for repo in repos:
        repo.mkdir()
        state = repo / ".harness"
        state.mkdir()
        configuration = ('[providers.fixture]\ntype = "harness"\ncommand = ' + json.dumps(sys.executable) +
                         '\nargs = ' + json.dumps([str(provider), "--log", str(state / "provider.jsonl")]) +
                         '\ndir = "project"\n[providers.heavy]\ntype = "harness"\ncontext = false\ncommand = "missing-evidence-heavy-fixture"\n')
        (state / "config.toml").write_text(configuration, encoding="utf-8")
        (repo / ".gitignore").write_text(".harness/\n", encoding="utf-8")
        text = "namespace Example.Core;\npublic class Widget {}\n" if repo != repos[1] else "using Example.Core;\nclass App {}\n"
        (repo / "Widget.cs").write_text(text, encoding="utf-8")
        if repo == repos[0]:
            (repo / "Core.csproj").write_text("<Project><PropertyGroup><PackageId>Example.Core</PackageId><Version>2.0</Version></PropertyGroup></Project>", encoding="utf-8")
        if repo == repos[1]:
            (repo / "App.csproj").write_text('<Project><ItemGroup>\n<PackageReference Include="Example.Core" Version="1.0" />\n</ItemGroup></Project>', encoding="utf-8")
        for command in [["init", "-b", "main"], ["add", "."], ["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "synthetic evidence"]]:
            run(["git", "-C", str(repo), *command])
        run([str(executable), "init", "--project", str(repo)])
    run([str(executable), "workspace", "create", "one", "--discover", str(root)])
    with closing(Peer([str(executable), "mcp", "--project", str(repos[1])], repos[1], env)) as peer:
        peer.initialize()
        call(peer, "memory_put", {"type": "decision", "content": "Widget invoice operations remain deterministic", "source_ref": "published evidence fixture"})
    database = repos[1] / ".harness/harness.db"
    before = hashlib.sha256(database.read_bytes()).hexdigest()
    with closing(Peer([str(executable), "mcp", "--project", str(repos[0])], repos[0], env)) as peer:
        peer.initialize()
        adoption = call(peer, "library_adoption", {"library": "alpha", "depth": "deep"})
        assert adoption["packages"] == ["Example.Core"], adoption
        assert [item["project"] for item in adoption["consumers"]] == ["beta"], adoption
        assert [item["project"] for item in adoption["possible_local_implementations"]] == ["gamma"], adoption
        assert "may be unrelated" in " ".join(adoption["notes"]), adoption
        assert all(not (repo / ".harness/provider.jsonl").exists() for repo in repos)
        concern = call(peer, "similar_concerns", {"concern": "Widget example invoice"})
        beta = next(item for item in concern["members"] if item["project"] == "beta")
        assert beta["code"][0]["provider"] == "fixture", beta
        assert beta["dependencies"][0]["provider"] == "manifest", beta
        assert beta["decisions"][0]["source_ref"] == "published evidence fixture", beta
        assert "not behaviour" in " ".join(concern["notes"]), concern
    assert before == hashlib.sha256(database.read_bytes()).hexdigest()
    receipt = {"public_adoption": True, "public_concerns": True, "adoption_starts_no_provider_even_deep": True,
               "manifest_and_memory_provenance": True, "foreign_database_unchanged": True,
               "heuristics_labelled": True, "text_structured_agreement": True}
    (artifacts / "evidence-published-smoke.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Published adoption/concerns: provider-free adoption, manifest/code/memory attribution, heuristic labels and unchanged foreign database passed.")


if __name__ == "__main__":
    main()
