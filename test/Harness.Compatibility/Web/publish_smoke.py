"""Verify the self-contained UI from an empty working directory and PATH."""
import argparse
import hashlib
from html.parser import HTMLParser
import json
import os
from pathlib import Path
from queue import Queue
import subprocess
import tempfile
from threading import Thread
from urllib.error import HTTPError
from urllib.request import build_opener, ProxyHandler, Request


class Resources(HTMLParser):
    def __init__(self):
        super().__init__()
        self.urls = []

    def handle_starttag(self, tag, attributes):
        attributes = dict(attributes)
        if tag in ("script", "img", "link"):
            url = attributes.get("src") or attributes.get("href")
            if url:
                self.urls.append(url)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    executable = args.executable.resolve()
    with tempfile.TemporaryDirectory(prefix="harness UI offline ") as directory:
        environment = dict(os.environ, HARNESS_CONFIG_DIR=str(Path(directory) / "global"), PATH="")
        environment.pop("DOTNET_ROOT", None)
        environment.pop("DOTNET_ROOT_X64", None)
        help_text = subprocess.run([str(executable), "ui", "--help"], cwd=directory, env=environment,
                                   capture_output=True, text=True, timeout=20, check=True).stdout
        assert "127.0.0.1:17477" in help_text
        process = subprocess.Popen([str(executable), "ui", "--addr", "127.0.0.1:0", "--no-open"],
                                   cwd=directory, env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True)
        try:
            lines = Queue()
            Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
            announced = lines.get(timeout=20)
            assert announced.startswith("harness ui on "), announced
            address = announced.split()[3]
            opener = build_opener(ProxyHandler({}))

            def read(path, method="GET", host=None):
                request = Request(address + path.lstrip("/"), method=method,
                                  headers={} if host is None else {"Host": host})
                with opener.open(request, timeout=15) as response:
                    return response.read(), response.headers

            html, headers = read("/")
            assert headers["Content-Security-Policy"] == "default-src 'self'; frame-ancestors 'none'"
            assert headers["X-Content-Type-Options"] == "nosniff"
            resources = Resources()
            resources.feed(html.decode("utf-8"))
            assert resources.urls
            assets = {}
            for path in set(resources.urls + ["/app.js", "/style.css", "/brand.css", "/logo.svg", "/favicon.png", "/apple-touch-icon.png"]):
                assert not path.startswith(("//", "http:", "https:", "data:")), path
                body, asset_headers = read(path)
                assert len(body) > 100, path
                assert int(asset_headers["Content-Length"]) == len(body), path
                assets[path] = hashlib.sha256(body).hexdigest()
            assert read("app.js", "HEAD")[0] == b""
            assert json.loads(read("/api/projects")[0]) == {"projects": []}
            assert json.loads(read("/api/docs")[0])["docs"][0]["Name"] == "guide"
            assert "127.0.0.1:17477" in json.loads(read("/api/docs/harness-ui")[0])["body"]
            for path, host, status in [("/", "evil.example", 403), ("/%2e%2e%2flogo.svg", None, 400)]:
                try:
                    read(path, host=host)
                    raise AssertionError("request should have been rejected: " + path)
                except HTTPError as error:
                    assert error.code == status
                    assert "error" in json.loads(error.read())
            receipt = {"result": "pass", "executable_sha256": hashlib.sha256(executable.read_bytes()).hexdigest(),
                       "default_address": "127.0.0.1:17477", "empty_path": True, "empty_working_directory": True,
                       "external_resources": [], "head": "pass", "host_guard": "pass", "traversal_guard": "pass",
                       "assets": assets}
        finally:
            process.terminate()
            try:
                process.communicate(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.communicate(timeout=10)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    main()
