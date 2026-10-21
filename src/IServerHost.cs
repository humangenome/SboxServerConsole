namespace SboxServerConsole;

// Cross-platform supervision contract for the child server process. Each
// implementation owns the OS-specific spawn + I/O plumbing and exposes a
// uniform read/write/lifecycle surface so ServerProcess stays platform-neutral.
//
// Windows  -> WindowsConPtyHost (PseudoConsoleHost). The engine refuses stdin
//             from a plain pipe on Windows, so the child is bound to a ConPTY
//             and stderr is naturally merged into the pseudo-console output.
// Linux    -> LinuxServerHost. The child runs on a pseudo-terminal the agent
//             holds, for the same reason: the engine reads no console input when
//             its output is redirected. Falls back to plain pipes when no PTY
//             can be opened.
public interface IServerHost : IDisposable
{
    // Spawn the child with this argument list (never re-read by a shell) and,
    // optionally, extra environment variables on top of the agent's own. Returns
    // when the OS reports the process is running; any startup race is the
    // implementation's problem, not the caller's.
    void Start(string exe, IReadOnlyList<string> args, string workingDir,
        IReadOnlyDictionary<string, string>? environment = null);

    // Combined stdout + stderr from the child. Read returns 0 when the child
    // exits and the OS closes the pipe.
    Stream OutputStream { get; }

    // stdin to the child. Writes are line-oriented from the caller's side
    // (newlines are appended explicitly), so platforms only need to forward
    // bytes verbatim.
    Stream InputStream { get; }

    uint ChildProcessId { get; }

    // What ends a command line on this host's input ("\r\n" for ConPTY, "\r" for
    // a PTY, which is what a terminal sends for Enter).
    string LineTerminator { get; }

    // True when the agent itself plays the child's terminal (Linux PTY). The
    // child's console then redraws its input line as keys arrive, and cursor moves
    // separate output fragments, so ServerProcess decodes and filters accordingly.
    bool EmulatesTerminal { get; }

    // Why the host fell back to a degraded mode (no PTY), or null.
    string? FallbackReason { get; }
    bool HasChildExited();
    int ChildExitCode();
}
