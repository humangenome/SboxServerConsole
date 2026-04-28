# Changelog

All notable changes to S&box Server Console are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.1] - 2026-04-27

### Fixed

- **Banlist + scheduler persistence.** `Save()` was writing PascalCase property names while `Load()` read snake_case, so even when serialization succeeded the next startup couldn't read the file back. Compounded by an anonymous-type wrapper that the runtime refused to serialize, producing `banlist save failed: The deserialization constructor for type '<>f__AnonymousType...'` in the audit log. Both files now use named DTOs with explicit `[JsonPropertyName]` attributes for full round-trip integrity.

## [1.0.0] - 2026-04-27

Initial public release.

### Features

- **Source RCON server** speaking the [Valve binary protocol](https://developer.valvesoftware.com/wiki/Source_RCON_Protocol). Drop-in compatibility with mcrcon, rcon-cli, BattleMetrics, GameDig, RustyConnector, Pterodactyl. Default port is `+port + 5`; only binds when `--rcon-password` is set.
- **HTTP + Server-Sent Events API** for browser/script automation. Bearer-token auth (`X-RCON-Password` or `Authorization: Bearer`). Endpoints: `/health`, `/version`, `/status`, `/history`, `/stream`, `/execute`, `/chat`, `/players`, `/bans`, `/scheduler`, `/server/{start,stop,restart}`, `/logs`. Default port is `+port + 4`.
- **Embedded web dashboard** — single-page HTML, no build step. Live console feed, player roster, banlist editor, scheduled commands, lifecycle controls.
- **A2S player tracking** via the standard A2S_INFO + A2S_PLAYER protocol on `+net_query_port`. Authoritative server name, map, and player roster — same protocol the Steam server browser uses.
- **Chat broadcast** via `POST /chat`. Forwards to the s&box `say` ConCommand with U+00B7 (middle-dot) substituted for ASCII spaces, working around the engine's argument-tokenizer truncation at whitespace ([Facepunch/sbox-public#2507](https://github.com/Facepunch/sbox-public/issues/2507)). Multi-word messages render as `hello·world` in chat.
- **Banlist with auto-kick** — JSON-backed, persists across restarts. Connect-line regex matches every joining player against the banlist and kicks immediately. Adding a ban for an already-connected player kicks them on next match.
- **Scheduled commands** — `@every 30s` / `@every 5m` / `@every 12h` shorthand or full 5-field cron expressions, backed by [Cronos](https://github.com/HangfireIO/Cronos). Persisted to JSON.
- **Auto-restart on crash** with backoff and capped attempts inside a sliding window. Customer-initiated `/server/restart` calls bypass the cap.
- **Lifecycle control** — `POST /server/{start,stop,restart}`. ServerProcess is restart-capable; the child gets replaced across crash-restart cycles while collaborators keep their references.
- **Discord webhook notifications** — lifecycle (start/stop) and player join/leave events as color-coded embeds.
- **Audit log** — append-only JSONL of every command, ban mutation, scheduler fire, and lifecycle event. Auto-rotates at 10MB, keeps 10 generations.
- **Read-only logs browser** — `--logs-dir <path>` exposes file inventory at `GET /logs` and tail at `GET /logs/<name>?tail=N`. Path resolution is canonicalized + prefix-checked against the configured root.
- **Process supervision** — Win32 Job Object kills the entire child tree on wrapper exit. ConPTY gives the child a real Windows console so it accepts stdin commands and emits readable stdout.
- **Windows service installer** — `scripts/install-service.ps1` registers the agent with `sc.exe` autostart + restart-on-failure.

### Known caveats

- **Unsigned release binary.** The published `SboxServerConsole.exe` carries no Authenticode signature. SmartScreen will warn on first run; mark-of-the-web on browser-downloaded zips may need `Unblock-File`. Build from source if your environment forbids unsigned binaries. See [Trust & First-Run Warning](README.md#trust--first-run-warning).
- **Windows-only release builds.** The published binary targets `win-x64` self-contained. The .NET 8 source compiles cleanly under Linux but the Job Object + ConPTY supervision code paths are Windows-specific.
- **`+port` and Steam Datagram Relay.** s&box clients connect via Steam Datagram Relay using SteamID, not direct UDP. `+port` still binds for A2S query traffic and the legacy Source-style listener, but most player connections come over the relay. Forward `+port` and `+net_query_port` UDP for server-browser visibility regardless.
