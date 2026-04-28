using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SboxServerConsole;

// Windows ConPTY (pseudo-console) wrapper.
//
// Launches a child process attached to a pseudo-console so the child sees a
// console (CONIN$/CONOUT$ + redirected stdin pipe) and reads our writes as
// if they were keyboard input. Plain RedirectStandardInput=true does NOT
// work for the s&box dedicated server — the engine refuses to consume input
// without a console attached. PTY-backed launch is the only mode that works
// (verified: timmybo5/sbox-server-manager uses node-pty for the same reason).
[SupportedOSPlatform("windows")]
public sealed class PseudoConsoleHost : IServerHost
{
    public IntPtr ChildProcessHandle { get; private set; } = IntPtr.Zero;
    public uint ChildProcessId { get; private set; }
    public Stream OutputStream { get; private set; } = null!;
    public Stream InputStream  { get; private set; } = null!;

    IntPtr _hPC = IntPtr.Zero;
    SafeFileHandle _hPipeOutRead = null!;
    SafeFileHandle _hPipeOutWrite = null!;
    SafeFileHandle _hPipeInRead = null!;
    SafeFileHandle _hPipeInWrite = null!;
    IntPtr _attrList = IntPtr.Zero;
    bool _disposed;

    public void Start(string exe, string args, string workingDir)
    {
        if (!CreatePipe(out _hPipeOutRead, out _hPipeOutWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe(out) failed err={Marshal.GetLastWin32Error()}");
        if (!CreatePipe(out _hPipeInRead,  out _hPipeInWrite,  IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe(in) failed err={Marshal.GetLastWin32Error()}");

        // ConPTY needs a viewport size; 200x50 is large enough that sbox status
        // padding doesn't wrap mid-line, which makes the line-based output pump
        // simpler than dealing with wrap-around column counters.
        var size = new COORD { X = 200, Y = 50 };
        int hr = CreatePseudoConsole(size, _hPipeInRead, _hPipeOutWrite, 0, out _hPC);
        if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole hr=0x{hr:x8}");

        // Once the child is launched, the pty owns the read-end-in / write-end-out;
        // close our duplicates so EOF propagates correctly when the child exits.
        _hPipeInRead.Dispose();
        _hPipeOutWrite.Dispose();

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        IntPtr lpSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        _attrList = Marshal.AllocHGlobal(lpSize);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref lpSize))
            throw new InvalidOperationException($"InitializeProcThreadAttributeList err={Marshal.GetLastWin32Error()}");

        if (!UpdateProcThreadAttribute(_attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException($"UpdateProcThreadAttribute err={Marshal.GetLastWin32Error()}");
        si.lpAttributeList = _attrList;

        string cmdLine = $"\"{exe}\" {args}";
        var pi = new PROCESS_INFORMATION();
        bool ok = CreateProcessW(
            null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
            EXTENDED_STARTUPINFO_PRESENT,
            IntPtr.Zero, workingDir, ref si, out pi);
        if (!ok)
            throw new InvalidOperationException($"CreateProcessW err={Marshal.GetLastWin32Error()}");

        ChildProcessHandle = pi.hProcess;
        ChildProcessId = pi.dwProcessId;
        CloseHandle(pi.hThread);

        // Wrap remaining pipe ends in FileStream so callers can read/write naturally.
        OutputStream = new FileStream(_hPipeOutRead, FileAccess.Read, 4096, isAsync: false);
        InputStream  = new FileStream(_hPipeInWrite, FileAccess.Write, 4096, isAsync: false);
    }

    public bool HasChildExited()
    {
        if (ChildProcessHandle == IntPtr.Zero) return true;
        return WaitForSingleObject(ChildProcessHandle, 0) == 0;
    }

    public int ChildExitCode()
    {
        if (ChildProcessHandle == IntPtr.Zero) return -1;
        return GetExitCodeProcess(ChildProcessHandle, out int code) ? code : -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { OutputStream?.Dispose(); } catch {}
        try { InputStream?.Dispose(); } catch {}
        if (_hPC != IntPtr.Zero) { try { ClosePseudoConsole(_hPC); } catch {}; _hPC = IntPtr.Zero; }
        if (_attrList != IntPtr.Zero) { try { DeleteProcThreadAttributeList(_attrList); Marshal.FreeHGlobal(_attrList); } catch {}; _attrList = IntPtr.Zero; }
        if (ChildProcessHandle != IntPtr.Zero) { try { CloseHandle(ChildProcessHandle); } catch {}; ChildProcessHandle = IntPtr.Zero; }
    }

    // -------- P/Invoke --------
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    [StructLayout(LayoutKind.Sequential)] public struct COORD { public short X; public short Y; }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFO {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, uint dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);
}
