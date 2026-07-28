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
    int _pid;
    bool _disposed;

    // Hold the pid rather than the Process object: the caller owns that object and
    // disposes it as soon as the assignment returns, and a disposed Process throws
    // on every member — which silently turned this whole cleanup into a no-op.
    public void AssignProcess(Process p) => _pid = p.Id;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(_pid);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
    }
}
