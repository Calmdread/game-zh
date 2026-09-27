"""Local protocol fixture for manual UI tests. This is not a translation provider."""

import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        try:
            request = json.loads(body)
            assert request["model"]
            assert request["messages"]
        except (ValueError, KeyError, AssertionError):
            self.send_error(400)
            return
        if "error401" in self.path:
            self.send_error(401)
            return
        payload = json.dumps({"choices": [{"message": {"content": "你好，世界"}}]}, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, format, *args):
        print(format % args, flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=18765)
    args = parser.parse_args()
    with ThreadingHTTPServer(("127.0.0.1", args.port), Handler) as server:
        print(f"mock API listening on {args.port}", flush=True)
        server.serve_forever()
