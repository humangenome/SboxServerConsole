# HTTP API reference

All paths are relative to `http://<bind>:<listen-port>/`.

Auth: `X-RCON-Password: <pwd>` header on every authenticated route.
For browser EventSource (`/stream`), pass `?password=<pwd>` instead.

## Public

### `GET /`

Returns the dashboard HTML (single-file, vanilla JS). Disable with
`--dashboard-disabled`.

### `GET /health`

```json
{"ok":true,"uptime_sec":123,"child_pid":4567,"child_alive":true}
```

No auth required. Suitable for Docker/Kubernetes liveness probes.

## Authenticated

### `GET /status`

```json
{"child_alive":true,"child_pid":4567,"uptime_sec":900,
 "buffer_capacity":500,"listen_port":27019,"child_port":27015}
```

### `GET /history?count=N`

```json
{"entries":[{"seq":42,"at":"2026-04-26T20:14:00.123Z","stream":"stdout","line":"..."}]}
```

`count` is clamped to `--buffer-size`. Streams are `stdout`, `input`,
`system`.

### `POST /execute[?collect=1]`

Body: `{"cmd": "say hello"}`. `cmd` <= 1024 chars, no newlines.

Without `collect`:

```json
{"ok":true}
```

With `collect=1`: returns lines that arrived in the next
`--execute-collect-ms` window (default 250 ms):

```json
{"ok":true,"output":[
  {"seq":99,"stream":"stdout","line":"player count: 0"}
]}
```

### `GET /stream`

Server-Sent Events. Each event:

```
data: {"seq":100,"stream":"stdout","line":"..."}
```

A `: heartbeat` comment line is emitted every 15s. Max 16 concurrent clients.
Each client has a 256-entry bounded queue; if the producer outruns the
client, oldest entries are dropped (the seq numbers will skip).

### `GET /metrics`

```json
{"execute":{"total":10,"success":10,"failure":0,"auth_failure":0,"bad_request":0},
 "stream":{"clients_total":3,"clients_active":1},
 "requests":{"history":42,"status":7,"health":120,"not_found":0},
 "uptime_sec":3600}
```

### `GET /players`

```json
{"players":[{"steamid":"7656...","name":"alice","seen_at":"2026-04-26T20:00:00Z"}]}
```

Populated by either the configured `--connect-regex` (push: every connect
event updates the roster) or the `--status-poll-sec` poller (pull: parses
the configured `--status-regex` over polled output). Empty until one of
those is configured.

### Bans

```
GET    /bans                       list all
POST   /bans       {steamid,reason} add a ban (auto-kicks if currently online)
DELETE /bans/<steamid>             remove a ban
```

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

## Status codes

| Code | Meaning |
|------|---------|
| 200 | OK |
| 400 | Bad request (invalid JSON, missing field, malformed regex) |
| 401 | Bad/missing password |
| 404 | Path or resource not found |
| 405 | Wrong HTTP method |
| 413 | Body > 4 KiB |
| 429 | Stream client cap reached |
| 503 | Child not running, or rcon-password not configured |
