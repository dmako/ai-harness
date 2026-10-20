"""Deterministic stdio MCP fixture provider. This is test tooling, never shipped."""

import argparse
import json
import os
from pathlib import Path
import sys
import threading
import time


TOOLS = [
    {"name": "harness_provider_info", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "code_symbol_search", "inputSchema": {"type": "object", "properties": {
        "query": {"type": "string"}, "path": {"type": "string"}, "limit": {"type": "integer"}}}},
    {"name": "find_symbols", "inputSchema": {"type": "object", "properties": {
        "query": {"type": "string"}}, "required": ["query"]}},
]


def serve(mode, log):
    lock = threading.Lock()

    def record(kind, value):
        with log.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps({"direction": kind, "message": value}, ensure_ascii=False) + "\n")

    def send(value):
        with lock:
            record("response", value)
            sys.stdout.buffer.write((json.dumps(value, ensure_ascii=False) + "\n").encode())
            sys.stdout.buffer.flush()

    def tool(value):
        return {"content": [{"type": "text", "text": json.dumps(value, ensure_ascii=False)}],
                "structuredContent": value}

    def handle(request):
        method = request.get("method")
        if "id" not in request:
            return
        result = {}
        if method == "initialize":
            result = {"protocolVersion": request["params"]["protocolVersion"],
                      "capabilities": {"tools": {}}, "serverInfo": {"name": "fixture-provider", "version": "1.0.0"}}
        elif method == "tools/list":
            result = {"tools": TOOLS}
        elif method == "tools/call":
            name = request["params"]["name"]
            if name == "harness_provider_info":
                result = tool({"protocol": "harness-provider/1", "name": "fixture-provider",
                               "capabilities": [{"capability": "code.symbol_search", "freshness": "live",
                                                 "cost": "low", "latency": "low", "evidence": "exact"}]})
            else:
                if mode == "disconnect":
                    os._exit(7)
                if mode == "timeout":
                    time.sleep(60)
                if mode == "error":
                    result = {"isError": True, "content": [{"type": "text", "text": "fixture provider error"}]}
                else:
                    if mode == "stderr":
                        with lock:
                            record("stderr", "fixture diagnostic on stderr")
                        print("fixture diagnostic on stderr", file=sys.stderr, flush=True)
                    result = tool({"items": [{"kind": "symbol", "name": "Widget", "qualified_name": "Example.Widget",
                                               "path": "Widget.cs", "start_line": 1, "end_line": 1,
                                               "snippet": "class Widget {}", "score": 1}]})
        elif method != "ping":
            send({"jsonrpc": "2.0", "id": request["id"],
                  "error": {"code": -32601, "message": "fixture method not found"}})
            return
        send({"jsonrpc": "2.0", "id": request["id"], "result": result})

    for line in sys.stdin.buffer:
        request = json.loads(line)
        with lock:
            record("request", request)
        threading.Thread(target=handle, args=(request,), daemon=True).start()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("normal", "error", "timeout", "stderr", "disconnect"), default="normal")
    parser.add_argument("--log", type=Path, required=True)
    arguments = parser.parse_args()
    serve(arguments.mode, arguments.log)
