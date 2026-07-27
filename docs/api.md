# HTTP API reference

All paths are relative to `http://<bind>:<listen-port>/`.

Auth: send either `X-RCON-Password: <pwd>` or `Authorization: Bearer <pwd>` on
every authenticated route. For browser `EventSource` (`/stream`), which cannot
set headers, pass `?password=<pwd>` instead.

If `--rcon-password` is empty, every authenticated route answers `503` — the
password is the only credential the agent has, so there is nothing to check
against.

Request bodies are capped at 4 KiB; anything larger is rejected with `413`.

**Empty-body POSTs on Windows.** `POST /server/start|stop|restart` take no body,
but Windows `HTTP.sys` rejects a POST that arrives with neither `Content-Length`
nor a chunked body — it answers `411 Length Required` before the request ever
reaches the agent. Send `Content-Length: 0` explicitly, or just send `{}`, which
works in every client on both platforms.

## Public

### `GET /`

Returns the dashboard HTML (single-file, vanilla JS). Disable with
`--dashboard-disabled`, which makes this route `404`.

### `GET /health`

```json
{"ok":true,"uptime_sec":123,"child_pid":4567,"child_alive":true}
```

No auth required. Suitable for Docker/Kubernetes liveness probes.

## Authenticated

### `GET /version`

```json
{"sidecar":"SboxServerConsole","version":"1.3.1","child_pid":4567,"child_alive":true}
```

The `sidecar` key is a compatibility artifact of the pre-1.0 wire format and is
kept deliberately — existing clients key off it. Do not rename it.

### `GET /status`

```json
{"child_alive":true,"child_pid":4567,"uptime_sec":900,
 "buffer_capacity":500,"listen_port":27019,"child_port":27015}
```

### `GET /history?count=N`

```json
{"entries":[{"seq":42,"at":"2026-04-26T20:14:00.123Z","stream":"stdout","line":"..."}]}
```

`count` is clamped to `--buffer-size`. Streams include `stdout`, `input`,
`system`, `agent`, `stderr`, and `chat`.

### `POST /execute[?collect=1]`

Body: `{"cmd": "say hello"}`. `cmd` <= 1024 chars, no newlines.

Without `collect`:

```json
{"ok":true}
```

With `collect=1`: returns lines that arrived in the next
`--execute-collect-ms` window (default 250 ms). `?wait_ms=<n>` overrides that
window for a single call and is clamped to 50–10000:

```json
{"ok":true,"output":[
  {"seq":99,"stream":"stdout","line":"player count: 0"}
]}
```

### `POST /chat`

Body: `{"text": "server restarting in 5 minutes"}`. `text` <= 512 chars,
no newlines.

Forwards to s&box `say` and appends a `stream:"chat"` entry on success:

```json
{"ok":true}
```

s&box's argument tokenizer truncates at the first whitespace character, so
whitespace runs are substituted with U+00B7 (middle dot) before the command is
sent. See the README's Chat Broadcast section for the detail.

### `GET /stream`

Server-Sent Events. Each event:

```
data: {"seq":100,"stream":"stdout","line":"..."}
```

A `: heartbeat` comment line is emitted every 15s. Max 16 concurrent clients.
Each client has a 256-entry bounded queue; if the producer outruns the
client, oldest entries are dropped (the seq numbers will skip).

### `GET /players`

```json
{"players":[{"steamid":"7656...","name":"alice","seen_at":"2026-04-26T20:00:00Z"}],
 "a2s_players":[{"name":"alice","score":12,"duration_sec":1430.4}]}
```

`players` is the agent's own roster, populated by either the configured
`--connect-regex` (push: every connect event updates the roster) or the
`--status-poll-sec` poller (pull: parses the configured `--status-regex` over
polled output). It is empty until one of those is configured.

`a2s_players` is the authoritative roster the child reports over A2S_PLAYER on
`--query-port`. It carries no steamids — that is what the A2S protocol returns.

### Bans

```
GET    /bans                       list all
POST   /bans       {steamid,reason} add a ban (auto-kicks if currently online)
DELETE /bans/<steamid>             remove a ban
```

Persisted to `--banlist` if set; otherwise in-memory only.

### Allows (allowlist / whitelist)

```
GET    /allows                     {"enforced":bool,"allow":[...]}
POST   /allows     {steamid,note}  add an entry
DELETE /allows/<steamid>           remove an entry
```

Enforcement is implicit: while the list is **non-empty** (`enforced: true`),
any connecting steamid not on it is auto-kicked through the same connect-line
hook the banlist uses. An empty list means an open server. Bans apply either
way. Persisted to `--allowlist` if set.

### Scheduler

```
GET    /scheduler                                list all jobs
POST   /scheduler  {id,schedule,command}         create
DELETE /scheduler/<id>                           remove
POST   /scheduler/<id>/enable                    enable
POST   /scheduler/<id>/disable                   disable
```

`schedule` accepts:

- `@every 30s` / `@every 5m` / `@every 12h`
- 5-field cron: `0 */4 * * *`

Jobs persist to `--scheduler` if set; otherwise they're in-memory only and
disappear on restart.

### Lifecycle

```
POST /server/start      start the child if it is stopped
POST /server/stop       stop the child if it is running
POST /server/restart    stop then start
```

All three return `{"ok":true}`. `start` on an already-running child and `stop`
on an already-stopped child both return `409`. `restart` bypasses the
auto-restart attempt cap, so an operator-driven restart is never refused
because a crash loop burned the window. See the empty-body note at the top of
this page.

### Logs

```
GET /logs                     list files under --logs-dir
GET /logs/<name>?tail=N       read the last N lines of one file
```

`GET /logs` returns JSON:

```json
{"root":"/var/log/sboxconsole","files":[
  {"name":"server.log","size":81234,"modified_at":"2026-04-28T15:30:00.000Z"}
]}
```

`GET /logs/<name>` returns `text/plain`, not JSON. `tail` defaults to 500 and
is clamped to 1–10000. Both routes `404` when `--logs-dir` is not configured.
Name resolution is confined to `--logs-dir`; traversal outside it `404`s.

## Status codes

| Code | Meaning |
|------|---------|
| 200 | OK |
| 400 | Bad request (invalid JSON, missing field, malformed regex) |
| 401 | Bad/missing password |
| 404 | Path or resource not found; dashboard or logs browser disabled |
| 405 | Wrong HTTP method |
| 409 | Lifecycle no-op (already running / already stopped) |
| 411 | Windows only, emitted by HTTP.sys: empty-body POST with no `Content-Length` |
| 413 | Body > 4 KiB |
| 429 | Stream client cap reached |
| 503 | Child not running, or rcon-password not configured |
