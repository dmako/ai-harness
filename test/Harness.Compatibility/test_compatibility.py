"""Offline regression checks for isolation, comparison strictness and failure fixtures."""

from contextlib import closing
import copy
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

from compatibility import FIXTURES, HISTORICAL, Normalizer, Peer, Sandbox, differences, validate_recording
from oracle import build_environment


class ComparisonTests(unittest.TestCase):
    def normalizer(self):
        return Normalizer({"C:\\isolated\\run": "<SANDBOX>"},
                          {"version": "oracle-version", "commit": "oracle-commit", "date": "build-date"})

    def test_object_order_is_semantic_but_array_order_is_preserved(self):
        self.assertEqual([], differences({"x": 1, "y": 2}, {"y": 2, "x": 1}))
        self.assertTrue(differences([1, 2], [2, 1]))

    def test_missing_null_empty_and_types_remain_distinct(self):
        for left, right in (({}, {"x": None}), (None, []), (1, True), (1, "1")):
            with self.subTest(left=left, right=right):
                self.assertTrue(differences(left, right))
        self.assertEqual([], differences(1, 1.0))

    def test_ids_preserve_relationships_across_structured_and_text_content(self):
        value = {"id": "mem_0123456789abcdef", "project_id": "prj_0123456789abcdef",
                 "content": [{"text": '{"id":"mem_0123456789abcdef"}'}]}
        normalized = self.normalizer().value(value)
        self.assertNotEqual(normalized["id"], normalized["project_id"])
        self.assertEqual(normalized["id"], json.loads(normalized["content"][0]["text"])["id"])

    def test_source_content_is_not_scrubbed_as_an_identifier_or_timestamp(self):
        for value in ("mem_0123456789abcdef", "2026-01-02T03:04:05Z"):
            self.assertEqual(value, self.normalizer().value({"snippet": value})["snippet"])

    def test_only_named_paths_and_metadata_are_normalized(self):
        value = {"path": "C:/isolated/run/Widget.cs", "other": "C:/unrelated/Widget.cs",
                 "version": "oracle-version", "package_version": "oracle-version",
                 "created_at": "2026-01-02T03:04:05Z", "duration_ms": 1.5}
        actual = self.normalizer().value(value)
        self.assertEqual("<SANDBOX>/Widget.cs", actual["path"])
        self.assertEqual(value["other"], actual["other"])
        self.assertEqual("<BUILD_VERSION>", actual["version"])
        self.assertEqual("oracle-version", actual["package_version"])
        self.assertEqual("<TIME>", actual["created_at"])
        self.assertEqual("<DURATION_MS>", actual["duration_ms"])


class IsolationTests(unittest.TestCase):
    def test_compiler_home_caches_and_go_configuration_are_isolated(self):
        with tempfile.TemporaryDirectory(prefix="harness-build-test-") as directory:
            root = Path(directory)
            environment = build_environment(root)
            self.assertEqual("off", environment["GOENV"])
            for key in ("HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME",
                        "XDG_CACHE_HOME", "TMP", "TEMP", "GOCACHE", "GOMODCACHE", "GOPATH"):
                self.assertTrue(Path(environment[key]).is_relative_to(root), key)

    def test_repository_home_config_and_git_environment_are_isolated(self):
        with patch.dict(os.environ, {"HARNESS_CONFIG_DIR": "do-not-use", "GIT_DIR": "do-not-use"}):
            sandbox = Sandbox(Path(sys.executable))
        root = sandbox.root
        try:
            self.assertNotIn("GIT_DIR", sandbox.environment)
            for key in ("HARNESS_CONFIG_DIR", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
                        "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "TMP", "TEMP"):
                self.assertTrue(Path(sandbox.environment[key]).is_relative_to(root), key)
            self.assertIn("\U0001f9ea", sandbox.repository.name)
            self.assertTrue((sandbox.repository / ".git").is_dir())
        finally:
            sandbox.close()
        self.assertFalse(root.exists())


class FixtureTests(unittest.TestCase):
    def test_historical_databases_are_synthetic_complete_and_unchanged(self):
        manifest = json.loads((FIXTURES / "sqlite/manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(HISTORICAL, tuple(item["schema_version"] for item in manifest["databases"]))
        for entry in manifest["databases"]:
            path = FIXTURES / "sqlite" / entry["file"]
            self.assertEqual(entry["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())
            with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as connection:
                self.assertEqual("ok", connection.execute("PRAGMA integrity_check").fetchone()[0])
                self.assertEqual([], connection.execute("PRAGMA foreign_key_check").fetchall())
                self.assertEqual(entry["schema_version"], connection.execute(
                    "SELECT max(version) FROM schema_migrations").fetchone()[0])
                self.assertEqual("\u017elu\u0165ou\u010dk\u00fd \U0001f9ea", connection.execute(
                    "SELECT value FROM diagnostics WHERE key='fixture'").fetchone()[0])

    def recording(self):
        return json.loads((FIXTURES / "go-windows-amd64.json").read_text(encoding="utf-8"))

    def test_recorded_expected_outcomes_and_all_thirty_runtime_schemas(self):
        validate_recording(self.recording())

    def test_capture_validation_rejects_accidental_timeout_success(self):
        recording = self.recording()
        provider = next(case for case in recording["cases"]["providers"] if case["name"] == "timeout")
        provider["response"]["result"]["structuredContent"]["results"] = [{"name": "wrong"}]
        with self.assertRaisesRegex(ValueError, "Failure provider did not fail"):
            validate_recording(recording)

    def test_exit_stderr_and_payload_mutations_are_visible(self):
        original = self.recording()["cases"]
        mutations = copy.deepcopy(original)
        mutations["cli"][0]["exit_code"] = 7
        mutations["cli"][1]["stderr"] = "unexpected diagnostic"
        mutations["cli"][1]["stdout_format"] = "json"
        mutations["mcp"][4]["response"]["result"]["structuredContent"]["value"] = "wrong"
        failures = differences(original, mutations)
        self.assertEqual(4, len(failures))


class FakeProviderTests(unittest.TestCase):
    def run_mode(self, mode, action):
        with tempfile.TemporaryDirectory(prefix="harness-fake-test-") as directory:
            log = Path(directory) / "messages.jsonl"
            peer = Peer([sys.executable, str(Path(__file__).with_name("fake_provider.py")),
                         "--mode", mode, "--log", str(log)], directory, os.environ.copy())
            try:
                peer.initialize()
                action(peer)
            finally:
                peer.close()
            return peer.errors.decode("utf-8"), [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines()]

    def test_normal_and_generic_capability_tools(self):
        def action(peer):
            for tool in ("code_symbol_search", "find_symbols"):
                response = peer.call(tool, {"query": "Widget"})
                self.assertEqual("Widget", response["result"]["structuredContent"]["items"][0]["name"])
        errors, messages = self.run_mode("normal", action)
        self.assertEqual("", errors)
        self.assertTrue(any(item["message"].get("method") == "tools/call" for item in messages))

    def test_error_is_a_tool_error_and_keeps_protocol_alive(self):
        def action(peer):
            self.assertTrue(peer.call("code_symbol_search", {})["result"]["isError"])
            self.assertIn("result", peer.request("ping"))
        self.run_mode("error", action)

    def test_disconnect_is_detected_without_hanging(self):
        def action(peer):
            with self.assertRaises(EOFError):
                peer.call("code_symbol_search", {})
        self.run_mode("disconnect", action)

    def test_timeout_is_bounded_and_process_is_reaped(self):
        def action(peer):
            with self.assertRaises(TimeoutError):
                peer.call("code_symbol_search", {}, timeout=0.1)
        self.run_mode("timeout", action)

    def test_stderr_does_not_corrupt_stdout_protocol(self):
        def action(peer):
            self.assertIn("result", peer.call("code_symbol_search", {}))
        errors, messages = self.run_mode("stderr", action)
        self.assertEqual("fixture diagnostic on stderr\n", errors.replace("\r\n", "\n"))
        self.assertTrue(any(item["direction"] == "stderr" for item in messages))


if __name__ == "__main__":
    unittest.main()
