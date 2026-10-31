# HTTP API reference

All paths are relative to `http://<bind>:<listen-port>/`.

Auth: send either `X-RCON-Password: <pwd>` or `Authorization: Bearer <pwd>` on
every authenticated route. A client that cannot set headers (a browser
`EventSource` on `/stream`) may pass `?password=<pwd>` instead, but the address
then carries the password into proxy logs and browser history; the bundled
dashboard reads `/stream` with `fetch` and the header.

If `--rcon-password` is empty, every authenticated route answers `503`: the
password is the only credential the agent has, so there is nothing to check
against.

**Wrong passwords.** After 10 wrong passwords from one address inside a minute,
that address gets `429` on every authenticated route (and its RCON connections
are closed) for 5 minutes. HTTP and RCON share the count. Loopback callers are
never locked out, so a local panel or reverse proxy keeps working.

Request bodies are capped at 4 KiB; anything larger is rejected with `413`.

**Empty-body POSTs on Windows.** `POST /server/start|stop|restart` take no body,
but Windows `HTTP.sys` rejects a POST that arrives with neither `Content-Length`
nor a chunked body: it answers `411 Length Required` before the request ever
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

### `GET /version`

```json
{"sidecar":"SboxServerConsole","version":"1.6.0","child_pid":4567,"child_alive":true}
```

No auth required, same as `/health`: version probes and upgrade checks run
before a client has credentials.

The `sidecar` key is a compatibility artifact of the pre-1.0 wire format and is
kept deliberately; existing clients key off it. Do not rename it.

## Authenticated

### `GET /status`

```json
{"child_alive":true,"child_pid":4567,"server_pid":4580,"child_uptime_sec":900,"child_memory_bytes":734003200,
 "child_cpu_seconds":"128.44","sidecar_uptime_sec":910,"buffer_capacity":500,
 "listen_port":27019,"child_port":27015,"query_port":27016,"execute_total":12,
 "stream_clients_active":1,
 "server":{"name":"My Server","map":"facepunch.datacore","folder":"sbox","game":"sbox",
           "players":3,"max_players":16,"bots":0,"fetched_at":"2026-04-26T20:14:00.000Z"}}
```

`server` is the last A2S_INFO snapshot, or `null` when the poller is disabled
(`--query-poll-sec 0`) or has not answered yet.

`child_pid` is the process the agent started. On Windows that is the PowerShell
wrapper that gives the server its console; `server_pid` is the s&box server
itself, and `child_memory_bytes` / `child_cpu_seconds` describe that process.
On Linux the two pids are the same. `server_pid` is `0` while the server is
stopped.

### `GET /history?count=N`

```json
{"entries":[{"seq":42,"at":"2026-04-26T20:14:00.123Z","stream":"stdout","line":"..."}]}
```

`count` is clamped to `--buffer-size`. Streams include `stdout`, `input`,
`system`, `agent`, `stderr`, and `chat`.

### `POST /execute[?collect=1]`

Body: `{"cmd": "status"}`. `cmd` <= 1024 chars, no line breaks or other
control characters (they would reach the server's console as keystrokes); such
a command is refused with `400`.

Without `collect`:

```json
{"ok":true}
```

With `collect=1`: returns lines that arrived in the next 250 ms.
`?wait_ms=<n>` overrides that window for a single call and is clamped to
50-10000:

```json
{"ok":true,"output":[
  {"seq":99,"stream":"stdout","line":"player count: 0"}
]}
```

### `POST /chat`

Body: `{"text": "server restarting in 5 minutes"}`. `text` <= 512 chars,
no newlines or other control characters (`400`).

Sends the text with the game's `say` command and appends a `stream:"chat"`
entry on success:

```json
{"ok":true}
```

`say` belongs to the running game, not to the s&box engine, and many games have
none (the stock `facepunch.sandbox` does not). When the server answers
`Unknown Command 'say'` within a second, the route returns `422` and records
nothing in the chat stream, so a broadcast nobody received is never reported as
sent.

The engine cuts an unquoted argument at the first space, so the message is sent
as one quoted argument: `say "server restarting in 5 minutes"`. Double quotes
inside the message become single quotes. (Releases before 1.4.0 replaced spaces
with U+00B7 because quoting did not survive older s&box builds,
[Facepunch/sbox-public#2507](https://github.com/Facepunch/sbox-public/issues/2507);
on build 26.10.02 a quoted argument keeps its spaces.)

### `GET /stream`

Server-Sent Events. Each event:

```
id: 100
data: {"seq":100,"stream":"stdout","line":"..."}
```

A `: heartbeat` comment line is emitted every 15s. Max 16 concurrent clients.
Each client has a 256-entry bounded queue; if the producer outruns the
client, oldest entries are dropped (the seq numbers will skip).

Backlog on connect:

- `?history=N` sends the last N lines first.
- A reconnecting client gets exactly the lines it missed: browsers send the
  `Last-Event-ID` header by themselves; other clients can pass `?since=<seq>`.
  Lines older than the ring buffer (`--buffer-size`) are gone.

### `GET /players`

```json
{"players":[{"steamid":"7656...","name":"alice","seen_at":"2026-04-26T20:00:00Z"}],
 "a2s_players":[{"name":"alice","score":12,"duration_sec":1430.4}]}
```

`players` is the agent's own roster, populated from the child's own output by
the configured `--connect-regex` and `--disconnect-regex`. It is empty until a
connect line matches, and it empties whenever the server process exits. If A2S
reports players for three minutes while no join line has ever matched, the agent
writes one warning to the console: the connect regex is not seeing this build's
output, so the roster, bans and the allowlist cannot see joins either.

With `--max-players N`, a player who joins while N players are already in this
roster is kicked with the reason `Server is full.` (the `--kick-command` with that
reason added). A dedicated s&box server ignores `+maxplayers`. A banned joiner, or
one the allowlist refuses, gets only that kick.

`a2s_players` is the authoritative roster the child reports over A2S_PLAYER on
`--query-port`. It carries no steamids; the A2S protocol does not return them.

### Bans

```
GET    /bans                       list all
POST   /bans       {steamid,reason} add a ban (auto-kicks if currently online)
DELETE /bans/<steamid>             remove a ban
```

Persisted to `--banlist` if set; otherwise in-memory only. With the default
`--connect-regex`, `steamid` must be a 17-digit SteamID64; anything else is
answered `400`, because no join line could ever match it. A file that cannot be
read at startup is kept as `<name>.unreadable-<timestamp>` and the agent starts
with an empty list, so the next save never overwrites it.

### Allows (allowlist / whitelist)

```
GET    /allows                     {"enforced":bool,"allow":[...]}
POST   /allows     {steamid,note}  add an entry
DELETE /allows/<steamid>           remove an entry
```

Enforcement is implicit: while the list is **non-empty** (`enforced: true`),
any connecting steamid not on it is auto-kicked through the same connect-line
hook the banlist uses. An empty list means an open server. Bans apply either
way. Persisted to `--allowlist` if set. The same SteamID64 rule as `/bans`
applies.

### Scheduler

```
GET    /scheduler                                list all jobs
POST   /scheduler  {id,schedule,command}         create; returns {"ok":true,"id":"..."}
DELETE /scheduler/<id>                           remove
POST   /scheduler/<id>/enable                    enable
POST   /scheduler/<id>/disable                   disable
```

`schedule` accepts:

- `@every 30s` / `@every 5m` / `@every 12h`
- 5-field cron: `0 */4 * * *`

`id` is optional: without one the agent generates one (`job-1a2b3c4d`) and
returns it. Posting an existing id replaces that job. Schedules run in UTC.

Jobs persist to `--scheduler` if set; otherwise they're in-memory only and
disappear on restart.

### Lifecycle

```
POST /server/start      start the child if it is stopped
POST /server/stop       stop the child if it is running
POST /server/restart    stop then start
```

All three return `{"ok":true}`. `start` on an already-running child and `stop`
on an already-stopped child both return `409`. `stop` leaves the agent running
(the dashboard, API and RCON stay up) so `start` can bring the server back.
`restart` never counts against the auto-restart cap and works with
`--no-auto-restart` too, so an operator's restart is never refused because a
crash loop burned the window. See the empty-body note at the top of this page.

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
is clamped to 1-10000. Both routes `404` when `--logs-dir` is not configured.
Name resolution is confined to `--logs-dir`; traversal outside it `404`s.
Hidden files and files whose name starts with a dot are never listed or served,
on any platform: when `--logs-dir` is the server folder they include agent
config holding the RCON password.

## Status codes

| Code | Meaning |
|------|---------|
| 200 | OK |
| 400 | Bad request (invalid JSON, missing field, malformed regex, a command or chat text with a line break or control character) |
| 401 | Bad/missing password |
| 404 | Path or resource not found; dashboard or logs browser disabled |
| 405 | Wrong HTTP method |
| 409 | Lifecycle no-op (already running / already stopped) |
| 411 | Windows only, emitted by HTTP.sys: empty-body POST with no `Content-Length` |
| 413 | Body > 4 KiB |
| 422 | `/chat`: the running game has no `say` command |
| 429 | Stream client cap reached, or too many wrong passwords from this address |
| 503 | Child not running, or rcon-password not configured |
