using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace SboxServerConsole.Tests;

// Why the child exits decides what happens next. Before 1.4.0 every exit that was
// not a crash ended supervision, and Program.cs ended the agent with it, so
// POST /server/stop took the whole agent down (and /server/start could never be
// reached), and /server/restart under --no-auto-restart killed the agent instead of
// restarting the server. The in-process ApiHost never wired that exit, which is why
// the old lifecycle tests passed. These check the supervisor directly and drive the
// real agent binary for the parts only Program.cs decides.
public class LifecycleTests : IDisposable
{
    const string Stub = """
        #!/bin/sh
        cr=$(printf '\r')
        if [ -t 1 ]; then echo "tty=yes"; else echo "tty=no"; fi
        while IFS= read -r line; do
          line=${line%$cr}
          [ -z "$line" ] && continue
          [ "$line" = "quit" ] && exit 0
          eval "$line"
        done

        """;

    readonly Scratch _s = new();
    readonly string _stub;
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public LifecycleTests()
    {
        _stub = _s.File("stub.sh");
        File.WriteAllText(_stub, Stub);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => _s.Dispose();

    (CliConfig Cfg, MessageBuffer Buf, ServerProcess Srv) Supervisor(params string[] extra)
    {
        var args = new List<string> { "--exe", _stub, "--child-args", "", "--child-port", "27015", "--restart-backoff-sec", "1" };
        args.AddRange(extra);
        var cfg = Configs.Parse(_s.Dir, args.ToArray());
        var buf = new MessageBuffer(cfg.BufferSize);
        return (cfg, buf, new ServerProcess(cfg, buf));
    }

    [Fact]
    public void StopLeavesSupervisionRunningAndStartBringsTheServerBack()
    {
        if (!Configs.CanSpawnShellChild) return;
        var (_, buf, srv) = Supervisor();
        using var _b = buf; using var _s2 = srv;
        bool ended = false;
        srv.OnSupervisorExit += () => ended = true;
        srv.Start();
        Assert.True(Wait.For(() => srv.IsAlive));

        srv.Stop(TimeSpan.FromSeconds(10));
        Assert.True(Wait.For(() => !srv.IsAlive), "the child should have stopped");
        Thread.Sleep(1500);
        Assert.False(ended, "a requested stop must not end supervision");
        Assert.False(srv.IsAlive, "a requested stop must not be auto-restarted");

        Assert.True(srv.TryStartIfStopped());
        Assert.True(Wait.For(() => srv.IsAlive), "start should bring the server back");
    }

    [Fact]
    public void RestartWorksWithAutoRestartOff()
    {
        if (!Configs.CanSpawnShellChild) return;
        var (_, buf, srv) = Supervisor("--no-auto-restart");
        using var _b = buf; using var _s2 = srv;
        bool ended = false;
        srv.OnSupervisorExit += () => ended = true;
        srv.Start();
        Assert.True(Wait.For(() => srv.IsAlive));
        int first = srv.ChildPid;

        srv.Restart(TimeSpan.FromSeconds(10));
        Assert.True(Wait.For(() => srv.IsAlive && srv.ChildPid != first, timeoutMs: 20000), "restart should start a new child");
        Assert.False(ended);
    }

    [Fact]
    public void RequestedRestartsDoNotSpendTheCrashBudget()
    {
        if (!Configs.CanSpawnShellChild) return;
        var (_, buf, srv) = Supervisor("--restart-max-attempts", "1");
        using var _b = buf; using var _s2 = srv;
        srv.Start();
        Assert.True(Wait.For(() => srv.IsAlive));
        for (int i = 0; i < 3; i++)
        {
            int pid = srv.ChildPid;
            srv.Restart(TimeSpan.FromSeconds(10));
            Assert.True(Wait.For(() => srv.IsAlive && srv.ChildPid != pid, timeoutMs: 20000), $"restart {i + 1} was refused");
        }
        Assert.Equal(0, srv.RestartAttemptsInWindow);
        Assert.False(srv.GaveUp);
    }

    [Fact]
    public void ACrashWithAutoRestartOffEndsSupervisionAsGivenUp()
    {
        if (!Configs.CanSpawnShellChild) return;
        var (_, buf, srv) = Supervisor("--no-auto-restart");
        using var _b = buf; using var _s2 = srv;
        bool ended = false, exited = false;
        srv.OnSupervisorExit += () => ended = true;
        srv.OnChildExited += () => exited = true;
        srv.Start();
        Assert.True(Wait.For(() => srv.IsAlive));

        Assert.True(srv.TrySendCommand("exit 3"));
        Assert.True(Wait.For(() => ended), "supervision should end");
        Assert.True(srv.GaveUp);
        Assert.True(exited);
        Assert.Equal(3, srv.LastExitCode);
    }

    [Fact]
    public void CommandsWithControlCharactersAreRefused()
    {
        Assert.False(ServerProcess.IsSendableCommand("say hi\nquit"));
        Assert.False(ServerProcess.IsSendableCommand("say hi\rquit"));
        Assert.False(ServerProcess.IsSendableCommand("\u0003"));
        Assert.False(ServerProcess.IsSendableCommand("   "));
        Assert.True(ServerProcess.IsSendableCommand("say hello·world"));
    }

    [Fact]
    public void OnLinuxTheChildRunsOnATerminal()
    {
        if (!OperatingSystem.IsLinux() || !Configs.CanSpawnShellChild) return;
        var (_, buf, srv) = Supervisor();
        using var _b = buf; using var _s2 = srv;
        srv.Start();
        Assert.True(Wait.For(() => buf.Tail(50).Any(e => e.Line == "tty=yes" || e.Line == "tty=no")));
        Assert.Contains(buf.Tail(50), e => e.Line == "tty=yes");
    }

    [Fact]
    public void TerminalEchoIsNotRepeatedIntoTheConsole()
    {
        if (!OperatingSystem.IsLinux() || !Configs.CanSpawnShellChild) return;
        var (_, buf, srv) = Supervisor();
        using var _b = buf; using var _s2 = srv;
        srv.Start();
        Assert.True(Wait.For(() => buf.Tail(50).Any(e => e.Line == "tty=yes")));

        Assert.True(srv.TrySendCommand("echo marker-4711"));
        Assert.True(Wait.For(() => buf.Tail(50).Any(e => e.Stream == "stdout" && e.Line == "marker-4711")));
        Assert.DoesNotContain(buf.Tail(50), e => e.Stream == "stdout" && e.Line == "echo marker-4711");
    }

    [Fact]
    public void CursorPositionQueriesAreAnswered()
    {
        if (!OperatingSystem.IsLinux() || !Configs.CanSpawnShellChild) return;
        // The s&box console asks for the cursor position on every redraw; with no
        // answer its own input handling eats the typed command.
        var probe = _s.File("probe.sh");
        File.WriteAllText(probe, "#!/bin/sh\nstty -icanon -echo min 0 time 30\nprintf '\\033[6n'\nr=$(dd bs=1 count=7 2>/dev/null | od -An -c | tr -d ' \\n')\necho \"reply=$r\"\nsleep 5\n");
        File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var cfg = Configs.Parse(_s.Dir, "--exe", probe, "--child-args", "", "--child-port", "27015", "--no-auto-restart");
        using var buf = new MessageBuffer(cfg.BufferSize);
        using var srv = new ServerProcess(cfg, buf);
        srv.Start();
        Assert.True(Wait.For(() => buf.Tail(50).Any(e => e.Line.StartsWith("reply=")), timeoutMs: 10000), "the probe never printed its reply");
        Assert.Contains(buf.Tail(50), e => e.Line == "reply=033[24;1R");
    }

    // ---- the real agent binary ----

    sealed class Agent : IDisposable
    {
        public Process Proc = null!;
        public int Port;
        public void Dispose()
        {
            try { if (!Proc.HasExited) Proc.Kill(entireProcessTree: true); } catch { }
            Proc.Dispose();
        }
    }

    Agent StartAgent(string exe, params string[] extra)
    {
        string bin = Path.Combine(AppContext.BaseDirectory, "SboxServerConsole");
        Assert.True(File.Exists(bin), $"agent binary not found at {bin}");
        var a = new Agent { Port = Ports.Free() };
        var psi = new ProcessStartInfo(bin)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var x in new[] { "--exe", exe, "--game-dir", _s.Dir, "--child-args", "", "--child-port", "27015",
                     "--listen-port", a.Port.ToString(), "--rcon-port", "0", "--rcon-password", "pw", "--query-poll-sec", "0" })
            psi.ArgumentList.Add(x);
        foreach (var x in extra) psi.ArgumentList.Add(x);
        a.Proc = Process.Start(psi)!;
        a.Proc.OutputDataReceived += (_, _) => { };
        a.Proc.ErrorDataReceived += (_, _) => { };
        a.Proc.BeginOutputReadLine();
        a.Proc.BeginErrorReadLine();
        return a;
    }

    static async Task<(int, string)> Post(Agent a, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{a.Port}{path}")
        {
            Content = new StringContent("{}"),
        };
        req.Headers.Add("X-RCON-Password", "pw");
        using var r = await Client.SendAsync(req);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    static JsonElement? Health(Agent a)
    {
        try { return JsonDocument.Parse(Client.GetStringAsync($"http://127.0.0.1:{a.Port}/health").GetAwaiter().GetResult()).RootElement.Clone(); }
        catch { return null; }
    }

    [Fact]
    public async Task TheAgentSurvivesServerStopAndCanStartItAgain()
    {
        if (!Configs.CanSpawnShellChild) return;
        using var a = StartAgent(_stub);
        Assert.True(Wait.For(() => Health(a)?.GetProperty("child_alive").GetBoolean() == true, timeoutMs: 20000), "agent did not come up");

        var (stop, stopBody) = await Post(a, "/server/stop");
        Assert.Equal(200, stop);
        Assert.Contains("\"ok\":true", stopBody);
        Assert.True(Wait.For(() => Health(a)?.GetProperty("child_alive").GetBoolean() == false), "child should be stopped");
        Thread.Sleep(1000);
        Assert.False(a.Proc.HasExited, "the agent exited after /server/stop");

        var (start, _) = await Post(a, "/server/start");
        Assert.Equal(200, start);
        Assert.True(Wait.For(() => Health(a)?.GetProperty("child_alive").GetBoolean() == true), "child should be running again");

        Process.Start("kill", $"-TERM {a.Proc.Id}")!.WaitForExit();
        Assert.True(a.Proc.WaitForExit(30000), "agent did not exit on SIGTERM");
        Assert.Equal(0, a.Proc.ExitCode);
    }

    [Fact]
    public void AnAgentWhoseServerKeepsCrashingExitsNonZero()
    {
        if (!Configs.CanSpawnShellChild) return;
        var crasher = _s.File("crash.sh");
        File.WriteAllText(crasher, "#!/bin/sh\necho starting\nsleep 1\nexit 7\n");
        File.SetUnixFileMode(crasher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var a = StartAgent(crasher, "--restart-max-attempts", "1", "--restart-backoff-sec", "1");
        Assert.True(a.Proc.WaitForExit(60000), "agent should give up and exit");
        Assert.Equal(4, a.Proc.ExitCode);
    }
}
