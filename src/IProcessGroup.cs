namespace SboxServerConsole;

// Abstraction over OS-specific kill-children-when-parent-dies primitives.
// Windows: Job Object with KILL_ON_JOB_CLOSE.
// Linux/macOS: Process.Kill(entireProcessTree:true) at dispose time.
//   Note: this is best-effort — if the parent dies hard (SIGKILL) without
//   running Dispose, the child is reparented to init and survives. Hosts
//   that need true tree death on parent crash should run SboxServerConsole
//   under systemd with KillMode=mixed (see scripts/sboxserverconsole.service).
public interface IProcessGroup : IDisposable
{
    void AssignProcess(System.Diagnostics.Process p);
}

public static class ProcessGroup
{
    // Returns a platform-specific implementation, or null if the current OS
    // has no impl. Callers decide whether absence is fatal.
    public static IProcessGroup? CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return new WindowsProcessGroup();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return new PosixProcessGroup();
        return null;
    }
}
