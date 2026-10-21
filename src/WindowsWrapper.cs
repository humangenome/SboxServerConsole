using System.Text;

namespace SboxServerConsole;

// On Windows the server runs under a PowerShell parent so it gets a real console on
// the ConPTY (sbox-server.exe does not bind directly to a ConPTY host).
//
// The PowerShell script is fixed text. The program and its argument line (quoted by
// ChildArguments.JoinForWindows) reach it through two environment variables, and the
// script starts the program with Process.Start on the same console, so nothing from
// --child-args is ever read as PowerShell.
public static class WindowsWrapper
{
    public const string ExeVariable = "SSC_CHILD_EXE";
    public const string ArgsVariable = "SSC_CHILD_ARGS";

    public const string Script = """
        $ErrorActionPreference = 'Stop'
        $exe = $env:SSC_CHILD_EXE
        $argLine = $env:SSC_CHILD_ARGS
        Remove-Item Env:SSC_CHILD_EXE, Env:SSC_CHILD_ARGS -ErrorAction SilentlyContinue
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.Arguments = $argLine
        $psi.UseShellExecute = $false
        $p = [System.Diagnostics.Process]::Start($psi)
        $p.WaitForExit()
        exit $p.ExitCode
        """;

    // -EncodedCommand takes UTF-16 LE base64, so PowerShell reads the script exactly.
    public static readonly string EncodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));

    public static (string Exe, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env) Build(
        string powerShellExe, string childExe, IReadOnlyList<string> childArgs)
    {
        var args = new[] { "-NoProfile", "-NoLogo", "-EncodedCommand", EncodedScript };
        var env = new Dictionary<string, string>
        {
            [ExeVariable] = childExe,
            [ArgsVariable] = ChildArguments.JoinForWindows(childArgs),
        };
        return (powerShellExe, args, env);
    }
}
