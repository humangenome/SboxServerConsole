using System.Diagnostics;
using System.Runtime.Versioning;

namespace SboxServerConsole;

// Linux child supervision. The Facepunch dedicated server ships as a regular
// .NET console app (sbox-server.sh -> dotnet sbox-server.dll), so plain
// redirected stdin/stdout works — no PTY required. We wrap the spawn in
// /bin/sh -c 'exec <exe> <args> 2>&1' so:
//   - 2>&1 folds stderr into stdout, matching the single-stream contract that
//     ConPTY already gives us on Windows
//   - exec replaces the shell with the actual server, so the PID we hold
//     points at sbox-server itself (not a wrapper shell), making
//     Process.Kill(entireProcessTree:true) reach the right tree
//   - sh re-tokenizes <args> so customer --child-args strings keep their
//     existing shell-style quoting (+hostname "My Server" works as one token)
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class LinuxServerHost : IServerHost
{
    Process? _proc;
    bool _disposed;

    public Stream OutputStream { get; private set; } = null!;
    public Stream InputStream  { get; private set; } = null!;
    public uint ChildProcessId => (uint)(_proc?.Id ?? 0);

    public void Start(string exe, string args, string workingDir)
    {
        if (string.IsNullOrEmpty(exe))
            throw new ArgumentException("exe is required", nameof(exe));

        // Single-quote-wrap the exe path so shell metachars in the path itself
        // (rare, but possible — spaces, $, &, !) survive the sh -c re-parse.
        // Customer --child-args are passed verbatim and re-tokenized by sh,
        // matching the Windows path where they round-trip through PowerShell.
        string script = $"exec {ShellSingleQuote(exe)} {args} 2>&1";

        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                WorkingDirectory = string.IsNullOrEmpty(workingDir) ? Environment.CurrentDirectory : workingDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        _proc.StartInfo.ArgumentList.Add("-c");
        _proc.StartInfo.ArgumentList.Add(script);

        if (!_proc.Start())
            throw new InvalidOperationException($"failed to spawn {exe}");

        OutputStream = _proc.StandardOutput.BaseStream;
        InputStream  = _proc.StandardInput.BaseStream;
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
        // Stop the child BEFORE touching the streams. Disposing the redirected
        // stdout stream while the output pump thread is parked in a blocking
        // Read does not return until the read completes, and the read only
        // completes when the child closes its end of the pipe — so disposing a
        // host whose child is still running would block forever.
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
        try { OutputStream?.Dispose(); } catch { }
        try { InputStream?.Dispose(); } catch { }
        if (_proc is not null)
        {
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }
    }
}
