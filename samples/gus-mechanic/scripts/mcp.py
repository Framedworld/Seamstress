"""Minimal stdio JSON-RPC client for Seamstress.Mcp, so a whole outfit can be scripted and re-run."""
import json, os, subprocess, threading, pathlib

def _root():
    p = pathlib.Path(__file__).resolve()
    for d in p.parents:
        if (d / "Seamstress.slnx").exists():
            return d
    return p.parents[2]


ROOT = _root()
DEFAULT_EXE = os.environ.get("SEAMSTRESS_MCP") or str(ROOT / "src/Seamstress.Mcp/bin/Release/net8.0/Seamstress.Mcp.exe")


class Mcp:
    def __init__(self, exe=DEFAULT_EXE, log=None):
        env = {**os.environ, "DOTNET_ROOT": os.environ.get("DOTNET_ROOT", str(pathlib.Path.home() / ".dotnet"))}
        self.log = open(log, "w", encoding="utf-8") if log else subprocess.DEVNULL
        self.p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log,
                                  env=env, text=True, encoding="utf-8", bufsize=1)
        self.id = 0
        self._rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "gus-outfit", "version": "1"}})

    def _rpc(self, method, params):
        self.id += 1
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.id, "method": method, "params": params}) + "\n")
        self.p.stdin.flush()
        line = self.p.stdout.readline()
        if not line:
            raise RuntimeError("Seamstress.Mcp exited")
        msg = json.loads(line)
        if "error" in msg:
            raise RuntimeError(msg["error"])
        return msg["result"]

    def tools(self):
        return [t["name"] for t in self._rpc("tools/list", {})["tools"]]

    def call(self, _tool, **args):
        args = {k: v for k, v in args.items() if v is not None}
        res = self._rpc("tools/call", {"name": _tool, "arguments": args})
        text = res["content"][0]["text"]
        if res.get("isError"):
            raise RuntimeError(text)
        return json.loads(text)

    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        return lambda **kw: self.call(name, **kw)

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(timeout=10)
        except Exception:
            self.p.kill()
