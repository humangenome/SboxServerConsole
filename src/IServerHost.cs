namespace SboxServerConsole;

// Cross-platform supervision contract for the child server process. Each
// implementation owns the OS-specific spawn + I/O plumbing and exposes a
// uniform read/write/lifecycle surface so ServerProcess stays platform-neutral.
//
// Windows  -> WindowsConPtyHost (PseudoConsoleHost). The engine refuses stdin
//             from a plain pipe on Windows, so the child is bound to a ConPTY
//             and stderr is naturally merged into the pseudo-console output.
// Linux    -> LinuxServerHost. Plain pipe-redirected Process; the child is
//             spawned through `/bin/sh -c 'exec <exe> <args> 2>&1'` so stderr
//             folds into stdout and a single OutputStream carries everything.
public interface IServerHost : IDisposable
{
    // Spawn the child. Returns when the OS reports the process is running;
    // any startup race is the implementation's problem, not the caller's.
    void Start(string exe, string args, string workingDir);

    // Combined stdout + stderr from the child. Read returns 0 when the child
    // exits and the OS closes the pipe.
    Stream OutputStream { get; }

    // stdin to the child. Writes are line-oriented from the caller's side
    // (newlines are appended explicitly), so platforms only need to forward
    // bytes verbatim.
    Stream InputStream { get; }

    uint ChildProcessId { get; }
    bool HasChildExited();
    int ChildExitCode();
}
