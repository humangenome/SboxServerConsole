using System.Text;
using Xunit;

namespace SboxServerConsole.Tests;

// --child-args is split once by the agent (double quotes group words, nothing else is
// special) and handed to the operating system as a list. These tests pin the split,
// the Windows quoting, the fixed Windows wrapper script, and, end to end on Linux,
// that a server receives symbols exactly as written.
public class LaunchArgumentTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("+port 27015", new[] { "+port", "27015" })]
    [InlineData("+hostname \"My Server\" +port 27015", new[] { "+hostname", "My Server", "+port", "27015" })]
    [InlineData("  +a   b\t+c  ", new[] { "+a", "b", "+c" })]
    [InlineData("+hostname \"\" +port 1", new[] { "+hostname", "", "+port", "1" })]
    [InlineData("a\"b c\"d", new[] { "ab cd" })]
    [InlineData("+hostname \"never closed +port 1", new[] { "+hostname", "never closed +port 1" })]
    [InlineData("'single' it's", new[] { "'single'", "it's" })]
    [InlineData("C:\\games\\ \\\\server\\share", new[] { "C:\\games\\", "\\\\server\\share" })]
    [InlineData("+hostname \"Fun & Games | $(x) ; `y` {z} <w> %v% !u!\"", new[] { "+hostname", "Fun & Games | $(x) ; `y` {z} <w> %v% !u!" })]
    [InlineData("$(x) ; y & z | w", new[] { "$(x)", ";", "y", "&", "z", "|", "w" })]
    public void SplitGroupsDoubleQuotedWordsAndNothingElse(string line, string[] expected)
    {
        Assert.Equal(expected, ChildArguments.Split(line));
    }

    // The C runtime's command-line parser (UCRT rules, the same CommandLineToArgvW follows
    // for the program's arguments), written out so the quoting can be checked both ways.
    static List<string> ParseWindowsCommandLine(string s)
    {
        var args = new List<string>();
        int i = 0;
        while (true)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            if (i >= s.Length) break;
            var cur = new StringBuilder();
            bool inQuotes = false;
            while (i < s.Length)
            {
                int bs = 0;
                while (i < s.Length && s[i] == '\\') { bs++; i++; }
                if (i < s.Length && s[i] == '"')
                {
                    cur.Append('\\', bs / 2);
                    if (bs % 2 == 1) { cur.Append('"'); i++; continue; }
                    if (inQuotes && i + 1 < s.Length && s[i + 1] == '"') { cur.Append('"'); i += 2; continue; }
                    inQuotes = !inQuotes; i++; continue;
                }
                cur.Append('\\', bs);
                if (i >= s.Length) break;
                if (!inQuotes && (s[i] == ' ' || s[i] == '\t')) break;
                cur.Append(s[i]); i++;
            }
            args.Add(cur.ToString());
        }
        return args;
    }

    [Fact]
    public void WindowsQuotingReadsBackTheSameList()
    {
        var cases = new[]
        {
            "plain", "two words", "", "trailing\\", "C:\\Program Files\\Game\\", "with \"quotes\" inside",
            "back\\\\slash\\\" mixed", "\\\"", "tab\there", "$(touch x) ; & | ` ' %PATH% !x! ^ < >",
            "+hostname", "Caf\u00e9 \u65e5\u672c \u201ccurly\u201d",
        };
        var line = ChildArguments.JoinForWindows(cases);
        Assert.Equal(cases, ParseWindowsCommandLine(line));
        foreach (var c in cases)
            Assert.Equal(new[] { c }, ParseWindowsCommandLine(ChildArguments.JoinForWindows(new[] { c })));
    }

    [Fact]
    public void WindowsQuotingLeavesOrdinaryArgumentsAsTheyWere()
    {
        // The line a server got before for ordinary input stays byte for byte the same.
        var args = ChildArguments.Split("+hostname \"My s&box Server\" +port 27015 +game facepunch.sandbox");
        Assert.Equal("+hostname \"My s&box Server\" +port 27015 +game facepunch.sandbox", ChildArguments.JoinForWindows(args));
    }

    [Fact]
    public void WindowsWrapperScriptIsFixedAndCarriesNoArguments()
    {
        const string payload = "+hostname \"a $(New-Item x) ; b\" +port 27015 `c` %d%";
        var a = WindowsWrapper.Build("powershell.exe", @"C:\srv\sbox-server.exe", ChildArguments.Split(payload));
        var b = WindowsWrapper.Build("powershell.exe", @"C:\other\sbox-server.exe", ChildArguments.Split("+port 1"));

        // Same PowerShell command line whatever the server and its arguments are.
        Assert.Equal(a.Args, b.Args);
        Assert.Equal(new[] { "-NoProfile", "-NoLogo", "-EncodedCommand", WindowsWrapper.EncodedScript }, a.Args);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(a.Args[3]));
        Assert.Equal(WindowsWrapper.Script, script);
        Assert.DoesNotContain("New-Item", script);
        Assert.DoesNotContain("sbox-server", script);

        // The program and the quoted argument line travel in the environment.
        Assert.Equal(@"C:\srv\sbox-server.exe", a.Env[WindowsWrapper.ExeVariable]);
        Assert.Equal(ChildArguments.Split(payload), ParseWindowsCommandLine(a.Env[WindowsWrapper.ArgsVariable]));
    }

    [Fact]
    public void LinuxShellScriptIsFixed()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return;
        Assert.Equal("exec \"$0\" \"$@\" 0<>\"$SSC_PTY\" 1>&0 2>&0", LinuxServerHost.PtyScript);
        Assert.Equal("exec \"$0\" \"$@\" 2>&1", LinuxServerHost.PipeScript);
    }

    [Fact]
    public void TheServerReceivesSymbolsExactlyAsWritten()
    {
        if (!Configs.CanSpawnShellChild || OperatingSystem.IsWindows()) return;
        using var s = new Scratch();
        string argsFile = s.File("received-args.txt");
        string marker1 = s.File("ran-1");
        string marker2 = s.File("ran-2");
        string stub = s.File("print-args-stub.sh");
        File.WriteAllText(stub, $$"""
            #!/bin/sh
            printf '%s\n' "$@" > '{{argsFile}}'
            cr=$(printf '\r')
            while IFS= read -r line; do
              line=${line%$cr}
              [ "$line" = "exit" ] && exit 0
            done

            """);
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string name = $"Fun $(touch {marker1}) ; touch {marker2} & echo x | y `id` 'q'";
        string childArgs = $"+hostname \"{name}\" +port 27015 it's back\\slash";
        using var h = ApiHost.Create("--exe", stub, "--child-args", childArgs, "--child-port", "27015", "--shutdown-command", "exit");
        h.StartChild();

        Assert.True(Wait.For(() => File.Exists(argsFile) && File.ReadAllText(argsFile).Contains("slash")), "the stand-in server never wrote its arguments");
        var received = File.ReadAllText(argsFile).TrimEnd('\n').Split('\n');
        Assert.Equal(new[] { "+hostname", name, "+port", "27015", "it's", "back\\slash" }, received);
        Assert.False(File.Exists(marker1), "a $( ) in the arguments ran");
        Assert.False(File.Exists(marker2), "a ; in the arguments ran");
    }
}
