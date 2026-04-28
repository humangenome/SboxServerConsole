# S&box Server Console

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform: Windows + Linux](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-blue.svg)](#installation)
[![Source RCON](https://img.shields.io/badge/Source_RCON-Built--in-green.svg)](https://developer.valvesoftware.com/wiki/Source_RCON_Protocol)
[![s&box](https://img.shields.io/badge/s%26box-Dedicated_Server-darkred.svg)](https://steamdb.info/app/1892930/)

Everything your s&box dedicated server is missing. Source RCON, a live web console, A2S player tracking, banlist enforcement, scheduled commands, lifecycle control, auto-restart on crash, audit log, and Discord notifications. Runs on Windows and Linux. No s&box code changes required.

> **Recommended Hosting:** Get s&box server hosting with S&box Server Console pre-installed at [SurvivalServers.com](https://www.survivalservers.com/services/game_servers/sbox/?utm_source=github&utm_medium=readme&utm_campaign=sbox_server_console)

_S&box Server Console is a community project and is not affiliated with or endorsed by Facepunch Studios or the s&box developers._

---

## Table of Contents

- [Features](#features)
- [Required Ports](#required-ports)
- [Installation](#installation)
- [Trust & First-Run Warning](#trust--first-run-warning)
- [Using S&box Server Console](#using-sbox-server-console)
- [Integrations](#integrations)
- [Build](#build)
- [License](#license)

---

## Features

### Source RCON Server
s&box ships no RCON. S&box Server Console adds one, exposing a TCP listener that speaks the [Valve Source RCON binary protocol](https://developer.valvesoftware.com/wiki/Source_RCON_Protocol) so existing tools ([mcrcon](https://github.com/Tiiffi/mcrcon), [BattleMetrics](https://www.battlemetrics.com/), [GameDig](https://github.com/gamedig/node-gamedig), [RustyConnector](https://github.com/JustRedTTG/RustyConnector), [Pterodactyl](https://pterodactyl.io/)) connect with zero changes.

```bash
mcrcon -H your-host -P 27020 -p YourPassword "say Hello from RCON"
```

The RCON port is `+port + 5` by default and only binds when `--rcon-password` is set. Commands are forwarded to the s&box dedicated server console as-is; run `help` over RCON to enumerate the full command set your server build supports.

### Live Web Dashboard
Single embedded HTML page, no build step. Live console feed via Server-Sent Events, players list, ban management, scheduled commands, server status. Every action is gated by your RCON password and remembered for the session.

```
http://127.0.0.1:27019/
```

### HTTP API
A clean REST surface over the same supervisor. Useful when you want JSON instead of binary RCON, or when you're driving the server from a script.

| Endpoint | Description |
|----------|-------------|
| `GET /health` | Liveness probe (no auth) |
| `GET /version` | Agent version + child state |
| `GET /status` | Child uptime, CPU/RAM, A2S info, port map |
| `GET /history?count=N` | Tail the in-memory ring buffer |
| `GET /stream` | Server-Sent Events live console feed |
| `POST /execute` | Send a console command (`?collect=1` to capture output) |
| `POST /chat` | Broadcast to in-game chat. See [Chat Broadcast](#chat-broadcast) below. |
| `GET /players` | A2S roster + regex-tracked player list |
| `GET/POST /bans` `DELETE /bans/<steamid>` | Banlist CRUD |
| `GET/POST /scheduler` `DELETE /scheduler/<id>` | Scheduled commands |
| `POST /server/{start,stop,restart}` | Lifecycle control |
| `GET /logs` `GET /logs/<name>?tail=N` | Read-only log file browser |

Auth is a single bearer token: `Authorization: Bearer <password>` or `X-RCON-Password: <password>`.

### Chat Broadcast

`POST /chat` forwards to the s&box `say` ConCommand. s&box's argument tokenizer truncates at the first whitespace character (Unicode category `Zs`) — including ASCII space, U+00A0 non-breaking space, and quoted strings — see [Facepunch/sbox-public#2507](https://github.com/Facepunch/sbox-public/issues/2507). To deliver multi-word messages intact, S&box Server Console substitutes ASCII spaces with U+00B7 (middle dot, category `Po`), which the tokenizer treats as a single token and the chat HUD renders as a visible separator: `hello world` arrives as `hello·world`. Single-word messages pass through unchanged.

```bash
curl -X POST -H "X-RCON-Password: $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"text":"server restarting in 5 minutes"}' \
  http://your-host:27019/chat
# in-game chat: server·restarting·in·5·minutes
```

`POST /execute` accepts any other console command s&box supports, and RCON-issued `kick` / banlist enforcement work today.

### A2S Player Tracking
The agent polls the child's `+net_query_port` using the standard A2S_INFO + A2S_PLAYER protocol and surfaces the authoritative server name, map, and player roster. Same protocol the Steam server browser, BattleMetrics, and GameDig speak. No log-line scraping.

```json
{
  "server": {
    "name": "My S&box Server",
    "map": "facepunch.flatgrass",
    "players": 3,
    "max_players": 16,
    "fetched_at": "2026-04-27T15:42:11.000Z"
  },
  "a2s_players": [
    { "name": "HumanGenome", "score": 12, "duration_sec": 1430.4 },
    { "name": "Voxel",       "score":  4, "duration_sec":  220.7 }
  ]
}
```

### Banlist with Auto-Kick
JSON-backed, persists across restarts. Every connect-line that matches your `--connect-regex` is checked against the banlist; banned steamids are auto-kicked via the configured kick command. Adding a ban for a currently-online player kicks immediately.

```json
{
  "bans": [
    { "steamid": "76561197960287930", "reason": "griefing", "added_by": "127.0.0.1", "added_at": "2026-04-27T15:30:00.000Z" }
  ]
}
```

### Scheduled Commands
`@every 30s` / `@every 5m` / `@every 12h` shorthand or full 5-field cron expressions (`0 */4 * * *`). Persisted to JSON, survives restarts.

```bash
curl -X POST http://127.0.0.1:27019/scheduler \
  -H "Authorization: Bearer YourPassword" \
  -d '{"id":"hourly-save","schedule":"0 * * * *","command":"server.save"}'
```

### Lifecycle Control + Auto-Restart on Crash
Start, stop, and restart the child server through the API. If the child exits unexpectedly, the supervisor sleeps a configurable backoff and respawns it, capped at N restarts inside a window so a hard-crashing build doesn't burn through restart slots forever. Customer-initiated restarts via `/server/restart` bypass the cap.

```
--no-auto-restart           opt out of auto-restart entirely
--restart-backoff-sec 5     wait 5 seconds before restart
--restart-max-attempts 5    cap restarts inside window
--restart-window-sec 600    window for max-attempts cap
```

`POST /server/start|stop|restart` accepts an empty body, but on Windows you must explicitly send `Content-Length: 0` (curl's `-X POST` does this implicitly; some HTTP clients drop the header on empty-body POSTs and Windows HTTP.sys rejects with `411 Length Required` before the request reaches the agent). Sending `{}` as the body works in every client.


### Discord Notifications
Lifecycle events (agent start, supervisor exit) and player events (join, leave) post as Discord embeds via webhook. Joins are color-coded blue; leaves yellow; supervisor stops red.

### Audit Log
Append-only JSONL of every `/execute`, `/chat`, RCON command, banlist mutation, scheduler fire, and lifecycle event. Rotated automatically at 10MB, keeps 10 generations.

```jsonl
{"at":"2026-04-27T15:30:00Z","event":"rcon_execute","client":"10.0.0.5:43122","cmd":"server.save","success":true}
{"at":"2026-04-27T15:30:01Z","event":"ban_add","steamid":"76561197960287930","reason":"griefing"}
{"at":"2026-04-27T15:31:00Z","event":"server_restart","client_ip":"10.0.0.5"}
```

### Process Supervision
Windows uses a Win32 Job Object so the entire child tree dies with the wrapper, and ConPTY gives the child a real Windows console so it accepts stdin commands and emits readable stdout. Linux uses pipe-redirected stdin/stdout (sbox-server is a normal .NET console app on Linux, no PTY needed) and `Process.Kill(entireProcessTree:true)` for tree shutdown. On Linux a graceful Dispose is required for tree death; deploy under systemd with `KillMode=mixed` (see [`examples/sboxserverconsole.service`](examples/sboxserverconsole.service)) if you need the cgroup to reap orphans on hard parent crashes.

---

## Required Ports

The agent and the s&box engine bind several ports. Behind a firewall, allow the inbound ones below. Defaults assume `--child-port 27015` (s&box default); override `--child-port` and the listen / RCON ports shift in lockstep.

| Port  | Proto | Required | Purpose |
|-------|-------|----------|---------|
| 27015 | UDP   | yes      | `--child-port`. s&box game traffic + A2S query (when `+net_query_port == +port`). |
| 27019 | TCP   | yes      | `--listen-port` (default `child-port + 4`). HTTP API, SSE stream, web dashboard. |
| 27020 | TCP   | optional | `--rcon-port` (default `child-port + 5`). Source RCON. Binds only when `--rcon-password` is set. |

All entries are inbound. If `+net_query_port` differs from `+port`, that UDP port also needs to be open inbound to be visible to the Steam server browser.

**Windows Firewall** (PowerShell, run as Administrator):

```powershell
New-NetFirewallRule -DisplayName "s&box game"     -Direction Inbound -Protocol UDP -LocalPort 27015 -Action Allow
New-NetFirewallRule -DisplayName "S&box Server Console HTTP" -Direction Inbound -Protocol TCP -LocalPort 27019 -Action Allow
# Only if you're using RCON:
New-NetFirewallRule -DisplayName "S&box Server Console RCON" -Direction Inbound -Protocol TCP -LocalPort 27020 -Action Allow
```

**Linux (ufw):**

```bash
sudo ufw allow 27015/udp comment 's&box game'
sudo ufw allow 27019/tcp comment 'S&box Server Console HTTP'
sudo ufw allow 27020/tcp comment 'S&box Server Console RCON'   # only if using RCON
```

**Linux (iptables):**

```bash
sudo iptables -A INPUT -p udp --dport 27015 -j ACCEPT
sudo iptables -A INPUT -p tcp --dport 27019 -j ACCEPT
sudo iptables -A INPUT -p tcp --dport 27020 -j ACCEPT      # only if using RCON
```

Multi-instance: give each s&box instance its own `--child-port` and the listen / rcon ports shift in lockstep. Keep ranges non-overlapping (e.g. `27015-27020`, `27025-27030`, `27035-27040`).

By default the HTTP and RCON listeners bind `127.0.0.1` (localhost-only). To accept connections from outside the host, pass `--bind 0.0.0.0` *and* open the firewall. Empty-string `--rcon-password` disables the RCON listener entirely (it never binds).

---

## Installation

S&box Server Console runs on Windows (x64) and Linux (x64). Pick the section for your platform.

### Common to both: install the s&box Dedicated Server

Use [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD) with app `1892930` (the anonymous-downloadable s&box server tool — see [SteamDB](https://steamdb.info/app/1892930/)):

```bash
# Windows
steamcmd +login anonymous +force_install_dir "C:\sbox-server" +app_update 1892930 validate +quit

# Linux
./steamcmd.sh +login anonymous +force_install_dir /opt/sbox-server +app_update 1892930 validate +quit
```

For the staging branch, append `-beta staging` to the `+app_update` line.

`child-args` follows Facepunch's official [dedicated-server flag syntax](https://sbox.game/dev/doc/networking/dedicated-servers): `+game <gamePackage> [mapPackage]` is a single flag taking one or two positional arguments. Optional flags include `+net_game_server_token <32-hex>` for a persistent SteamID (register at [steamcommunity.com/dev/managegameservers](https://steamcommunity.com/dev/managegameservers) using app ID `1892930`) and `+extensions "addon1;addon2"` for semicolon-separated addon loading.

`--listen-port` defaults to `+port + 4` (HTTP), `--rcon-port` defaults to `+port + 5` (Source RCON), and `--query-port` is inferred from `+net_query_port`.

---

### Windows (x64)

**Step 1.** Download `SboxServerConsole.exe` (or `SboxServerConsole-win-x64.zip` for binary + scripts + examples) from the [latest release](https://github.com/HumanGenome/SboxServerConsole/releases/latest) and drop it next to `sbox-server.exe`.

**Step 2.** Copy `examples/config.example.json` to `config.json` and edit the paths + password:

```json
{
  "exe": "C:\\sbox-server\\sbox-server.exe",
  "game-dir": "C:\\sbox-server",
  "child-args": "+game facepunch.sandbox facepunch.flatgrass +port 27015 +net_query_port 27015 +hostname \"My S&box Server\"",
  "rcon-password": "ChooseAStrongPassword",
  "bind": "127.0.0.1",
  "audit-log": "C:\\sbox-server\\sboxconsole\\audit.jsonl",
  "banlist": "C:\\sbox-server\\sboxconsole\\bans.json",
  "scheduler": "C:\\sbox-server\\sboxconsole\\schedule.json",
  "logs-dir": "C:\\sbox-server\\logs"
}
```

**Step 3.** Run it:

```powershell
.\SboxServerConsole.exe --config-file .\config.json
```

The dashboard URL and port map print on startup.

**Step 4 (optional).** Install as a Windows service:

```powershell
# Run as Administrator
.\scripts\install-service.ps1 `
  -ExePath    .\SboxServerConsole.exe `
  -ConfigPath .\config.json
Start-Service SboxServerConsole
```

The script registers the service with autostart and restart-on-failure baked in via `sc.exe`. Uninstall with `scripts\uninstall-service.ps1`.

---

### Linux (x64)

Tested on Ubuntu 22.04 LTS and Debian 12. Any glibc-based distro from the last few years should work.

**Step 1.** Download `SboxServerConsole-linux-x64.tar.gz` from the [latest release](https://github.com/HumanGenome/SboxServerConsole/releases/latest) and extract it next to `sbox-server.sh`:

```bash
cd /opt/sbox-server
curl -L -o sboxconsole.tar.gz https://github.com/HumanGenome/SboxServerConsole/releases/latest/download/SboxServerConsole-linux-x64.tar.gz
tar -xzf sboxconsole.tar.gz
chmod +x SboxServerConsole
```

**Step 2.** Copy `examples/config.linux.example.json` to `sboxconsole.json` and edit the paths + password:

```json
{
  "exe": "/opt/sbox-server/sbox-server.sh",
  "game-dir": "/opt/sbox-server",
  "child-args": "+game facepunch.sandbox facepunch.flatgrass +port 27015 +net_query_port 27015 +hostname \"My S&box Server\"",
  "rcon-password": "ChooseAStrongPassword",
  "bind": "127.0.0.1",
  "audit-log": "/var/log/sboxconsole/audit.jsonl",
  "banlist": "/var/lib/sboxconsole/banlist.json",
  "scheduler": "/var/lib/sboxconsole/scheduler.json",
  "logs-dir": "/var/log/sboxconsole"
}
```

`exe` points at Facepunch's `sbox-server.sh` launcher (which `exec`s into `dotnet sbox-server.dll`). You can also point `exe` at `dotnet` directly and put `sbox-server.dll +game ...` in `child-args` if you'd rather skip the shell wrapper.

**Step 3.** Run it:

```bash
./SboxServerConsole --config-file ./sboxconsole.json
```

**Step 4 (optional).** Install as a systemd service:

```bash
sudo install -d /var/log/sboxconsole /var/lib/sboxconsole
sudo cp examples/sboxserverconsole.service /etc/systemd/system/
# Edit User=, ExecStart=, WorkingDirectory= in the unit to match your install
sudo systemctl daemon-reload
sudo systemctl enable --now sboxserverconsole
sudo journalctl -u sboxserverconsole -f
```

The unit ships with `KillMode=mixed` so the systemd cgroup reaps any orphaned `sbox-server` children if SboxServerConsole itself is killed hard (SIGKILL). Recommended for production deployments.

---

### Configuration reference

Everything passable as a `--flag` on the command line is also a kebab-case key in the config file. CLI flags override config-file values. Run `SboxServerConsole --help` for the full list, or read [`examples/config.example.json`](examples/config.example.json) (Windows) and [`examples/config.linux.example.json`](examples/config.linux.example.json) (Linux) for fully populated examples.

---

## Trust & First-Run Warning

**The Windows release binary is not Authenticode-signed.** S&box Server Console is shipped as an unsigned single-file `SboxServerConsole.exe` on Windows. Code-signing certificates are recurring annual costs that don't fit a free open-source tool, so the binary you download has no embedded signature. This is normal for community Windows tooling but it means you'll see one or both of the following on first run:

1. **Microsoft SmartScreen**: "Windows protected your PC". Click *More info* → *Run anyway*. SmartScreen filters new unsigned binaries by reputation; once enough machines run a given build it stops warning, but every fresh release starts at zero reputation.
2. **Mark-of-the-Web zone block**: if you downloaded the zip in a browser, Windows tags the extracted files as "from the internet". Right-click `SboxServerConsole.exe` → *Properties* → check *Unblock* → *OK*. Or run from PowerShell: `Unblock-File .\SboxServerConsole.exe`.
3. **Antivirus heuristic flags**: occasionally an AV vendor flags an unsigned single-file .NET self-contained publish as suspicious. The full source builds locally with `dotnet publish` (see [Build](#build)) and reproduces byte-for-byte from a tagged release commit, so you can verify rather than trust.

The Linux release tarball ships an unsigned ELF binary. SHA-256 sums for each release artifact are visible on the GitHub Release page and reproducible from a tagged source commit via `dotnet publish -r linux-x64 --self-contained -p:PublishSingleFile=true`.

If your environment policy forbids running unsigned binaries, you can build from source on either platform — the same code, your own signing chain.

---

## Using S&box Server Console

### From the Dashboard

The embedded dashboard at `http://<bind>:<listen-port>/` covers the day-to-day:
- Live console with command input
- Player list (A2S roster)
- Ban management (add by steamid, browse, remove)
- Scheduled commands (cron + `@every`)
- Server status (uptime, CPU, RAM, A2S info)
- Lifecycle controls (start, stop, restart)

### From RCON Tools

Any Source RCON-compatible tool works against the RCON port (default `+port + 5`):

```bash
# mcrcon (single command)
mcrcon -H your-host -P 27020 -p YourPassword "server.save"

# mcrcon (interactive)
mcrcon -H your-host -P 27020 -p YourPassword -t

# rcon-cli
echo "server.save" | rcon-cli --host your-host --port 27020 --password YourPassword
```

### From a Script

```bash
# Send a command, capture output:
curl -X POST http://127.0.0.1:27019/execute?collect=1 \
  -H "Authorization: Bearer YourPassword" \
  -d '{"cmd":"server.save"}'

# Tail the live console:
curl -N http://127.0.0.1:27019/stream \
  -H "Authorization: Bearer YourPassword"
```

See [`examples/python_client.py`](examples/python_client.py) for a stdlib-only Python client and [`examples/curl.sh`](examples/curl.sh) for a full bash + jq tour.

### Exposing Beyond Localhost

The default bind is `127.0.0.1`. Only the local machine can reach the agent. To expose it on the LAN or internet, set `--bind 0.0.0.0`, open the firewall port, and put a reverse proxy (nginx, Caddy) in front for TLS. The agent does not terminate TLS itself.

---

## Integrations

- **[mcrcon](https://github.com/Tiiffi/mcrcon), [rcon-cli](https://github.com/itzg/rcon-cli), [GameDig](https://github.com/gamedig/node-gamedig):** works out of the box on the RCON port (default `+port + 5`).
- **[BattleMetrics](https://www.battlemetrics.com/):** point it at the RCON port; A2S query also auto-discovered on `+net_query_port`.
- **[Pterodactyl](https://pterodactyl.io/):** use the standard Source RCON egg variables and point them at the agent's RCON port.
- **[Discord](https://discord.com/developers/docs/resources/webhook):** set `--discord-webhook` for join/leave + lifecycle notifications.
- **Reverse proxies ([nginx](https://nginx.org/), [Caddy](https://caddyserver.com/), [Traefik](https://traefik.io/)):** sit in front of `--listen-port` for TLS termination.

---

## Build

Local (.NET 8 SDK required):

```bash
# Windows binary
dotnet publish src/SboxServerConsole.csproj -c Release -r win-x64 \
  --self-contained -p:PublishSingleFile=true -o publish/win-x64

# Linux binary
dotnet publish src/SboxServerConsole.csproj -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o publish/linux-x64
chmod +x publish/linux-x64/SboxServerConsole
```

Both publishes work cross-platform: a Linux dev box can produce the Windows binary, and vice versa. .NET 8 SDK is the only prerequisite.

Tagged releases (`vX.Y.Z`) are built by the GitHub Actions workflow at `.github/workflows/release.yml` and attached to the GitHub Release as `SboxServerConsole.exe`, `SboxServerConsole-win-x64.zip`, the bare Linux ELF, and `SboxServerConsole-linux-x64.tar.gz`.

Full HTTP request/response shapes and status codes are in [`docs/api.md`](docs/api.md).

---

## License

MIT, see [LICENSE](LICENSE).
