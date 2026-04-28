using System.Diagnostics;
using System.Runtime.Versioning;

namespace SboxServerConsole;

// POSIX best-effort process-tree cleanup. .NET 8's
// Process.Kill(entireProcessTree:true) walks /proc on Linux to find children
// and kills them with SIGTERM. macOS uses pgrep equivalents under the hood.
//
// Limitation: only fires on graceful Dispose. If the wrapper itself is killed
// with SIGKILL the child is reparented to init/PID 1 and keeps running. For
// production deployments use a systemd unit with KillMode=mixed so the cgroup
// supervisor reaps the orphan tree on hard parent death.
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
sealed class PosixProcessGroup : IProcessGroup
{
    Process? _proc;
    bool _disposed;

    public void AssignProcess(Process p) => _proc = p;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_proc is null) return;
        try
        {
            if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
