using System.Text;
using System.Text.RegularExpressions;

namespace SboxServerConsole;

// Owns the child sbox-server.exe process across (potentially many) crash-restart
// cycles. A single ServerProcess instance lives for the lifetime of the agent;
// the actual child process gets replaced on restart while every collaborator
// (Banlist, Scheduler, HttpApi, RCON listener) keeps its single ServerProcess
// reference and its calls transparently route to the current incarnation.
//
// Auto-restart policy (controlled by CliConfig):
//   - On unexpected child exit, if AutoRestart is on, sleep RestartBackoffSeconds
//     then spawn a new child. Cap at RestartMaxAttempts within RestartWindowSeconds.
//   - When the cap is hit, OnSupervisorExit fires and the wrapper exits.
//   - User-requested stops (ctrl-C, /server/stop, app shutdown) bypass the
//     restart logic.
public sealed class ServerProcess : IDisposable
{
    readonly CliConfig _cfg;
    readonly MessageBuffer _buffer;
    readonly object _lifecycleLock = new();
    readonly object _stdinLock = new();

    PseudoConsoleHost? _pty;
    IProcessGroup? _group;
    Thread? _outputPump;
    Thread? _exitWatcher;
    CancellationTokenSource _childCts = new();
    DateTime _childStartedAt;
    int _lastExitCode = -1;
    bool _userRequestedStop;
    int _restartAttemptsInWindow;
    DateTime _restartWindowStart;
    readonly Regex? _suppressRe;

    public event Action? OnSupervisorExit;

    public ServerProcess(CliConfig cfg, MessageBuffer buffer)
    {
        _cfg = cfg;
        _buffer = buffer;
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
            lock (_lifecycleLock) return (int)(_pty?.ChildProcessId ?? 0);
        }
    }

    public bool IsAlive
    {
        get
        {
            lock (_lifecycleLock) return _pty is not null && !_pty.HasChildExited();
        }
    }

    public TimeSpan Uptime
    {
        get
        {
            lock (_lifecycleLock) return _pty is null ? TimeSpan.Zero : DateTime.UtcNow - _childStartedAt;
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

    public void Start() => StartChild(initial: true);

    public bool TryStartIfStopped()
    {
        lock (_lifecycleLock)
        {
            if (_pty is not null && !_pty.HasChildExited()) return false;
        }
        StartChild(initial: false);
        return true;
    }

    void StartChild(bool initial)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ConPTY launch is Windows-only");

        lock (_lifecycleLock)
        {
            DisposeChildLocked();
            _userRequestedStop = false;
            _childCts = new CancellationTokenSource();

            _pty = new PseudoConsoleHost();
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
            var wrappedArgs = $"-NoProfile -NoLogo -EncodedCommand {encoded}";
            _pty.Start(psExe, wrappedArgs, _cfg.GameDir);
            _childStartedAt = DateTime.UtcNow;
            _buffer.Append("system", initial
                ? $"shell wrapper (powershell) started pid={_pty.ChildProcessId}"
                : $"child restarted pid={_pty.ChildProcessId}");

            try
            {
                _group = ProcessGroup.CreateForCurrentPlatform();
                if (_group is not null)
                {
                    using var p = System.Diagnostics.Process.GetProcessById((int)_pty.ChildProcessId);
                    _group.AssignProcess(p);
                }
            }
            catch (Exception ex)
            {
                _buffer.Append("system", $"warn: process group attach failed: {ex.Message}");
            }

            _outputPump = new Thread(PumpOutput) { IsBackground = true, Name = "pty-output" };
            _outputPump.Start();

            _exitWatcher = new Thread(WatchExit) { IsBackground = true, Name = "pty-exit" };
            _exitWatcher.Start();
        }
    }

    void DisposeChildLocked()
    {
        try { _childCts.Cancel(); } catch { }
        try { _group?.Dispose(); } catch { }
        try { _pty?.Dispose(); } catch { }
        try { _outputPump?.Join(2000); } catch { }
        try { _exitWatcher?.Join(2000); } catch { }
        _pty = null;
        _group = null;
        _outputPump = null;
        _exitWatcher = null;
    }

    void PumpOutput()
    {
        var pty = _pty;
        if (pty is null) return;
        var stream = pty.OutputStream;
        var buf = new byte[4096];
        var line = new StringBuilder();
        try
        {
            while (!_childCts.IsCancellationRequested)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == 0x1B) { i = SkipAnsi(buf, i, n); continue; }
                    if (b == '\r') continue;
                    if (b == '\n') { Flush(line); continue; }
                    if (b < 0x20 && b != '\t') continue;
                    line.Append((char)b);
                    if (line.Length > 2000) Flush(line);
                }
            }
        }
        catch (Exception ex) { _buffer.Append("system", $"output pump exception: {ex.Message}"); }
        finally { Flush(line); }
    }

    static int SkipAnsi(byte[] buf, int i, int len)
    {
        if (i + 1 >= len) return i;
        byte b1 = buf[i + 1];
        i += 1;
        if (b1 == '[')
        {
            i += 1;
            while (i < len && (buf[i] < 0x40 || buf[i] > 0x7E)) i++;
            return i;
        }
        if (b1 == ']')
        {
            i += 1;
            while (i < len && buf[i] != 0x07) i++;
            return i;
        }
        return i;
    }

    void Flush(StringBuilder sb)
    {
        if (sb.Length == 0) return;
        // sbox pads stdout lines with ~70 trailing spaces (column-aligned status display).
        // Strip that before suppression-match and before storing — saves bandwidth on the
        // SSE stream and makes /history actually readable.
        var line = sb.ToString().TrimEnd();
        sb.Clear();
        if (line.Length == 0) return;
        if (_suppressRe is not null && _suppressRe.IsMatch(line)) return;
        _buffer.Append("stdout", line);
    }

    void WatchExit()
    {
        var pty = _pty;
        if (pty is null) return;
        try
        {
            while (!_childCts.IsCancellationRequested && !pty.HasChildExited()) Thread.Sleep(500);
        }
        catch { }
        int code;
        try { code = pty.ChildExitCode(); } catch { code = -1; }
        bool userStopped;
        bool autoRestart = _cfg.AutoRestart;
        lock (_lifecycleLock)
        {
            _lastExitCode = code;
            userStopped = _userRequestedStop;
        }
        _buffer.Append("system", $"child exited code={code} user_requested={(userStopped ? "yes" : "no")}");

        if (userStopped || !autoRestart)
        {
            OnSupervisorExit?.Invoke();
            return;
        }
        // Decide whether to restart or give up.
        bool restart = TryClaimRestartSlot();
        if (!restart)
        {
            _buffer.Append("system", $"auto-restart cap of {_cfg.RestartMaxAttempts} hit inside {_cfg.RestartWindowSeconds}s window — giving up");
            OnSupervisorExit?.Invoke();
            return;
        }
        Thread.Sleep(TimeSpan.FromSeconds(_cfg.RestartBackoffSeconds));
        try { StartChild(initial: false); }
        catch (Exception ex)
        {
            _buffer.Append("system", $"auto-restart failed: {ex.Message}");
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

    public bool TrySendCommand(string cmd)
    {
        PseudoConsoleHost? pty;
        lock (_lifecycleLock) pty = _pty;
        if (pty is null) return false;
        if (pty.HasChildExited()) return false;
        try
        {
            lock (_stdinLock)
            {
                var bytes = Encoding.UTF8.GetBytes(cmd + "\r\n");
                pty.InputStream.Write(bytes, 0, bytes.Length);
                pty.InputStream.Flush();
            }
            _buffer.Append("input", cmd);
            return true;
        }
        catch (Exception ex)
        {
            _buffer.Append("system", $"pty input write failed: {ex.Message}");
            return false;
        }
    }

    public void Stop(TimeSpan timeout)
    {
        PseudoConsoleHost? pty;
        lock (_lifecycleLock)
        {
            _userRequestedStop = true;
            pty = _pty;
        }
        if (pty is null || pty.HasChildExited()) return;
        try
        {
            TrySendCommand(_cfg.ShutdownCommand);
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !pty.HasChildExited()) Thread.Sleep(200);
            if (!pty.HasChildExited()) _buffer.Append("system", "graceful shutdown timed out, ClosePseudoConsole will kill child");
        }
        catch (Exception ex) { _buffer.Append("system", $"stop failed: {ex.Message}"); }
    }

    public bool Restart(TimeSpan stopTimeout)
    {
        PseudoConsoleHost? pty;
        lock (_lifecycleLock) pty = _pty;
        if (pty is not null && !pty.HasChildExited())
        {
            // Send graceful shutdown without flagging as user-requested stop —
            // we want the auto-restart machinery to fire.
            try
            {
                TrySendCommand(_cfg.ShutdownCommand);
                var deadline = DateTime.UtcNow + stopTimeout;
                while (DateTime.UtcNow < deadline && !pty.HasChildExited()) Thread.Sleep(200);
            }
            catch { }
            // If still alive, dispose the pty (kills it).
            lock (_lifecycleLock)
            {
                if (_pty is not null && !_pty.HasChildExited())
                {
                    try { _pty.Dispose(); } catch { }
                }
            }
            // WatchExit thread will handle the auto-restart when it sees the exit.
            return true;
        }
        // Child already dead — start a new one immediately.
        StartChild(initial: false);
        return true;
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            _userRequestedStop = true;
            DisposeChildLocked();
        }
    }
}
