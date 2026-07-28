using System.Text;
using System.Text.Json;
using Xunit;

namespace SboxServerConsole.Tests;

// HTTP surface: auth, the routes the panel and third-party clients depend on, the
// documented status codes, and the payload keys clients parse.
public class HttpApiTests
{
    // ---- auth ----

    [Fact]
    public async Task PublicRoutesNeedNoPassword()
    {
        using var h = ApiHost.Create();
        var (health, healthBody) = await h.Call("GET", "/health", password: null);
        Assert.Equal(200, health);
        Assert.True(JsonDocument.Parse(healthBody).RootElement.GetProperty("ok").GetBoolean());

        // /version is served alongside /health without auth. Third-party monitoring
        // and upgrade checks probe it unauthenticated; keep it public.
        var (version, _) = await h.Call("GET", "/version", password: null);
        Assert.Equal(200, version);

        var (dash, _) = await h.Call("GET", "/", password: null);
        Assert.Equal(200, dash);
    }

    [Fact]
    public async Task AuthenticatedRoutesRejectMissingAndWrongPasswords()
    {
        using var h = ApiHost.Create();
        foreach (var path in new[] { "/history", "/status", "/players", "/bans", "/allows", "/scheduler", "/logs" })
        {
            var (missing, _) = await h.Call("GET", path, password: null);
            Assert.True(missing == 401, $"{path} without a password -> {missing}");
            var (wrong, _) = await h.Call("GET", path, password: "nope");
            Assert.True(wrong == 401, $"{path} with a bad password -> {wrong}");
            // Same length as the real password, different content: guards the
            // constant-time comparison against a length-only check.
            var (nearMiss, _) = await h.Call("GET", path, password: new string('x', ApiHost.Password.Length));
            Assert.True(nearMiss == 401, $"{path} with a same-length bad password -> {nearMiss}");
        }
    }

    [Fact]
    public async Task BearerHeaderAndQueryStringAreAcceptedToo()
    {
        using var h = ApiHost.Create();
        var (bearer, _) = await h.Call("GET", "/status", bearer: true);
        Assert.Equal(200, bearer);

        // EventSource cannot set headers, so ?password= is the documented fallback.
        var (query, _) = await h.Call("GET", $"/status?password={ApiHost.Password}", password: null);
        Assert.Equal(200, query);
    }

    [Fact]
    public async Task NoRconPasswordConfiguredMeans503NotOpenAccess()
    {
        using var h = ApiHost.Create("--rcon-password", "");
        var (status, body) = await h.Call("GET", "/status", password: null);
        Assert.Equal(503, status);
        Assert.Contains("rcon_password not configured", body);

        // Supplying anything at all must not open the door either.
        var (guessed, _) = await h.Call("GET", "/status", password: "");
        Assert.Equal(503, guessed);
    }

    // ---- the sidecar compatibility key ----

    [Fact]
    public async Task VersionKeepsTheSidecarKey()
    {
        // Pre-1.0 shipped as "sidecar" and existing clients key off it. Renaming the
        // key to something tidier breaks every deployed console integration.
        using var h = ApiHost.Create();
        var v = await h.Json("GET", "/version");
        Assert.Equal("SboxServerConsole", v.GetProperty("sidecar").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", v.GetProperty("version").GetString());
        Assert.True(v.TryGetProperty("child_pid", out _));
        Assert.False(v.GetProperty("child_alive").GetBoolean());
    }

    [Fact]
    public void SidecarKeyIsStillInTheSource()
    {
        // Belt and braces: the wire key lives in a raw JSON literal, so a careless
        // rename would not break compilation.
        Assert.Contains("\"sidecar\":\"SboxServerConsole\"", Res.Read("src.HttpApi.cs"));
    }

    // ---- empty-body POSTs ----

    [Fact]
    public async Task LifecycleRoutesAcceptAZeroLengthBody()
    {
        // Windows HTTP.sys answers 411 to a POST that carries neither Content-Length
        // nor chunked framing, before the request ever reaches the agent — hence the
        // documented "send Content-Length: 0" rule. The agent itself must be happy
        // with an empty body, which is what this asserts.
        using var h = ApiHost.Create();
        var (stop, stopBody) = await h.Call("POST", "/server/stop");
        Assert.Equal(409, stop); // no child running
        Assert.Contains("already stopped", stopBody);

        // ...and equally happy with "{}", the cross-client workaround.
        var (stop2, _) = await h.Call("POST", "/server/stop", json: "{}");
        Assert.Equal(409, stop2);
    }

    [Fact]
    public void The411QuirkStaysDocumented()
    {
        // If someone "cleans up" this note, clients start dropping Content-Length and
        // Windows installs break with a status code the agent never emitted.
        var api = Res.Read("docs.api.md");
        Assert.Contains("411", api);
        Assert.Contains("Content-Length: 0", api);
        Assert.Contains("HTTP.sys", api);

        var readme = Res.Read("README.md");
        Assert.Contains("411", readme);
        Assert.Contains("Content-Length: 0", readme);
    }

    // ---- execute ----

    [Fact]
    public async Task ExecuteRejectsBadRequests()
    {
        using var h = ApiHost.Create();

        var (wrongMethod, _) = await h.Call("GET", "/execute");
        Assert.Equal(405, wrongMethod);

        var (badJson, _) = await h.Call("POST", "/execute", json: "{not json");
        Assert.Equal(400, badJson);

        var (noCmd, noCmdBody) = await h.Call("POST", "/execute", json: "{}");
        Assert.Equal(400, noCmd);
        Assert.Contains("cmd required", noCmdBody);

        var (newline, newlineBody) = await h.Call("POST", "/execute", json: JsonSerializer.Serialize(new { cmd = "say hi\nquit" }));
        Assert.Equal(400, newline);
        Assert.Contains("newline", newlineBody);

        var (carriage, _) = await h.Call("POST", "/execute", json: JsonSerializer.Serialize(new { cmd = "say hi\rquit" }));
        Assert.Equal(400, carriage);

        var (tooLong, tooLongBody) = await h.Call("POST", "/execute", json: JsonSerializer.Serialize(new { cmd = new string('a', 1025) }));
        Assert.Equal(400, tooLong);
        Assert.Contains("cmd too long", tooLongBody);
    }

    [Fact]
    public async Task ExecuteRejectsOversizedBodies()
    {
        using var h = ApiHost.Create();
        var body = JsonSerializer.Serialize(new { cmd = "x", pad = new string('p', 5000) });
        var (status, _) = await h.Call("POST", "/execute", json: body);
        Assert.Equal(413, status);
    }

    [Fact]
    public async Task ExecuteWithoutAChildIs503()
    {
        using var h = ApiHost.Create();
        var (status, body) = await h.Call("POST", "/execute", json: JsonSerializer.Serialize(new { cmd = "status" }));
        Assert.Equal(503, status);
        Assert.Contains("child not running", body);

        // The attempt is still audited, with success=false.
        var ev = Assert.Single(h.AuditEvents(), e => e.GetProperty("event").GetString() == "execute");
        Assert.Equal("status", ev.GetProperty("cmd").GetString());
        Assert.False(ev.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task ChatValidatesTextTheSameWay()
    {
        using var h = ApiHost.Create();
        var (wrongMethod, _) = await h.Call("GET", "/chat");
        Assert.Equal(405, wrongMethod);

        var (noText, noTextBody) = await h.Call("POST", "/chat", json: "{}");
        Assert.Equal(400, noText);
        Assert.Contains("text required", noTextBody);

        var (tooLong, _) = await h.Call("POST", "/chat", json: JsonSerializer.Serialize(new { text = new string('a', 513) }));
        Assert.Equal(400, tooLong);

        var (newline, _) = await h.Call("POST", "/chat", json: JsonSerializer.Serialize(new { text = "a\nb" }));
        Assert.Equal(400, newline);
    }

    // ---- bans / allows / scheduler over HTTP ----

    [Fact]
    public async Task BansCrudOverHttp()
    {
        using var h = ApiHost.Create();
        Assert.Empty((await h.Json("GET", "/bans")).GetProperty("bans").EnumerateArray());

        var (add, _) = await h.Call("POST", "/bans", json: JsonSerializer.Serialize(new { steamid = "76561198000000001", reason = "griefing" }));
        Assert.Equal(200, add);

        var listed = Assert.Single((await h.Json("GET", "/bans")).GetProperty("bans").EnumerateArray().ToList());
        Assert.Equal("76561198000000001", listed.GetProperty("steamid").GetString());
        Assert.Equal("griefing", listed.GetProperty("reason").GetString());

        var (noSteamId, noSteamIdBody) = await h.Call("POST", "/bans", json: "{}");
        Assert.Equal(400, noSteamId);
        Assert.Contains("steamid required", noSteamIdBody);

        var (del, _) = await h.Call("DELETE", "/bans/76561198000000001");
        Assert.Equal(200, del);
        var (delAgain, _) = await h.Call("DELETE", "/bans/76561198000000001");
        Assert.Equal(404, delAgain);

        var (wrongMethod, _) = await h.Call("PUT", "/bans");
        Assert.Equal(405, wrongMethod);
    }

    [Fact]
    public async Task AllowsCrudReportsEnforcement()
    {
        using var h = ApiHost.Create();
        var empty = await h.Json("GET", "/allows");
        Assert.False(empty.GetProperty("enforced").GetBoolean());

        var (add, _) = await h.Call("POST", "/allows", json: JsonSerializer.Serialize(new { steamid = "76561198000000001", note = "owner" }));
        Assert.Equal(200, add);

        var loaded = await h.Json("GET", "/allows");
        Assert.True(loaded.GetProperty("enforced").GetBoolean());
        Assert.Equal("owner", loaded.GetProperty("allow")[0].GetProperty("note").GetString());

        var (del, _) = await h.Call("DELETE", "/allows/76561198000000001");
        Assert.Equal(200, del);
        Assert.False((await h.Json("GET", "/allows")).GetProperty("enforced").GetBoolean());
    }

    [Fact]
    public async Task SchedulerCrudOverHttp()
    {
        using var h = ApiHost.Create();

        var (bad, badBody) = await h.Call("POST", "/scheduler", json: JsonSerializer.Serialize(new { id = "x", schedule = "every-so-often", command = "say hi" }));
        Assert.Equal(400, bad);
        Assert.Contains("invalid cron", badBody);

        var (add, _) = await h.Call("POST", "/scheduler", json: JsonSerializer.Serialize(new { id = "announce", schedule = "@every 1h", command = "say hi" }));
        Assert.Equal(200, add);

        var job = Assert.Single((await h.Json("GET", "/scheduler")).GetProperty("jobs").EnumerateArray().ToList());
        Assert.Equal("announce", job.GetProperty("id").GetString());
        Assert.True(job.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, job.GetProperty("run_count").GetInt64());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("last_run_at").ValueKind);
        Assert.False(string.IsNullOrEmpty(job.GetProperty("next_fire_at").GetString()));

        var (disable, _) = await h.Call("POST", "/scheduler/announce/disable");
        Assert.Equal(200, disable);
        Assert.False((await h.Json("GET", "/scheduler")).GetProperty("jobs")[0].GetProperty("enabled").GetBoolean());

        var (enable, _) = await h.Call("POST", "/scheduler/announce/enable");
        Assert.Equal(200, enable);

        var (unknownAction, _) = await h.Call("POST", "/scheduler/announce/frobnicate");
        Assert.Equal(404, unknownAction);

        var (del, _) = await h.Call("DELETE", "/scheduler/announce");
        Assert.Equal(200, del);
        var (delAgain, _) = await h.Call("DELETE", "/scheduler/announce");
        Assert.Equal(404, delAgain);
    }

    [Fact]
    public async Task SchedulerJobAddedOverHttpIsPersisted()
    {
        using var h = ApiHost.Create();
        await h.Call("POST", "/scheduler", json: JsonSerializer.Serialize(new { id = "announce", schedule = "@every 1h", command = "say hi" }));
        var onDisk = JsonDocument.Parse(File.ReadAllText(h.Config.SchedulerPath));
        Assert.Equal("announce", onDisk.RootElement.GetProperty("jobs")[0].GetProperty("id").GetString());
    }

    // ---- misc surface ----

    [Fact]
    public async Task UnknownPathIs404()
    {
        using var h = ApiHost.Create();
        var (status, body) = await h.Call("GET", "/nope");
        Assert.Equal(404, status);
        Assert.Contains("not found", body);

        // The historically mis-documented route really does not exist.
        var (metrics, _) = await h.Call("GET", "/metrics");
        Assert.Equal(404, metrics);
    }

    [Fact]
    public async Task DashboardCanBeDisabled()
    {
        using var h = ApiHost.Create("--dashboard-disabled");
        var (status, body) = await h.Call("GET", "/", password: null);
        Assert.Equal(404, status);
        Assert.Contains("dashboard disabled", body);
    }

    [Fact]
    public async Task HistoryReturnsBufferedLinesAndClampsCount()
    {
        using var h = ApiHost.Create("--buffer-size", "10");
        for (int i = 0; i < 20; i++) h.Buffer.Append("stdout", $"line {i}");

        var all = await h.Json("GET", "/history?count=9999");
        var entries = all.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(10, entries.Count); // clamped to --buffer-size, which is also the ring size
        Assert.Equal("line 19", entries[^1].GetProperty("line").GetString());
        Assert.Equal("stdout", entries[^1].GetProperty("stream").GetString());
        Assert.True(entries[^1].GetProperty("seq").GetInt64() > entries[0].GetProperty("seq").GetInt64());

        var few = await h.Json("GET", "/history?count=3");
        Assert.Equal(3, few.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task HistoryEscapesAwkwardLines()
    {
        using var h = ApiHost.Create();
        const string nasty = "quote\" backslash\\ tab\t end";
        h.Buffer.Append("stdout", nasty);
        var entries = (await h.Json("GET", "/history?count=5")).GetProperty("entries").EnumerateArray().ToList();
        Assert.Contains(entries, e => e.GetProperty("line").GetString() == nasty);
    }

    [Fact]
    public async Task StatusReportsAgentState()
    {
        using var h = ApiHost.Create();
        var s = await h.Json("GET", "/status");
        Assert.False(s.GetProperty("child_alive").GetBoolean());
        Assert.Equal(h.Config.ListenPort, s.GetProperty("listen_port").GetInt32());
        Assert.Equal(h.Config.ChildPort, s.GetProperty("child_port").GetInt32());
        Assert.Equal(h.Config.BufferSize, s.GetProperty("buffer_capacity").GetInt32());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("server").ValueKind); // no A2S poll configured
    }

    [Fact]
    public async Task PlayersReflectsTheConnectHookRoster()
    {
        using var h = ApiHost.Create();
        Assert.Empty((await h.Json("GET", "/players")).GetProperty("players").EnumerateArray());

        h.Buffer.Append("stdout", "01:20:36 Generic  Joe [76561198966650247] is connecting");

        var p = Assert.Single((await h.Json("GET", "/players")).GetProperty("players").EnumerateArray().ToList());
        Assert.Equal("76561198966650247", p.GetProperty("steamid").GetString());
        Assert.Equal("Joe", p.GetProperty("name").GetString());
    }

    // ---- logs browser ----

    [Fact]
    public async Task LogsRoutes404WithoutALogsDir()
    {
        using var h = ApiHost.Create();
        var (list, listBody) = await h.Call("GET", "/logs");
        Assert.Equal(404, list);
        Assert.Contains("logs-dir not configured", listBody);

        var (tail, _) = await h.Call("GET", "/logs/anything.log");
        Assert.Equal(404, tail);
    }

    [Fact]
    public async Task LogsListAndTail()
    {
        using var scratch = new Scratch();
        var logDir = Path.Combine(scratch.Dir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "server.log"), "one\ntwo\nthree\nfour\n");

        using var h = ApiHost.Create("--logs-dir", logDir);
        var list = await h.Json("GET", "/logs");
        var file = Assert.Single(list.GetProperty("files").EnumerateArray().ToList());
        Assert.Equal("server.log", file.GetProperty("name").GetString());
        Assert.True(file.GetProperty("size").GetInt64() > 0);

        var (status, body) = await h.Call("GET", "/logs/server.log?tail=2");
        Assert.Equal(200, status);
        Assert.DoesNotContain("one", body);
        Assert.Contains("four", body);
    }

    [Theory]
    [InlineData("/logs/../secret.txt")]
    [InlineData("/logs/..%2Fsecret.txt")]
    [InlineData("/logs/%2e%2e%2fsecret.txt")]
    [InlineData("/logs/sub%2Fnested.log")]
    [InlineData("/logs/")]
    public async Task LogsRoutesRefuseToEscapeTheRoot(string path)
    {
        using var scratch = new Scratch();
        var logDir = Path.Combine(scratch.Dir, "logs");
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(Path.Combine(logDir, "sub"));
        File.WriteAllText(Path.Combine(logDir, "sub", "nested.log"), "nested\n");
        File.WriteAllText(Path.Combine(scratch.Dir, "secret.txt"), "TOP SECRET\n");

        using var h = ApiHost.Create("--logs-dir", logDir);
        var (status, body) = await h.Call("GET", path);
        Assert.True(status == 404, $"{path} -> {status}");
        Assert.DoesNotContain("TOP SECRET", body);
        Assert.DoesNotContain("nested", body);
    }

    [Fact]
    public void LogsBrowserResolvesOnlyPlainNamesInsideTheRoot()
    {
        using var scratch = new Scratch();
        var logDir = Path.Combine(scratch.Dir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "server.log"), "hello\n");
        File.WriteAllText(Path.Combine(scratch.Dir, "secret.txt"), "TOP SECRET\n");

        var browser = new LogsBrowser(logDir);
        Assert.True(browser.Enabled);
        Assert.True(browser.TryResolve("server.log", out _));
        Assert.False(browser.TryResolve("../secret.txt", out _));
        Assert.False(browser.TryResolve("..\\secret.txt", out _));
        Assert.False(browser.TryResolve("sub/nested.log", out _));
        Assert.False(browser.TryResolve(Path.Combine(scratch.Dir, "secret.txt"), out _));
        Assert.False(browser.TryResolve("", out _));
        Assert.False(browser.TryResolve("missing.log", out _));

        Assert.False(new LogsBrowser(null).Enabled);
        Assert.False(new LogsBrowser(Path.Combine(scratch.Dir, "does-not-exist")).Enabled);
    }

    [Fact]
    public void LogsBrowserTailsFromTheEnd()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Dir, "a.log");
        File.WriteAllText(file, string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}")) + "\n");
        var browser = new LogsBrowser(scratch.Dir);
        var tail = browser.TailToString(file, 3);
        Assert.DoesNotContain("line 96", tail);
        Assert.Contains("line 99", tail);
        Assert.Contains("line 100", tail);
    }

    // ---- message buffer ----

    [Fact]
    public void MessageBufferIsABoundedRing()
    {
        using var buf = new MessageBuffer(3);
        for (int i = 1; i <= 5; i++) buf.Append("stdout", $"line {i}");
        var tail = buf.Tail(10);
        Assert.Equal(3, tail.Count);
        Assert.Equal("line 3", tail[0].Line);
        Assert.Equal("line 5", tail[^1].Line);
        Assert.Equal(5, buf.LastSeq);
        Assert.Equal(2, buf.SinceSeq(3).Count);
        Assert.Empty(buf.SinceSeq(5));
    }
}
