using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SboxServerConsole.Tests;

// Input validation, password-guessing limits, what the logs browser will serve,
// the chat broadcast's honesty, Discord mention safety and stream replay.
public class HardeningTests
{
    [Theory]
    [InlineData("STEAM_0:1:12345")]
    [InlineData("76561198000000001\nquit")]
    [InlineData("123")]
    public async Task BansAndAllowsRejectIdsThatCouldNeverMatch(string id)
    {
        using var h = ApiHost.Create();
        var body = JsonSerializer.Serialize(new { steamid = id, reason = "x", note = "x" });
        var (ban, banBody) = await h.Call("POST", "/bans", json: body);
        Assert.Equal(400, ban);
        Assert.Contains("SteamID64", banBody);
        var (allow, _) = await h.Call("POST", "/allows", json: body);
        Assert.Equal(400, allow);
        Assert.Empty((await h.Json("GET", "/bans")).GetProperty("bans").EnumerateArray());
        Assert.False((await h.Json("GET", "/allows")).GetProperty("enforced").GetBoolean());
    }

    [Fact]
    public async Task SchedulerPostWithoutAnIdReturnsTheGeneratedOne()
    {
        using var h = ApiHost.Create();
        var r = await h.Json("POST", "/scheduler", JsonSerializer.Serialize(new { schedule = "@every 1h", command = "status" }));
        string id = r.GetProperty("id").GetString()!;
        Assert.StartsWith("job-", id);
        var job = Assert.Single((await h.Json("GET", "/scheduler")).GetProperty("jobs").EnumerateArray().ToList());
        Assert.Equal(id, job.GetProperty("id").GetString());
    }

    [Fact]
    public void ThrottleLocksOutAfterRepeatedFailuresAndSparesLoopback()
    {
        var t = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var th = new AuthThrottle(() => t);
        var remote = IPAddress.Parse("203.0.113.7");
        for (int i = 0; i < AuthThrottle.MaxFailures - 1; i++) Assert.False(th.RecordFailure(remote));
        Assert.False(th.IsLockedOut(remote));
        Assert.True(th.RecordFailure(remote));
        Assert.True(th.IsLockedOut(remote));
        Assert.False(th.IsLockedOut(IPAddress.Parse("203.0.113.8")));

        t = t + AuthThrottle.Lockout + TimeSpan.FromSeconds(1);
        Assert.False(th.IsLockedOut(remote));

        for (int i = 0; i < 50; i++) th.RecordFailure(IPAddress.Loopback);
        Assert.False(th.IsLockedOut(IPAddress.Loopback));
        Assert.False(th.IsLockedOut(IPAddress.Parse("::ffff:127.0.0.1")));
    }

    [Fact]
    public void FailuresSpreadOverTimeDoNotLockOut()
    {
        var t = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var th = new AuthThrottle(() => t);
        var remote = IPAddress.Parse("203.0.113.9");
        for (int i = 0; i < 30; i++)
        {
            th.RecordFailure(remote);
            t = t.AddSeconds(20);
        }
        Assert.False(th.IsLockedOut(remote));
    }

    [Fact]
    public async Task DotFilesAreNeitherListedNorServed()
    {
        using var scratch = new Scratch();
        var dir = Path.Combine(scratch.Dir, "game");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "server.log"), "line\n");
        File.WriteAllText(Path.Combine(dir, ".ssc_config.json"), "{\"rcon-password\":\"secret\"}");

        using var h = ApiHost.Create("--logs-dir", dir);
        var files = (await h.Json("GET", "/logs")).GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "server.log" }, files);
        var (status, body) = await h.Call("GET", "/logs/.ssc_config.json");
        Assert.Equal(404, status);
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public void RconRefusesCommandsWithLineBreaks()
    {
        using var h = ApiHost.Create();
        using var tcp = new TcpClient("127.0.0.1", h.Config.RconPort);
        var net = tcp.GetStream();
        Rcon.Send(net, 1, 3, ApiHost.Password);
        Rcon.Read(net); // empty RESPONSE_VALUE
        var (authId, _, _) = Rcon.Read(net);
        Assert.Equal(1, authId);
        Rcon.Send(net, 2, 2, "status\nquit");
        var (id, _, body) = Rcon.Read(net);
        Assert.Equal(2, id);
        Assert.Contains("line breaks", body);
        Assert.DoesNotContain(h.AuditEvents(), e => e.GetProperty("event").GetString() == "rcon_execute");
    }

    [Fact]
    public void AnRconClientThatNeverAuthenticatesIsDropped()
    {
        using var h = ApiHost.Create();
        using var tcp = new TcpClient("127.0.0.1", h.Config.RconPort);
        tcp.ReceiveTimeout = 20000;
        var net = tcp.GetStream();
        var started = DateTime.UtcNow;
        var buf = new byte[16];
        int n;
        try { n = net.Read(buf, 0, buf.Length); } catch (IOException) { n = 0; }
        Assert.Equal(0, n);
        var waited = DateTime.UtcNow - started;
        Assert.True(waited < TimeSpan.FromSeconds(18), $"the idle unauthenticated client was held for {waited}");
    }

    [Fact]
    public void DiscordQuoteCannotBreakOutOfInlineCode()
    {
        var q = DiscordWebhook.Quote("a`@everyone`b\nc");
        Assert.Equal("`a'@everyone'b c`", q);
        Assert.Equal(1, q.Count(c => c == '`') / 2);
    }

    [Fact]
    public async Task DiscordPayloadBlocksMentionsAndAFailingWebhookIsReported()
    {
        int port = Ports.Free();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        string? payload = null;
        var serve = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var ctx = await listener.GetContextAsync();
                using (var sr = new StreamReader(ctx.Request.InputStream)) payload = await sr.ReadToEndAsync();
                ctx.Response.StatusCode = i == 0 ? 404 : 204;
                ctx.Response.Close();
            }
        });
        var logged = new List<string>();
        using var hook = new DiscordWebhook($"http://127.0.0.1:{port}/hook", logged.Add);
        await hook.SendAsync("Player joined", DiscordWebhook.Quote("@everyone") + " connected.", DiscordWebhook.ColorBlue);
        Assert.Contains("\"allowed_mentions\":{\"parse\":[]}", payload);
        Assert.Contains(logged, l => l.Contains("HTTP 404"));
        await hook.SendAsync("again", "x", DiscordWebhook.ColorBlue);
        Assert.Contains(logged, l => l.Contains("delivering again"));
        await serve.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ChatReportsAGameWithoutASayCommand()
    {
        if (!Configs.CanSpawnShellChild) return;
        using var s = new Scratch();
        var stub = s.File("nosay.sh");
        File.WriteAllText(stub, "#!/bin/sh\ncr=$(printf '\\r')\nwhile IFS= read -r line; do\n  line=${line%$cr}\n  case \"$line\" in\n    say*) echo \"08:00:00 Generic  Unknown Command 'say'\" ;;\n    exit) exit 0 ;;\n  esac\ndone\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var h = ApiHost.Create("--exe", stub, "--child-args", "", "--child-port", "27015", "--shutdown-command", "exit");
        h.StartChild();
        var (status, body) = await h.Call("POST", "/chat", json: "{\"text\":\"hello there\"}");
        Assert.Equal(422, status);
        Assert.Contains("no say command", body);
        Assert.DoesNotContain(h.Buffer.Tail(50), e => e.Stream == "chat");
    }

    [Fact]
    public async Task StreamReplaysExactlyWhatAReconnectingClientMissed()
    {
        using var h = ApiHost.Create();
        for (int i = 1; i <= 5; i++) h.Buffer.Append("stdout", $"line {i}");
        long third = h.Buffer.Tail(5)[2].SeqNo;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{h.BaseUrl}/stream");
        req.Headers.Add("X-RCON-Password", ApiHost.Password);
        req.Headers.Add("Last-Event-ID", third.ToString());
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        var got = new List<string>();
        var ids = new List<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (got.Count < 2 && !cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;
            if (line.StartsWith("id: ")) ids.Add(line[4..]);
            if (line.StartsWith("data: ")) got.Add(JsonDocument.Parse(line[6..]).RootElement.GetProperty("line").GetString()!);
        }
        Assert.Equal(new[] { "line 4", "line 5" }, got);
        Assert.Equal(new[] { (third + 1).ToString(), (third + 2).ToString() }, ids);
    }
}

static class Rcon
{
    public static void Send(NetworkStream net, int id, int type, string body)
    {
        var b = Encoding.UTF8.GetBytes(body);
        var pkt = new byte[4 + 4 + 4 + b.Length + 2];
        BitConverter.GetBytes(4 + 4 + b.Length + 2).CopyTo(pkt, 0);
        BitConverter.GetBytes(id).CopyTo(pkt, 4);
        BitConverter.GetBytes(type).CopyTo(pkt, 8);
        b.CopyTo(pkt, 12);
        net.Write(pkt);
    }

    public static (int Id, int Type, string Body) Read(NetworkStream net)
    {
        var hdr = new byte[4];
        net.ReadExactly(hdr);
        int size = BitConverter.ToInt32(hdr);
        var buf = new byte[size];
        net.ReadExactly(buf);
        int end = Array.IndexOf(buf, (byte)0, 8);
        return (BitConverter.ToInt32(buf, 0), BitConverter.ToInt32(buf, 4), Encoding.UTF8.GetString(buf, 8, end - 8));
    }
}
