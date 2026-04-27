namespace SboxServerConsole;

// Abstraction over OS-specific kill-children-when-parent-dies primitives.
// Windows: Job Object with KILL_ON_JOB_CLOSE.
// Posix (TODO): setpgid(0,0) + killpg(SIGKILL), or systemd cgroup attach.
public interface IProcessGroup : IDisposable
{
    void AssignProcess(System.Diagnostics.Process p);
}

public static class ProcessGroup
{
    // Returns a platform-specific implementation, or null if the current OS has no impl.
    // Callers decide whether absence is fatal — on Windows it must be, on Posix today
    // we degrade to best-effort cleanup until sbox ships official Linux server binaries.
    public static IProcessGroup? CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return new WindowsProcessGroup();
        // TODO: PosixProcessGroup
        return null;
    }
}
