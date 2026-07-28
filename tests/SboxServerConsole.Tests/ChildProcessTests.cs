using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace SboxServerConsole.Tests;

// End-to-end tests against a real supervised child process. The stand-in below is
// a POSIX shell script that behaves like a dedicated server console: it reads
// commands from stdin, runs them, writes results to stdout, and quits on "exit".
// That is the entire contract ServerProcess depends on.
//
// Skipped on Windows, where the child is wrapped in a ConPTY via PowerShell and
// there is no equivalent one-line stand-in. CI runs Linux.
public class ChildProcessTests : IDisposable
{
    const string StubScript = """
        #!/bin/sh
        # Stand-in dedicated-server console. Strips the CR the agent appends to every
        # command (a real .NET server gets that for free from Console.ReadLine).
        cr=$(printf '\r')
        while IFS= read -r line; do
          line=${line%$cr}
          [ -z "$line" ] && continue
          [ "$line" = "exit" ] && exit 0
          eval "$line"
        done

        """;

    readonly Scratch _stub = new();
    readonly string _stubPath;

    public ChildProcessTests()
    {
        _stubPath = _stub.File("console-stub.sh");
        File.WriteAllText(_stubPath, StubScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose() => _stub.Dispose();

    ApiHost? Host()
    {
        if (!Configs.CanSpawnShellChild) return null;
        var h = ApiHost.Create(
            "--exe", _stubPath,
            "--child-args", "",
            "--child-port", "27015",
            "--shutdown-command", "exit",
            "--restart-backoff-sec", "1");
        h.StartChild();
        return h;
    }

    [Fact]
    public async Task ExecuteCollectReturnsTheChildsOutput()
    {
        using var h = Host();
        if (h is null) return;

        var (status, body) = await h.Call("POST", "/execute?collect=1&wait_ms=2000",
            json: JsonSerializer.Serialize(new { cmd = "echo hello-from-child" }));
        Assert.Equal(200, status);

        var output = JsonDocument.Parse(body).RootElement.GetProperty("output").EnumerateArray().ToList();
        Assert.Contains(output, e => e.GetProperty("stream").GetString() == "input"
                                  && e.GetProperty("line").GetString() == "echo hello-from-child");
        Assert.Contains(output, e => e.GetProperty("stream").GetString() == "stdout"
                                  && e.GetProperty("line").GetString() == "hello-from-child");
    }

    [Fact]
    public async Task ExecuteWithoutCollectJustAcknowledges()
    {
        using var h = Host();
        if (h is null) return;

        var (status, body) = await h.Call("POST", "/execute", json: JsonSerializer.Serialize(new { cmd = "echo quiet" }));
        Assert.Equal(200, status);
        Assert.Equal("{\"ok\":true}", body);
        Assert.True(Wait.For(() => h.Buffer.Tail(50).Any(e => e.Line == "quiet")));
    }

    [Fact]
    public async Task ChatSubstitutesWhitespaceWithMiddleDot()
    {
        // s&box's command tokenizer splits at any Unicode whitespace even inside
        // quotes, so "say hello world" would only deliver "hello". Whitespace runs
        // are replaced with U+00B7 before the command goes to the child.
        using var h = Host();
        if (h is null) return;

        var (status, _) = await h.Call("POST", "/chat", json: JsonSerializer.Serialize(new { text = "hello   world  again" }));
        Assert.Equal(200, status);

        Assert.True(Wait.For(() => h.Buffer.Tail(100).Any(e => e.Stream == "input" && e.Line == "say hello·world·again")),
            "expected the middle-dot transcription on the wire");

        // The buffer keeps the human-readable original for the console view.
        Assert.Contains(h.Buffer.Tail(100), e => e.Stream == "chat" && e.Line == "Server: hello   world  again");
    }

    [Fact]
    public async Task SuppressRegexDropsFrameStatSpam()
    {
        using var h = Host();
        if (h is null) return;

        await h.Call("POST", "/execute?collect=1&wait_ms=1500",
            json: JsonSerializer.Serialize(new { cmd = "echo Physics 1.23ms, Network 4.56ms" }));
        await h.Call("POST", "/execute?collect=1&wait_ms=1500",
            json: JsonSerializer.Serialize(new { cmd = "echo a normal log line" }));

        var lines = h.Buffer.Tail(200);
        Assert.Contains(lines, e => e.Stream == "stdout" && e.Line == "a normal log line");
        Assert.DoesNotContain(lines, e => e.Stream == "stdout" && e.Line.StartsWith("Physics", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StructuredChatLinesBecomeChatEntries()
    {
        using var h = Host();
        if (h is null) return;

        await h.Call("POST", "/execute?collect=1&wait_ms=1500", json: JsonSerializer.Serialize(new
        {
            cmd = "echo 'SSCHAT {\"steamid\":\"76561198000000001\",\"name\":\"alice\",\"message\":\"hi there\"}'",
        }));

        Assert.True(Wait.For(() => h.Buffer.Tail(200).Any(e => e.Stream == "chat" && e.Line == "alice: hi there")),
            "SSCHAT payload should surface as a chat entry");
    }

    [Fact]
    public async Task StatusAndHealthSeeTheLiveChild()
    {
        using var h = Host();
        if (h is null) return;

        var s = await h.Json("GET", "/status");
        Assert.True(s.GetProperty("child_alive").GetBoolean());
        Assert.True(s.GetProperty("child_pid").GetInt32() > 0);

        var (_, healthBody) = await h.Call("GET", "/health", password: null);
        Assert.True(JsonDocument.Parse(healthBody).RootElement.GetProperty("child_alive").GetBoolean());
    }

    [Fact]
    public async Task LifecycleStopAndStart()
    {
        using var h = Host();
        if (h is null) return;

        var (stop, _) = await h.Call("POST", "/server/stop");
        Assert.Equal(200, stop);
        Assert.True(Wait.For(() => !h.Server.IsAlive), "child should have exited");

        var (stopAgain, stopAgainBody) = await h.Call("POST", "/server/stop");
        Assert.Equal(409, stopAgain);
        Assert.Contains("already stopped", stopAgainBody);

        var (start, _) = await h.Call("POST", "/server/start");
        Assert.Equal(200, start);
        Assert.True(Wait.For(() => h.Server.IsAlive), "child should have restarted");

        var (startAgain, startAgainBody) = await h.Call("POST", "/server/start");
        Assert.Equal(409, startAgain);
        Assert.Contains("already running", startAgainBody);

        var events = h.AuditEvents().Select(e => e.GetProperty("event").GetString()).ToList();
        Assert.Contains("server_stop", events);
        Assert.Contains("server_start", events);
    }

    [Fact]
    public async Task LifecycleRestartBringsTheChildBack()
    {
        using var h = Host();
        if (h is null) return;
        int firstPid = h.Server.ChildPid;

        var (restart, _) = await h.Call("POST", "/server/restart");
        Assert.Equal(200, restart);

        // The supervisor waits --restart-backoff-sec before respawning.
        Assert.True(Wait.For(() => h.Server.IsAlive && h.Server.ChildPid != firstPid, timeoutMs: 30000),
            "auto-restart should have produced a new child");
        Assert.Contains("server_restart", h.AuditEvents().Select(e => e.GetProperty("event").GetString()));
    }

    [Fact]
    public void RconExecReachesTheChild()
    {
        using var h = Host();
        if (h is null) return;

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, h.Config.RconPort);
        client.NoDelay = true;
        client.ReceiveTimeout = 15000;
        using var net = client.GetStream();

        RconWire.Send(net, 1, RconWire.Auth, ApiHost.Password);
        Assert.True(RconWire.TryRead(net, out _, out _, out _));
        Assert.True(RconWire.TryRead(net, out int authId, out int authType, out _));
        Assert.Equal(RconWire.AuthResponse, authType);
        Assert.Equal(1, authId);

        RconWire.Send(net, 2, RconWire.ExecCommand, "echo rcon-round-trip");
        Assert.True(RconWire.TryRead(net, out int id, out int type, out string body));
        Assert.Equal(RconWire.ResponseValue, type);
        Assert.Equal(2, id);
        Assert.Contains("rcon-round-trip", body);
        // Echoed input lines are filtered out of the RCON response body.
        Assert.DoesNotContain("echo rcon-round-trip", body);
    }

    [Fact]
    public void ScheduledJobFiresAgainstTheChild()
    {
        using var h = Host();
        if (h is null) return;
        h.Scheduler.Start();

        Assert.True(h.Scheduler.TryAdd("tick", "@every 1s", "echo scheduled-tick", out string err), err);
        Assert.True(Wait.For(() => h.Buffer.Tail(200).Any(e => e.Line == "scheduled-tick"), timeoutMs: 15000),
            "the scheduled command never reached the child");

        var job = Assert.Single(h.Scheduler.All());
        Assert.True(job.RunCount >= 1);
        Assert.NotNull(job.LastRunAt);

        // The run history is written back to disk, not just held in memory.
        var onDisk = JsonDocument.Parse(File.ReadAllText(h.Config.SchedulerPath));
        Assert.True(onDisk.RootElement.GetProperty("jobs")[0].GetProperty("run_count").GetInt64() >= 1);
        Assert.Contains("scheduler_fire", h.AuditEvents().Select(e => e.GetProperty("event").GetString()));
    }
}
