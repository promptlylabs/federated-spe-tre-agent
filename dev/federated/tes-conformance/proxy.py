"""Test harness for GA4GH's TES compliance suite against the dev stack: adds the admin login the suite cannot send and, with
MODE=federated, replaces each submitted task's body with a valid federated study (keeping the suite's name, description
and its own tags), each with its own fed.study_id, as DAAMS would. Records every study it creates, for clean-up."""
import hashlib, json, os, sys, threading, time, urllib.parse, urllib.request, uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TARGET = os.environ.get("TARGET", "http://host.docker.internal:5034")
KEYCLOAK = os.environ.get("KEYCLOAK", "http://host.docker.internal:8180/realms/Dare-Control")
MODE = os.environ.get("MODE", "stock")
CREATED = os.environ.get("CREATED", "/out/created.txt")
CODE = "def site_analysis(df):\n    return {'n': len(df)}\n\n\ndef aggregate(results):\n    return {'n': sum(r['n'] for r in results)}\n"
_token = {"value": None, "at": 0}
_lock = threading.Lock()

def token():
    with _lock:
        if _token["value"] and time.time() - _token["at"] < 60:
            return _token["value"]
        data = urllib.parse.urlencode({"grant_type": "password", "client_id": "Dare-Control-API", "client_secret": "dev-dare-control-api-secret",
                                       "username": "control-admin", "password": "ControlAdmin1!"}).encode()
        with urllib.request.urlopen(KEYCLOAK + "/protocol/openid-connect/token", data) as r:
            _token.update(value=json.load(r)["access_token"], at=time.time())
        return _token["value"]

def federated(suite):
    tags = {k: v for k, v in (suite.get("tags") or {}).items() if not k.lower().startswith("fed.")}
    tags.update({"fed.sites": "my-tre|other-tre", "fed.study_id": f"compliance-{uuid.uuid4().hex[:12]}",
                 "fed.code_sha256": hashlib.sha256(CODE.encode()).hexdigest()})
    task = {"executors": [{"image": "python-runner:1", "command": ["run-analysis", "/code/analysis.py", "/data/cohort_2024", "/outputs/result.json"]}],
            "inputs": [{"path": "/code/analysis.py", "type": "FILE", "content": CODE},
                       {"url": "dataset://cohort_2024", "path": "/data/cohort_2024", "type": "DIRECTORY"}],
            "outputs": [{"path": "/outputs/result.json", "url": "result://", "type": "FILE"}], "tags": tags}
    for key in ("name", "description"):
        if suite.get(key) is not None:
            task[key] = suite[key]
    return task

class Handler(BaseHTTPRequestHandler):
    def _forward(self, method):
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0)) if method == "POST" else None
        if method == "POST" and MODE == "federated" and self.path.rstrip("/").endswith("/v1/tasks"):
            body = json.dumps(federated(json.loads(body or b"{}"))).encode()
        req = urllib.request.Request(TARGET + self.path, data=body, method=method,
                                     headers={"Authorization": "Bearer " + token(), "Content-Type": "application/json", "Accept": "application/json"})
        try:
            with urllib.request.urlopen(req) as r:
                status, data = r.status, r.read()
        except urllib.error.HTTPError as e:
            status, data = e.code, e.read()
        if method == "POST" and status == 200 and self.path.rstrip("/").endswith("/v1/tasks"):
            with open(CREATED, "a") as f:
                f.write(json.loads(data)["id"] + "\n")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def do_GET(self): self._forward("GET")
    def do_POST(self): self._forward("POST")
    def log_message(self, *args): pass

ThreadingHTTPServer(("127.0.0.1", 8000), Handler).serve_forever()
