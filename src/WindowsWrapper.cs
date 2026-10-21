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

    // Only .NET calls, no cmdlets: a cmdlet makes PowerShell load its module from
    // PSModulePath, and an agent started from PowerShell 7 hands Windows PowerShell a
    // PSModulePath it cannot load those modules from.
    public const string Script = """
        $ErrorActionPreference = 'Stop'
        $exe = [Environment]::GetEnvironmentVariable('SSC_CHILD_EXE')
        $argLine = [Environment]::GetEnvironmentVariable('SSC_CHILD_ARGS')
        [Environment]::SetEnvironmentVariable('SSC_CHILD_EXE', $null)
        [Environment]::SetEnvironmentVariable('SSC_CHILD_ARGS', $null)
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
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
