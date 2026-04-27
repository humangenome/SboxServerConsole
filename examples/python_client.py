"""Minimal Python client for SboxServerConsole. Uses only stdlib."""

import json
import urllib.request
from urllib.parse import quote


class SboxConsole:
    def __init__(self, base: str, password: str, timeout: float = 10.0):
        self.base = base.rstrip("/")
        self.password = password
        self.timeout = timeout

    def _req(self, method: str, path: str, body=None):
        data = json.dumps(body).encode("utf-8") if body is not None else None
        req = urllib.request.Request(
            self.base + path,
            data=data,
            method=method,
            headers={
                "X-RCON-Password": self.password,
                "Content-Type": "application/json" if data else "",
            },
        )
        with urllib.request.urlopen(req, timeout=self.timeout) as r:
            return json.loads(r.read().decode("utf-8"))

    def health(self):
        return self._req("GET", "/health")

    def status(self):
        return self._req("GET", "/status")

    def history(self, count=100):
        return self._req("GET", f"/history?count={count}")

    def execute(self, cmd: str, collect: bool = False):
        suffix = "?collect=1" if collect else ""
        return self._req("POST", f"/execute{suffix}", {"cmd": cmd})

    def players(self):
        return self._req("GET", "/players")

    def list_bans(self):
        return self._req("GET", "/bans")

    def ban(self, steamid: str, reason: str = ""):
        return self._req("POST", "/bans", {"steamid": steamid, "reason": reason})

    def unban(self, steamid: str):
        return self._req("DELETE", f"/bans/{quote(steamid)}")

    def scheduler_list(self):
        return self._req("GET", "/scheduler")

    def scheduler_add(self, job_id: str, schedule: str, command: str):
        return self._req("POST", "/scheduler",
                         {"id": job_id, "schedule": schedule, "command": command})


if __name__ == "__main__":
    import os
    c = SboxConsole(
        base=os.environ.get("HOST", "http://127.0.0.1:27019"),
        password=os.environ["PWD"],
    )
    print("health  :", c.health())
    print("status  :", c.status())
    print("execute :", c.execute("status", collect=True))
    print("players :", c.players())
