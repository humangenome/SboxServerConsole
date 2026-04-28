# Changelog

All notable changes to S&box Server Console are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.0] - 2026-04-28

### Added

- **Allowlist (whitelist) enforcement.** New `--allowlist <path>` flag persists a JSON list of permitted SteamID64 entries to `.allowlist.json`. When the list is **non-empty**, any connecting steamid not on the list is auto-kicked via the same connect-line regex hook used by the banlist. An empty allowlist disables enforcement (open server). The banlist still applies independently regardless of allowlist state.
- **`/allows` HTTP API.** `GET /allows` returns `{"enforced":bool,"allow":[...]}`. `POST /allows` accepts `{"steamid","note"}` and adds an entry. `DELETE /allows/{steamid}` removes one. Same `X-RCON-Password` / `Authorization: Bearer` auth as the rest of the API.
- **Startup status line.** SboxServerConsole prints `allowlist persisted to <path> (entries: N, enforced: yes|no)` so operators can confirm at a glance whether enforcement is active.

## [1.2.0] - 2026-04-28

### Added

- **Local terminal command input.** When SboxServerConsole is launched in an interactive Windows or Linux terminal, typed lines are forwarded directly to the running s&box server the same way `/execute`, RCON, and dashboard commands are.

### Changed

- **Child server output now mirrors to the wrapper terminal.** The local process window shows the child server's live stdout before the dashboard suppression filter runs, so host operators watching the Windows console or a Linux terminal see the unsuppressed server stream while the web dashboard can still suppress noisy frame/status lines. The mirror is best-effort under extreme terminal backpressure so child output draining is never blocked by a slow local console.

## [1.1.1] - 2026-04-28

### Added

- **Structured inbound chat stream support.** Child stdout lines containing `SSCHAT {"steamid":"...","name":"...","message":"..."}` are now classified as `stream:"chat"` in `/history` and `/stream` instead of raw stdout. This gives local projects or server-side game bridges a stable, user-friendly way to feed player chat into the console UI.
- **`/chat` broadcasts now echo to the chat stream.** Successful `POST /chat` calls append `Server: <text>` to the in-memory stream so dashboards can show outbound admin chat immediately.

## [1.1.0] - 2026-04-28

### Added

- **Linux (x64) support.** SboxServerConsole now builds, ships, and runs natively on Linux alongside Windows. New `LinuxServerHost` spawns the child server through `/bin/sh -c 'exec <exe> <args> 2>&1'` so stderr folds into stdout and customer `--child-args` quoting (`+hostname "My Server"`) keeps its existing semantics. New `PosixProcessGroup` uses `Process.Kill(entireProcessTree:true)` for tree shutdown on graceful Dispose.
- **Linux release artifacts.** Release pipeline now publishes both `win-x64` and `linux-x64` single-file self-contained binaries from one Ubuntu runner. New downloads on every tag: `SboxServerConsole` (bare Linux ELF), `SboxServerConsole-linux-x64.tar.gz` (binary + examples + systemd unit), `SboxServerConsole-win-x64.zip` (binary + scripts + examples), `SboxServerConsole.exe` (single Windows file).
- **systemd unit example** at [`examples/sboxserverconsole.service`](examples/sboxserverconsole.service). Ships with `KillMode=mixed` so the cgroup reaps any orphaned `sbox-server` children if SboxServerConsole itself is killed hard. Recommended for production Linux deployments.
- **Linux config example** at [`examples/config.linux.example.json`](examples/config.linux.example.json) with FHS-conformant paths (`/opt`, `/var/log`, `/var/lib`).

### Changed

- **`PseudoConsoleHost` now implements the new `IServerHost` interface** alongside `LinuxServerHost`. `ServerProcess` dispatches to the right host at runtime via `OperatingSystem.IsWindows()` instead of throwing `PlatformNotSupportedException`.
- **`csproj` no longer hardcodes `win-x64`.** The runtime identifier is supplied at publish time (`-r win-x64` or `-r linux-x64`), making cross-target builds the default rather than the exception.
- **README install section split into Windows and Linux subsections** with platform-appropriate paths, run commands, and service-installer instructions (sc.exe on Windows, systemd on Linux).

### Notes

- Linux child supervision uses pipe-redirected stdin (no PTY). The Facepunch dedicated server on Linux is a regular .NET console app that reads from `Console.ReadLine`, so plain pipes work — no node-pty or libc PTY P/Invoke needed. If a future sbox build ever refuses pipe stdin on Linux the same way the Windows build refuses non-ConPTY input, file an issue and we'll add a Linux PTY backend.
- macOS is not officially supported but the code paths compile and run there if you build from source. No release artifact is published for macOS.

## [1.0.3] - 2026-04-27

### Added

- **`?wait_ms=N` query parameter** on `/execute?collect=1` to override the default 250ms collect window. Required for verbose ConCommand output like `cvarlist` (732+ lines on a stock server) which routinely exceeds 250ms on busy hosts and was returning truncated output. Clamped to `[50, 10000]` to prevent client-driven worker stalls. Existing callers continue to use the 250ms default.

## [1.0.2] - 2026-04-27

### Fixed

- **`/stream` left clients hanging on quiet servers.** With `SendChunked = true` the HTTP response headers do not actually go on the wire until the first body byte; on a server with no recent /chat or /execute traffic, the first byte was the heartbeat after 15s, so browser EventSource and most reverse proxies gave up well before that and the dashboard sat on "Connecting…". The handler now writes a `: connected\n\n` SSE comment and the requested `?history=N` backlog snapshot before entering the live loop, so the client transitions to `onopen` immediately.
- **`?history=N` query parameter** is now honored by `/stream`, returning the last N entries from the ring buffer at connection time so dashboards do not have to follow `GET /history` with a separate `GET /stream`.

### Added

- **`--rcon-disabled` flag** to suppress the Source RCON TCP listener while leaving the HTTP API running. Useful for hosts that want to keep an in-panel console but close the public RCON port; equivalent to `RconServer.Enabled = false` and surfaces in the panel as a "Source RCON: Enabled / Disabled" toggle.

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
