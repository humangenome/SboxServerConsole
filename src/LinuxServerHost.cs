using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SboxServerConsole;

// Linux child supervision.
//
// The s&box dedicated server only reads console input when its output is a real
// terminal: its launcher skips the whole console (input included) when
// Console.IsOutputRedirected is true. A plain pipe therefore delivers output but
// silently drops every command, kick and quit. So on Linux the child runs on a
// pseudo-terminal, the same reason Windows uses ConPTY.
//
// The agent plays the terminal: it holds the PTY master, gives the PTY a window
// size (the server's console ignores input on a zero-width terminal) and answers
// the cursor-position queries the console sends (ESC[6n). Without those answers
// the console's own redraw swallows typed input.
//
// The child is spawned through /bin/sh -c 'exec <exe> <args> 0<>"$SSC_PTY" 1>&0 2>&0':
//   - exec replaces the shell with the server, so the PID we hold is the server's
//     and Process.Kill(entireProcessTree:true) reaches the right tree
//   - sh re-tokenizes <args>, so --child-args keeps shell-style quoting
//     (+hostname "My Server" stays one token)
//   - stdin, stdout and stderr all point at the PTY slave, so one stream carries
//     everything, matching the ConPTY contract on Windows
//
// If no PTY can be opened (a container without /dev/ptmx, or macOS), the child
// falls back to plain pipes and the agent says plainly that commands will not
// reach the server.
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class LinuxServerHost : IServerHost
{
    const ushort PtyColumns = 160;
    const ushort PtyRows = 48;

    Process? _proc;
    PtyMasterStream? _pty;
    bool _disposed;

    public Stream OutputStream { get; private set; } = null!;
    public Stream InputStream  { get; private set; } = null!;
    public uint ChildProcessId => (uint)(_proc?.Id ?? 0);
    // True when the agent is the child's terminal (see the class comment).
    public bool EmulatesTerminal => _pty is not null;
    public string LineTerminator => _pty is not null ? "\r" : "\r\n";

    // Set when the PTY could not be opened; ServerProcess reports it once.
    public string? FallbackReason { get; private set; }

    public void Start(string exe, string args, string workingDir)
    {
        if (string.IsNullOrEmpty(exe))
            throw new ArgumentException("exe is required", nameof(exe));

        string? slavePath = null;
        if (OperatingSystem.IsLinux())
        {
            try { (_pty, slavePath) = PtyMasterStream.Open(PtyColumns, PtyRows); }
            catch (Exception ex) { FallbackReason = ex.Message; _pty = null; }
        }
        else
        {
            FallbackReason = "pseudo-terminal support is Linux-only";
        }

        // Single-quote-wrap the exe path so shell metachars in the path itself
        // (rare, but possible: spaces, $, &, !) survive the sh -c re-parse.
        string script = _pty is not null
            ? $"exec {ShellSingleQuote(exe)} {args} 0<>\"$SSC_PTY\" 1>&0 2>&0"
            : $"exec {ShellSingleQuote(exe)} {args} 2>&1";

        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                WorkingDirectory = string.IsNullOrEmpty(workingDir) ? Environment.CurrentDirectory : workingDir,
                RedirectStandardInput = _pty is null,
                RedirectStandardOutput = _pty is null,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        _proc.StartInfo.ArgumentList.Add("-c");
        _proc.StartInfo.ArgumentList.Add(script);
        if (_pty is not null)
        {
            _proc.StartInfo.Environment["SSC_PTY"] = slavePath!;
            // The console draws with standard xterm sequences; a service manager
            // usually starts us with no TERM at all.
            _proc.StartInfo.Environment["TERM"] = "xterm";
            _proc.StartInfo.Environment["COLUMNS"] = PtyColumns.ToString();
            _proc.StartInfo.Environment["LINES"] = PtyRows.ToString();
        }

        try
        {
            if (!_proc.Start())
                throw new InvalidOperationException($"failed to spawn {exe}");
        }
        catch
        {
            _pty?.Dispose();
            _pty = null;
            throw;
        }

        if (_pty is not null)
        {
            OutputStream = _pty;
            InputStream = _pty;
        }
        else
        {
            OutputStream = _proc.StandardOutput.BaseStream;
            InputStream  = _proc.StandardInput.BaseStream;
        }
    }

    static string ShellSingleQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public bool HasChildExited()
    {
        if (_proc is null) return true;
        try { return _proc.HasExited; }
        catch { return true; }
    }

    public int ChildExitCode()
    {
        if (_proc is null) return -1;
        try { return _proc.HasExited ? _proc.ExitCode : -1; }
        catch { return -1; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Stop the child BEFORE touching the streams. Disposing a stream while the
        // output pump thread is parked in a blocking read does not return until the
        // read completes, and the read only completes when the child closes its end,
        // so disposing a host whose child is still running would block forever.
        if (_proc is not null)
        {
            try
            {
                if (!_proc.HasExited)
                {
                    _proc.Kill(entireProcessTree: true);
                    _proc.WaitForExit(5000);
                }
            }
            catch { }
        }
        if (_pty is not null)
        {
            try { _pty.Dispose(); } catch { }
        }
        else
        {
            try { OutputStream?.Dispose(); } catch { }
            try { InputStream?.Dispose(); } catch { }
        }
        if (_proc is not null)
        {
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }
    }
}

// The master side of a Linux pseudo-terminal as a Stream. Reads see what the child
// writes to its terminal; writes are what the child reads as typed input.
// Cursor-position queries (ESC[6n) in the output are answered here, inline, the
// way a terminal emulator would.
[SupportedOSPlatform("linux")]
sealed class PtyMasterStream : Stream
{
    const int O_RDWR = 0x2;
    const int O_NOCTTY = 0x100;
    const int O_CLOEXEC = 0x80000;
    const ulong TIOCSWINSZ = 0x5414;
    const int EINTR = 4;
    const int EIO = 5;
    static readonly byte[] CursorQuery = { 0x1B, (byte)'[', (byte)'6', (byte)'n' };

    [StructLayout(LayoutKind.Sequential)]
    struct WinSize { public ushort Rows, Cols, XPixel, YPixel; }

    [DllImport("libc", SetLastError = true)] static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] static extern int ptsname_r(int fd, byte[] buf, nuint buflen);
    [DllImport("libc", SetLastError = true)] static extern int ioctl(int fd, ulong request, ref WinSize ws);
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, byte[] buf, nint count);
    [DllImport("libc", SetLastError = true)] static extern nint write(int fd, byte[] buf, nint count);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);

    int _fd;
    readonly ushort _rows;
    readonly object _writeLock = new();
    // Bytes of a cursor query that straddled the previous read.
    int _queryMatched;

    PtyMasterStream(int fd, ushort rows) { _fd = fd; _rows = rows; }

    public static (PtyMasterStream Stream, string SlavePath) Open(ushort cols, ushort rows)
    {
        int fd = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (fd < 0) throw new IOException($"posix_openpt failed (errno {Marshal.GetLastPInvokeError()})");
        try
        {
            if (grantpt(fd) != 0) throw new IOException($"grantpt failed (errno {Marshal.GetLastPInvokeError()})");
            if (unlockpt(fd) != 0) throw new IOException($"unlockpt failed (errno {Marshal.GetLastPInvokeError()})");
            var name = new byte[256];
            if (ptsname_r(fd, name, (nuint)name.Length) != 0)
                throw new IOException($"ptsname_r failed (errno {Marshal.GetLastPInvokeError()})");
            int len = Array.IndexOf(name, (byte)0);
            string slave = Encoding.ASCII.GetString(name, 0, len < 0 ? name.Length : len);
            var ws = new WinSize { Rows = rows, Cols = cols };
            if (ioctl(fd, TIOCSWINSZ, ref ws) != 0)
                throw new IOException($"setting the terminal size failed (errno {Marshal.GetLastPInvokeError()})");
            return (new PtyMasterStream(fd, rows), slave);
        }
        catch
        {
            close(fd);
            throw;
        }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var tmp = offset == 0 ? buffer : new byte[count];
        while (true)
        {
            int fd = _fd;
            if (fd < 0) return 0;
            nint n = read(fd, tmp, count);
            if (n < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR) continue;
                // EIO is how Linux reports that every slave descriptor is closed:
                // the child has exited. That is end of stream, not an error.
                if (errno == EIO) return 0;
                throw new IOException($"pseudo-terminal read failed (errno {errno})");
            }
            if (!ReferenceEquals(tmp, buffer)) Buffer.BlockCopy(tmp, 0, buffer, offset, (int)n);
            AnswerCursorQueries(buffer.AsSpan(offset, (int)n));
            return (int)n;
        }
    }

    void AnswerCursorQueries(ReadOnlySpan<byte> data)
    {
        int answers = 0;
        foreach (byte b in data)
        {
            if (b == CursorQuery[_queryMatched])
            {
                if (++_queryMatched == CursorQuery.Length) { answers++; _queryMatched = 0; }
            }
            else
            {
                _queryMatched = b == CursorQuery[0] ? 1 : 0;
            }
        }
        if (answers == 0) return;
        // Any consistent position will do: nothing renders this screen.
        var reply = Encoding.ASCII.GetBytes($"\x1b[{_rows / 2};1R");
        for (int i = 0; i < answers; i++)
        {
            try { Write(reply, 0, reply.Length); } catch { return; }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var data = offset == 0 ? buffer : buffer.AsSpan(offset, count).ToArray();
        lock (_writeLock)
        {
            int done = 0;
            while (done < count)
            {
                int fd = _fd;
                if (fd < 0) throw new ObjectDisposedException(nameof(PtyMasterStream));
                var chunk = done == 0 ? data : data.AsSpan(done, count - done).ToArray();
                nint n = write(fd, chunk, count - done);
                if (n < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == EINTR) continue;
                    throw new IOException($"pseudo-terminal write failed (errno {errno})");
                }
                done += (int)n;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        int fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0) close(fd);
        base.Dispose(disposing);
    }
}
