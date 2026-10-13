using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SboxServerConsole;

// Owns the child sbox-server.exe process across (potentially many) crash-restart
// cycles. A single ServerProcess instance lives for the lifetime of the agent;
// the actual child process gets replaced on restart while every collaborator
// (Banlist, Scheduler, HttpApi, RCON listener) keeps its single ServerProcess
// reference and its calls transparently route to the current incarnation.
//
// Why the child exits decides what happens next:
//   - Requested restart (/server/restart): a new child starts right away. It does
//     not count against the crash cap and works with --no-auto-restart too.
//   - Requested stop (/server/stop): the child stays down and the agent keeps
//     serving, so /server/start can bring it back.
//   - Agent shutdown (signal, Ctrl-C): the child is stopped and the agent exits.
//   - Anything else is a crash. With AutoRestart on, sleep RestartBackoffSeconds
//     and spawn a new child, capped at RestartMaxAttempts inside
//     RestartWindowSeconds. When the cap is hit, or AutoRestart is off,
//     OnSupervisorExit fires with GaveUp set and the agent exits non-zero.
public sealed class ServerProcess : IDisposable
{
    sealed class StructuredChatLine
    {
        [JsonPropertyName("steamid")]
        public string? SteamId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    readonly CliConfig _cfg;
    readonly MessageBuffer _buffer;
    readonly Action<string>? _localConsoleMirror;
    readonly object _lifecycleLock = new();
    readonly object _stdinLock = new();
    readonly object _echoLock = new();
    readonly List<(string Cmd, DateTime At)> _pendingEcho = new();

    IServerHost? _host;
    IProcessGroup? _group;
    Thread? _outputPump;
    Thread? _exitWatcher;
    CancellationTokenSource _childCts = new();
    DateTime _childStartedAt;
    int _lastExitCode = -1;
    ExitIntent _intent = ExitIntent.None;
    bool _gaveUp;
    int _restartAttemptsInWindow;
    DateTime _restartWindowStart;
    readonly Regex? _suppressRe;

    enum ExitIntent { None, Restart, Stop, Shutdown }

    // Fires when supervision ends: the agent is shutting down, or the child crashed
    // and will not be restarted (see GaveUp).
    public event Action? OnSupervisorExit;

    // Fires every time a child process exits, for any reason. Collaborators that
    // hold per-session state (the player roster) reset on it.
    public event Action? OnChildExited;

    public ServerProcess(CliConfig cfg, MessageBuffer buffer, Action<string>? localConsoleMirror = null)
    {
        _cfg = cfg;
        _buffer = buffer;
        _localConsoleMirror = localConsoleMirror;
        _suppressRe = string.IsNullOrWhiteSpace(cfg.SuppressLineRegex)
            ? null
            : SafeCompile(cfg.SuppressLineRegex);
    }

    static Regex? SafeCompile(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return null; }
    }

    public int ChildPid
    {
        get
        {
            lock (_lifecycleLock) return (int)(_host?.ChildProcessId ?? 0);
        }
    }

    // The game server's own process. On Windows the child is the PowerShell wrapper
    // that gives the server its console, so the server is found among the job
    // object's processes by executable name. Falls back to the child.
    public int ServerPid
    {
        get
        {
            IProcessGroup? group;
            int child;
            lock (_lifecycleLock) { group = _group; child = (int)(_host?.ChildProcessId ?? 0); }
            if (group is null || !OperatingSystem.IsWindows()) return child;
            string want = Path.GetFileNameWithoutExtension(_cfg.ChildExe);
            try
            {
                foreach (int pid in group.ProcessIds())
                {
                    if (pid == child) continue;
                    try
                    {
                        using var p = System.Diagnostics.Process.GetProcessById(pid);
                        if (string.Equals(p.ProcessName, want, StringComparison.OrdinalIgnoreCase)) return pid;
                    }
                    catch { }
                }
            }
            catch { }
            return child;
        }
    }

    public bool IsAlive
    {
        get
        {
            lock (_lifecycleLock) return _host is not null && !_host.HasChildExited();
        }
    }

    public TimeSpan Uptime
    {
        get
        {
            lock (_lifecycleLock) return _host is null ? TimeSpan.Zero : DateTime.UtcNow - _childStartedAt;
        }
    }

    public int LastExitCode
    {
        get { lock (_lifecycleLock) return _lastExitCode; }
    }

    public int RestartAttemptsInWindow
    {
        get { lock (_lifecycleLock) return _restartAttemptsInWindow; }
    }

    // True when supervision ended because the child crashed and was not restarted
    // (auto-restart off, or the crash cap was hit). The agent exits non-zero then.
    public bool GaveUp
    {
        get { lock (_lifecycleLock) return _gaveUp; }
    }

    public void Start() => StartChild(initial: true);

    public bool TryStartIfStopped()
    {
        lock (_lifecycleLock)
        {
            if (_host is not null && !_host.HasChildExited()) return false;
        }
        StartChild(initial: false);
        return true;
    }

    void StartChild(bool initial)
    {
        lock (_lifecycleLock)
        {
            DisposeChildLocked();
            _intent = ExitIntent.None;
            _childCts = new CancellationTokenSource();

            string spawnExe;
            string spawnArgs;
            string wrapperLabel;

            if (OperatingSystem.IsWindows())
            {
                _host = new PseudoConsoleHost();
                // Wrap actual server in PowerShell so it gets a real Windows console
                // (sbox-server.exe doesn't bind directly to a ConPTY host).
                var psExe = $@"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\WindowsPowerShell\v1.0\powershell.exe";
                if (!File.Exists(psExe)) psExe = "powershell.exe";
                var psCmd = $"& \"{_cfg.ChildExe}\" {_cfg.ChildArgs}";
                // Pass the script via -EncodedCommand (UTF-16 LE base64) so PowerShell
                // never has to parse it through CreateProcess argv quoting. Any byte —
                // including & ( ) ; | $ ` and embedded double quotes from --child-args
                // values like +hostname "Foo & Bar" — survives verbatim.
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psCmd));
                spawnExe = psExe;
                spawnArgs = $"-NoProfile -NoLogo -EncodedCommand {encoded}";
                wrapperLabel = "powershell";
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                _host = new LinuxServerHost();
                spawnExe = _cfg.ChildExe;
                spawnArgs = _cfg.ChildArgs;
                wrapperLabel = "sh";
            }
            else
            {
                throw new PlatformNotSupportedException(
                    $"unsupported platform; only Windows, Linux, and macOS are wired today");
            }

            _host.Start(spawnExe, spawnArgs, _cfg.GameDir);
            _childStartedAt = DateTime.UtcNow;
            if (_host.EmulatesTerminal) wrapperLabel = "pty";
            _buffer.Append("system", initial
                ? $"shell wrapper ({wrapperLabel}) started pid={_host.ChildProcessId}"
                : $"child restarted pid={_host.ChildProcessId}");
            if (_host.FallbackReason is not null)
                _buffer.Append("system", $"warn: no pseudo-terminal ({_host.FallbackReason}); the server will not read console commands, so commands, kicks, bans and the graceful quit cannot reach it");

            try
            {
                _group = ProcessGroup.CreateForCurrentPlatform();
                if (_group is not null)
                {
                    using var p = System.Diagnostics.Process.GetProcessById((int)_host.ChildProcessId);
                    _group.AssignProcess(p);
                }
            }
            catch (Exception ex)
            {
                _buffer.Append("system", $"warn: process group attach failed: {ex.Message}");
            }

            _outputPump = new Thread(PumpOutput) { IsBackground = true, Name = "child-output" };
            _outputPump.Start();

            _exitWatcher = new Thread(WatchExit) { IsBackground = true, Name = "child-exit" };
            _exitWatcher.Start();
        }
    }

    void DisposeChildLocked()
    {
        try { _childCts.Cancel(); } catch { }
        try { _group?.Dispose(); } catch { }
        try { _host?.Dispose(); } catch { }
        // The exit watcher calls StartChild itself on a restart; joining the current
        // thread would just burn the whole timeout before every restart.
        try { if (_outputPump is not null && _outputPump != Thread.CurrentThread) _outputPump.Join(2000); } catch { }
        try { if (_exitWatcher is not null && _exitWatcher != Thread.CurrentThread) _exitWatcher.Join(2000); } catch { }
        _host = null;
        _group = null;
        _outputPump = null;
        _exitWatcher = null;
    }

    void PumpOutput()
    {
        var host = _host;
        if (host is null) return;
        var stream = host.OutputStream;
        var buf = new byte[4096];
        bool terminal = host.EmulatesTerminal;
        var decoder = new ConsoleLineDecoder(l => HandleLine(l, terminal), breakOnCursorMove: terminal);
        try
        {
            while (!_childCts.IsCancellationRequested)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                decoder.Feed(buf.AsSpan(0, n));
            }
        }
        catch (Exception ex) { _buffer.Append("system", $"output pump exception: {ex.Message}"); }
        finally { decoder.Flush(); }
    }

    void HandleLine(string raw, bool terminal)
    {
        // sbox pads stdout lines with ~70 trailing spaces (column-aligned status display).
        // Strip that before suppression-match and before storing — saves bandwidth on the
        // SSE stream and makes /history actually readable.
        // On a PTY the console's redraw fragments also carry leading padding.
        var line = terminal ? raw.Trim() : raw.TrimEnd();
        if (line.Length == 0) return;
        if (_suppressRe is not null && _suppressRe.IsMatch(line)) return;
        if (IsInputEcho(line)) return;
        _localConsoleMirror?.Invoke(line);
        if (TryFormatStructuredChatLine(line, out var chatLine))
        {
            _buffer.Append("chat", chatLine);
            return;
        }
        _buffer.Append("stdout", line);
    }

    static bool TryFormatStructuredChatLine(string line, out string chatLine)
    {
        chatLine = "";
        const string marker = "SSCHAT ";
        int markerAt = line.IndexOf(marker, StringComparison.Ordinal);
        if (markerAt < 0) return false;

        var payload = line[(markerAt + marker.Length)..].Trim();
        if (payload.Length == 0) return false;

        if (payload[0] != '{')
        {
            chatLine = CleanChatField(payload, 600);
            return chatLine.Length > 0;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<StructuredChatLine>(payload);
            var message = CleanChatField(parsed?.Message, 512);
            if (message.Length == 0) return false;

            var name = CleanChatField(parsed?.Name, 80);
            if (name.Length == 0) name = CleanChatField(parsed?.SteamId, 32);
            chatLine = name.Length > 0 ? $"{name}: {message}" : message;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static string CleanChatField(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }

    void WatchExit()
    {
        var host = _host;
        if (host is null) return;
        try
        {
            while (!_childCts.IsCancellationRequested && !host.HasChildExited()) Thread.Sleep(500);
        }
        catch { }
        // A cancelled watcher belongs to a child that StartChild is already replacing.
        if (_childCts.IsCancellationRequested && !host.HasChildExited()) return;
        int code;
        try { code = host.ChildExitCode(); } catch { code = -1; }
        ExitIntent intent;
        lock (_lifecycleLock)
        {
            if (!ReferenceEquals(_host, host)) return; // already replaced
            _lastExitCode = code;
            intent = _intent;
            _intent = ExitIntent.None;
        }
        lock (_echoLock) _pendingEcho.Clear();
        try { OnChildExited?.Invoke(); } catch { }

        switch (intent)
        {
            case ExitIntent.Restart:
                _buffer.Append("system", $"child exited code={code} for a requested restart; starting a new one");
                Thread.Sleep(1000);
                try { StartChild(initial: false); }
                catch (Exception ex)
                {
                    _buffer.Append("system", $"restart failed: {ex.Message}");
                    lock (_lifecycleLock) _gaveUp = true;
                    OnSupervisorExit?.Invoke();
                }
                return;
            case ExitIntent.Stop:
                _buffer.Append("system", $"child exited code={code}; server stopped on request (POST /server/start starts it again)");
                return;
            case ExitIntent.Shutdown:
                _buffer.Append("system", $"child exited code={code}; agent shutting down");
                OnSupervisorExit?.Invoke();
                return;
        }

        _buffer.Append("system", $"child exited unexpectedly code={code}");
        if (!_cfg.AutoRestart)
        {
            _buffer.Append("system", "auto-restart is off (--no-auto-restart); agent exiting");
            lock (_lifecycleLock) _gaveUp = true;
            OnSupervisorExit?.Invoke();
            return;
        }
        if (!TryClaimRestartSlot())
        {
            _buffer.Append("system", $"auto-restart cap of {_cfg.RestartMaxAttempts} hit inside {_cfg.RestartWindowSeconds}s window, giving up");
            lock (_lifecycleLock) _gaveUp = true;
            OnSupervisorExit?.Invoke();
            return;
        }
        Thread.Sleep(TimeSpan.FromSeconds(_cfg.RestartBackoffSeconds));
        lock (_lifecycleLock)
        {
            // Someone asked for a stop or shutdown during the backoff, or already started a child.
            if (_intent is ExitIntent.Stop or ExitIntent.Shutdown) { _intent = ExitIntent.None; return; }
            if (!ReferenceEquals(_host, host)) return;
        }
        try { StartChild(initial: false); }
        catch (Exception ex)
        {
            _buffer.Append("system", $"auto-restart failed: {ex.Message}");
            lock (_lifecycleLock) _gaveUp = true;
            OnSupervisorExit?.Invoke();
        }
    }

    bool TryClaimRestartSlot()
    {
        lock (_lifecycleLock)
        {
            var now = DateTime.UtcNow;
            if (_restartWindowStart == default || (now - _restartWindowStart).TotalSeconds > _cfg.RestartWindowSeconds)
            {
                _restartWindowStart = now;
                _restartAttemptsInWindow = 0;
            }
            if (_restartAttemptsInWindow >= _cfg.RestartMaxAttempts) return false;
            _restartAttemptsInWindow++;
            return true;
        }
    }

    // A command is one line of plain text. Line breaks would smuggle a second,
    // unaudited command in, and control characters reach the child's console as
    // keystrokes (Ctrl-C included), so both are refused for every caller: HTTP,
    // RCON, scheduler, kick templates and the local terminal.
    public static bool IsSendableCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        foreach (char c in cmd)
        {
            if (c < 0x20 || c == 0x7F) return false;
        }
        return true;
    }

    public bool TrySendCommand(string cmd)
    {
        if (!IsSendableCommand(cmd))
        {
            _buffer.Append("system", "refused a command containing a line break or control character");
            return false;
        }
        IServerHost? host;
        lock (_lifecycleLock) host = _host;
        if (host is null) return false;
        if (host.HasChildExited()) return false;
        try
        {
            lock (_echoLock)
            {
                _pendingEcho.Add((cmd, DateTime.UtcNow));
                if (_pendingEcho.Count > 32) _pendingEcho.RemoveAt(0);
            }
            lock (_stdinLock)
            {
                // ConPTY takes CRLF from the host side; a PTY takes CR, which is what a
                // terminal sends for Enter.
                var bytes = Encoding.UTF8.GetBytes(cmd + host.LineTerminator);
                host.InputStream.Write(bytes, 0, bytes.Length);
                host.InputStream.Flush();
            }
            _buffer.Append("input", cmd);
            return true;
        }
        catch (Exception ex)
        {
            _buffer.Append("system", $"stdin write failed: {ex.Message}");
            return false;
        }
    }

    // The server's console redraws its input line after every key, so a typed
    // "status" comes back as "s", "st", ... "status" (on a PTY) or as a partial
    // "kick 7656119796" plus a bare ">" prompt (through ConPTY). Those redraws are
    // dropped; the server's own "> status" line is kept.
    bool IsInputEcho(string rawLine)
    {
        var line = rawLine.TrimStart();
        lock (_echoLock)
        {
            if (_pendingEcho.Count == 0) return false;
            if (line == ">") return true;
            var cutoff = DateTime.UtcNow.AddSeconds(-60);
            _pendingEcho.RemoveAll(p => p.At < cutoff);
            for (int i = 0; i < _pendingEcho.Count; i++)
            {
                var cmd = _pendingEcho[i].Cmd;
                if (line == "> " + cmd || line == ">" + cmd)
                {
                    _pendingEcho.RemoveAt(i);
                    return false;
                }
                if (cmd.StartsWith(line, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    // Stops the child and leaves the agent running (POST /server/stop).
    public void Stop(TimeSpan timeout) => StopChild(ExitIntent.Stop, timeout);

    // Stops the child because the agent is exiting.
    public void Shutdown(TimeSpan timeout) => StopChild(ExitIntent.Shutdown, timeout);

    void StopChild(ExitIntent intent, TimeSpan timeout)
    {
        IServerHost? host;
        lock (_lifecycleLock)
        {
            _intent = intent;
            host = _host;
        }
        if (host is null || host.HasChildExited()) return;
        try
        {
            TrySendCommand(_cfg.ShutdownCommand);
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !host.HasChildExited()) Thread.Sleep(200);
            if (!host.HasChildExited())
            {
                _buffer.Append("system", "graceful shutdown timed out; killing the server process tree");
                lock (_lifecycleLock)
                {
                    if (ReferenceEquals(_host, host)) { try { _group?.Dispose(); } catch { } try { host.Dispose(); } catch { } }
                }
            }
        }
        catch (Exception ex) { _buffer.Append("system", $"stop failed: {ex.Message}"); }
    }

    // Requested restart: never refused by the crash cap, works with --no-auto-restart.
    public bool Restart(TimeSpan stopTimeout)
    {
        IServerHost? host;
        lock (_lifecycleLock)
        {
            host = _host;
            if (host is not null && !host.HasChildExited()) _intent = ExitIntent.Restart;
        }
        if (host is not null && !host.HasChildExited())
        {
            try
            {
                TrySendCommand(_cfg.ShutdownCommand);
                var deadline = DateTime.UtcNow + stopTimeout;
                while (DateTime.UtcNow < deadline && !host.HasChildExited()) Thread.Sleep(200);
            }
            catch { }
            // Still alive: kill it. The exit watcher sees the Restart intent and starts a new child.
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_host, host) && !host.HasChildExited())
                {
                    try { _group?.Dispose(); } catch { }
                    try { host.Dispose(); } catch { }
                }
            }
            return true;
        }
        // Child already dead (stopped, or between crash restarts): start a new one now.
        StartChild(initial: false);
        return true;
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            _intent = ExitIntent.Shutdown;
            DisposeChildLocked();
        }
    }
}
