# Changelog

All notable changes to SboxServerConsole are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.6.0] - 2026-10-29

### Added

- **A player limit.** A dedicated s&box server ignores `+maxplayers` and advertises the game's own player count. With `max-players` set, a player who joins when that many are already on is kicked with the reason "Server is full." A rejoin is not counted twice, and a banned joiner or one the allowlist refuses gets only that kick. Off by default.

## [1.5.0] - 2026-10-24

### Changed

- **Runs on .NET 10.** Microsoft ends .NET 8 support on 2026-11-10. The release files are still self-contained, so there is nothing to install.
- **Releases wait for the Windows smoke test.** The exact `SboxServerConsole.exe` a release publishes now runs on Windows (console, job object, HTTP.sys, service install) before anything is published. The Windows zip carries only the two service scripts, no longer the maintainers' release checker.

### Fixed

- **Dashboard: a wrong password is said once.** The dashboard used to carry on after a wrong password, polling every 5 seconds, which kept tripping the agent's wrong-password lockout for that browser's address, and it kept the wrong password for the next reload. It now checks once, says "Wrong password." (or that the address is locked out) and stops. Only a password the agent accepted is kept for the tab.
- **Dashboard: the password stays out of the address.** The live console sent the password as `/stream?password=...`, where it ends up in proxy logs and browser history. It is now sent in a header; the console reconnects by itself and picks up after the last line it showed. `?password=` still works for clients that cannot set headers.
- **Dashboard: works on a phone.** On narrow screens the side panel moves under the console. Lists stop refreshing while the tab is hidden.
- **A refused command says why.** `/execute` and `/chat` answer `400` for a command with a tab or another control character; it used to come back as `503 child not running`.
- **RCON: long replies keep their characters.** Replies over 4 KB were cut at fixed byte offsets, so an accented or emoji name in a long `status` turned into replacement characters. They are now cut between characters. Two RCON clients sending at the same moment no longer receive each other's output.
- **Status no longer shows a server that stopped answering.** After two missed A2S polls, `/status` and `/players` drop the last answer instead of showing it forever. Answers split over several packets (a large player list) are now read.
- Password checks take the same time whatever the supplied password's length.

### Removed

- A roster sweep for bans and the allowlist that nothing called.

## [1.4.1] - 2026-10-21

### Changed

- **Launch arguments are passed as a list.** SboxServerConsole now splits `child-args` itself: spaces separate arguments, double quotes group words, and every other character reaches the server exactly as written. Shell syntax in `child-args` (single quotes, `$VARIABLES`, backslash escapes) is no longer interpreted, so write values the way the server should receive them; if you used single quotes on Linux, switch to double quotes. The usual `+hostname "My Server" +port 27015` works as before.
- The Windows smoke test now checks that the server receives its arguments exactly as written.

## [1.4.0] - 2026-10-12

Checked against the s&box dedicated server build of 2026-10-02 (26.10.02) on Windows and Linux.

### Fixed

- **Linux: commands, kicks and bans now reach the server.** The s&box server only reads console input when its output is a terminal, and the agent ran it on plain pipes, so on Linux every command, RCON call, scheduled job, ban kick, allowlist kick and the graceful `quit` was accepted and then silently dropped. The server now runs on a pseudo-terminal the agent holds. If no pseudo-terminal can be opened, the agent says so at startup.
- **`POST /server/stop` no longer shuts the agent down.** Stopping the server ended the whole agent, so `POST /server/start` could never be reached. The agent now stays up with the server stopped.
- **Requested restarts always restart.** `POST /server/restart` counted against the crash-restart limit, and with `--no-auto-restart` it shut the agent down instead of restarting the server. Neither happens now.
- **The A2S query port default matches the server.** Without `+net_query_port` the s&box server answers A2S on 27016, but the agent polled the game port and never got an answer.
- **`/chat` tells the truth.** `say` comes from the running game, and many games (the stock `facepunch.sandbox` included) do not have one. The route now answers `422` instead of reporting a broadcast nobody received. Messages are sent as one quoted argument, so they arrive with their spaces instead of `hello·world`.
- **Player names and chat show their real characters.** Non-ASCII text (accents, Cyrillic, CJK) came out garbled, and terminal colour codes split across reads leaked into the console.
- **Status on Windows describes the server.** Memory and CPU in `/status` were those of the PowerShell wrapper that gives the server its console. They now come from the s&box server process, reported as `server_pid`.
- **The roster empties when the server exits**, a leave is announced once even when the server prints two lines for it, and the current "disconnected" line is recognised.
- **Example configs no longer switch bans off.** Both example configs set `connect-regex` to an empty string, which disabled join detection for anyone who copied them. An empty value now means the default, and the examples no longer carry it.
- **The Windows service install works.** `scripts/install-service.ps1` registered the plain console program, which has no service entry point, so the service manager could not get a "running" report from it. The program now runs under the service manager when started with `--windows-service`, which the script adds, and a server that keeps crashing stops the service with exit code 4 so its restart-on-failure settings apply. Checked on a Windows CI runner, which now runs a smoke test on every push.
- **The systemd example can write its own files.** Its sandboxing made the README's `/var/lib/sboxconsole` and `/var/log/sboxconsole` paths read-only.
- **Dashboard fixes.** The uptime pill showed "NaN", the stats panel called an API route that never existed, the player list ignored A2S, the command bar could sit below the window, and there was no allowlist tab. All fixed.

### Security

- A player name can no longer pass another SteamID through a join line (for example a name ending in `[<SteamID>] is connecting`); the real SteamID, printed last by the server, always wins.
- After 10 wrong passwords in a minute from one address, the HTTP API and RCON refuse that address for 5 minutes. Local connections are never locked out.
- An RCON client that does not log in within 10 seconds is disconnected, so idle connections cannot hold every slot.
- Commands containing line breaks or control characters are refused everywhere (HTTP, RCON, scheduler, kick template), so one command cannot carry a second one or a Ctrl-C.
- Bans and allowlist entries must be a 17-digit SteamID64 when the default connect regex is in use; anything else could never match and is refused with `400`.
- The log browser never lists or serves hidden files or dot-files on any platform, which keeps the agent's config (and its RCON password) out of `/logs`.
- Discord messages can no longer ping `@everyone` or a role through a player name.

### Added

- `/stream` events carry an `id`, and a reconnecting client gets exactly the lines it missed (`Last-Event-ID`, or `?since=<seq>`).
- `POST /scheduler` works without an `id` and returns the one it made.
- Startup warnings for settings the agent does not recognise, for regexes that do not compile and for an empty RCON password.
- A warning when A2S reports players but no join line has matched for three minutes: the roster, bans and the allowlist cannot see joins on that server build.
- A banlist, allowlist or scheduler file that cannot be read is kept as `<name>.unreadable-<time>` instead of being overwritten by the next save.
- The agent exits with code 4 when the server keeps crashing and auto-restart gives up (0 after a normal stop), so a service manager can act on it.
- A failing Discord webhook is reported once on the console, and once more when it works again.
- `SHA256SUMS` on every release.

## [1.3.3] - 2026-07-28

### Fixed

- **SIGINT was ignored in non-interactive contexts.** When the agent was launched as a background job of a non-interactive shell (init scripts, wrappers), it inherited SIGINT as ignored and the runtime never delivered the signal; the agent kept serving forever and could not be stopped with SIGINT. The inherited disposition is now reset at startup and both SIGINT and SIGTERM are handled through `PosixSignalRegistration`, so either signal runs the same graceful shutdown.
- **SIGTERM orphaned the game server.** Termination used to go through the runtime's process-exit path, which hard-exits on a short budget before the child is stopped; the game server was left running with no supervisor. Both signals now stop the child (escalating to a process-tree kill if it ignores the shutdown command) before the agent exits cleanly.

### Changed

- Builds no longer embed the build machine's checkout path in shipped binaries, and the build fails if such a path ever reappears (checked in ASCII and UTF-16).

## [1.3.2] - 2026-07-28

### Fixed

- **Shutdown hang on Linux when the server was still running.** Stopping the agent (or restarting a child that ignores the shutdown command) disposed the child's stdout stream while the output reader was still blocked on it, which never returned. The child is now stopped before its streams are torn down, so shutdown and restart complete instead of wedging the process.
- **Linux process-group cleanup never ran.** The tree-kill helper held a process handle its caller had already released, so every cleanup attempt failed silently and orphaned children could survive an agent restart.

### Added

- **Test suite.** Covers persistence round-trips, HTTP and RCON auth, ban/allowlist enforcement, scheduler persistence, audit-log formatting, log-browser path confinement, child supervision, and a `docs/api.md`-versus-implementation drift check. Runs on every push and blocks a release on failure.

## [1.3.1] - 2026-04-28

### Fixed

- **Local terminal spam filtering.** The Windows/Linux terminal mirror now uses the same suppression filter as `/history`, `/stream`, and the dashboard, so high-frequency s&box timing/resource reload lines no longer clobber typed console commands.
- **Default suppression coverage.** The built-in filter now also drops startup resource reload and cleanup baseline chatter while still preserving errors, connect/disconnect messages, and operator command output.

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

- Linux child supervision uses pipe-redirected stdin (no PTY). The Facepunch dedicated server on Linux is a regular .NET console app that reads from `Console.ReadLine`, so plain pipes work; no node-pty or libc PTY P/Invoke needed. If a future sbox build ever refuses pipe stdin on Linux the same way the Windows build refuses non-ConPTY input, file an issue and we'll add a Linux PTY backend.
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
- **Embedded web dashboard**: single-page HTML, no build step. Live console feed, player roster, banlist editor, scheduled commands, lifecycle controls.
- **A2S player tracking** via the standard A2S_INFO + A2S_PLAYER protocol on `+net_query_port`. Authoritative server name, map, and player roster; same protocol the Steam server browser uses.
- **Chat broadcast** via `POST /chat`. Forwards to the s&box `say` ConCommand with U+00B7 (middle-dot) substituted for ASCII spaces, working around the engine's argument-tokenizer truncation at whitespace ([Facepunch/sbox-public#2507](https://github.com/Facepunch/sbox-public/issues/2507)). Multi-word messages render as `hello·world` in chat.
- **Banlist with auto-kick**: JSON-backed, persists across restarts. Connect-line regex matches every joining player against the banlist and kicks immediately. Adding a ban for an already-connected player kicks them on next match.
- **Scheduled commands**: `@every 30s` / `@every 5m` / `@every 12h` shorthand or full 5-field cron expressions, backed by [Cronos](https://github.com/HangfireIO/Cronos). Persisted to JSON.
- **Auto-restart on crash** with backoff and capped attempts inside a sliding window. Customer-initiated `/server/restart` calls bypass the cap.
- **Lifecycle control**: `POST /server/{start,stop,restart}`. ServerProcess is restart-capable; the child gets replaced across crash-restart cycles while collaborators keep their references.
- **Discord webhook notifications**: lifecycle (start/stop) and player join/leave events as color-coded embeds.
- **Audit log**: append-only JSONL of every command, ban mutation, scheduler fire, and lifecycle event. Auto-rotates at 10MB, keeps 10 generations.
- **Read-only logs browser**: `--logs-dir <path>` exposes file inventory at `GET /logs` and tail at `GET /logs/<name>?tail=N`. Path resolution is canonicalized + prefix-checked against the configured root.
- **Process supervision**: Win32 Job Object kills the entire child tree on wrapper exit. ConPTY gives the child a real Windows console so it accepts stdin commands and emits readable stdout.
- **Windows service installer**: `scripts/install-service.ps1` registers the agent with `sc.exe` autostart + restart-on-failure.

### Known caveats

- **Unsigned release binary.** The published `SboxServerConsole.exe` carries no Authenticode signature. SmartScreen will warn on first run; mark-of-the-web on browser-downloaded zips may need `Unblock-File`. Build from source if your environment forbids unsigned binaries. See [Trust & First-Run Warning](README.md#trust--first-run-warning).
- **Windows-only release builds.** The published binary targets `win-x64` self-contained. The .NET 8 source compiles cleanly under Linux but the Job Object + ConPTY supervision code paths are Windows-specific.
- **`+port` and Steam Datagram Relay.** s&box clients connect via Steam Datagram Relay using SteamID, not direct UDP. `+port` still binds for A2S query traffic and the legacy Source-style listener, but most player connections come over the relay. Forward `+port` and `+net_query_port` UDP for server-browser visibility regardless.
