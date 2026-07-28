using System.Text.Json;
using Xunit;

namespace SboxServerConsole.Tests;

// v1.0.0 shipped Save() using anonymous types in JsonSerializer.Serialize(), which
// emits PascalCase property names. Load() reads snake_case, so every save wrote a
// file the next start could not read — bans and scheduler jobs silently vanished on
// restart. v1.0.1 replaced them with named DTOs carrying [JsonPropertyName].
//
// These tests pin both halves of that fix: the exact on-disk key names, and a real
// save/reload round-trip through a second instance.
public class PersistenceTests
{
    static (CliConfig Cfg, MessageBuffer Buf, ServerProcess Srv, AuditLog Audit, DiscordWebhook Discord) Deps(Scratch s, params string[] extra)
    {
        var cfg = Configs.Parse(s.Dir, extra);
        var buf = new MessageBuffer(cfg.BufferSize);
        var srv = new ServerProcess(cfg, buf);
        var audit = new AuditLog(cfg.AuditLogPath);
        var discord = new DiscordWebhook(cfg.DiscordWebhookUrl);
        return (cfg, buf, srv, audit, discord);
    }

    // ---- banlist ----

    [Fact]
    public void Banlist_SavesSnakeCaseSchema()
    {
        using var s = new Scratch();
        var d = Deps(s, "--banlist", s.File("bans.json"));
        using (var bans = new Banlist(d.Cfg, d.Srv, d.Buf, d.Audit, d.Discord))
        {
            Assert.True(bans.Add("76561198000000001", "griefing", "127.0.0.1"));
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(s.File("bans.json")));
        var root = doc.RootElement;
        Assert.Equal(new[] { "bans" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        var entry = root.GetProperty("bans")[0];
        Assert.Equal(
            new[] { "added_at", "added_by", "reason", "steamid" },
            entry.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("76561198000000001", entry.GetProperty("steamid").GetString());
        Assert.Equal("griefing", entry.GetProperty("reason").GetString());
        Assert.Equal("127.0.0.1", entry.GetProperty("added_by").GetString());
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("added_at").GetString()));
    }

    [Fact]
    public void Banlist_RoundTripsThroughRestart()
    {
        using var s = new Scratch();
        var d = Deps(s, "--banlist", s.File("bans.json"));
        using (var bans = new Banlist(d.Cfg, d.Srv, d.Buf, d.Audit, d.Discord))
        {
            bans.Add("76561198000000001", "griefing", "alice");
            bans.Add("76561198000000002", "cheating", "bob");
            Assert.True(bans.Remove("76561198000000002"));
        }

        var d2 = Deps(s, "--banlist", s.File("bans.json"));
        using var reloaded = new Banlist(d2.Cfg, d2.Srv, d2.Buf, d2.Audit, d2.Discord);
        var all = reloaded.All();
        Assert.Single(all);
        Assert.Equal("76561198000000001", all[0].SteamId);
        Assert.Equal("griefing", all[0].Reason);
        Assert.Equal("alice", all[0].AddedBy);
        Assert.True(reloaded.IsBanned("76561198000000001"));
        Assert.False(reloaded.IsBanned("76561198000000002"));
    }

    [Fact]
    public void Banlist_WithoutPathIsInMemoryOnly()
    {
        using var s = new Scratch();
        var d = Deps(s);
        using var bans = new Banlist(d.Cfg, d.Srv, d.Buf, d.Audit, d.Discord);
        Assert.False(bans.Persisted);
        bans.Add("76561198000000003", "", "");
        Assert.Single(bans.All());
        Assert.Empty(Directory.GetFiles(s.Dir));
    }

    [Fact]
    public void Banlist_CorruptFileDoesNotThrow()
    {
        using var s = new Scratch();
        File.WriteAllText(s.File("bans.json"), "{ this is not json");
        var d = Deps(s, "--banlist", s.File("bans.json"));
        using var bans = new Banlist(d.Cfg, d.Srv, d.Buf, d.Audit, d.Discord);
        Assert.Empty(bans.All());
        Assert.Contains(d.Buf.Tail(50), e => e.Line.Contains("banlist load failed"));
    }

    // ---- allowlist ----

    [Fact]
    public void Allowlist_SavesSnakeCaseSchema()
    {
        using var s = new Scratch();
        var d = Deps(s, "--allowlist", s.File("allow.json"));
        using (var allow = new Allowlist(d.Cfg, d.Srv, d.Buf, d.Audit))
        {
            Assert.True(allow.Add("76561198000000001", "owner", "127.0.0.1"));
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(s.File("allow.json")));
        var root = doc.RootElement;
        Assert.Equal(new[] { "allow" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        var entry = root.GetProperty("allow")[0];
        Assert.Equal(
            new[] { "added_at", "added_by", "note", "steamid" },
            entry.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("owner", entry.GetProperty("note").GetString());
    }

    [Fact]
    public void Allowlist_RoundTripsThroughRestart()
    {
        using var s = new Scratch();
        var d = Deps(s, "--allowlist", s.File("allow.json"));
        using (var allow = new Allowlist(d.Cfg, d.Srv, d.Buf, d.Audit))
        {
            allow.Add("76561198000000001", "owner", "alice");
            allow.Add("76561198000000002", "friend", "alice");
            Assert.True(allow.Enforced);
        }

        var d2 = Deps(s, "--allowlist", s.File("allow.json"));
        using var reloaded = new Allowlist(d2.Cfg, d2.Srv, d2.Buf, d2.Audit);
        Assert.Equal(2, reloaded.Count);
        Assert.True(reloaded.Enforced);
        Assert.True(reloaded.IsAllowed("76561198000000002"));
        Assert.Equal("owner", reloaded.All().First(e => e.SteamId == "76561198000000001").Note);
    }

    [Fact]
    public void Allowlist_EmptyMeansNotEnforced()
    {
        using var s = new Scratch();
        var d = Deps(s, "--allowlist", s.File("allow.json"));
        using var allow = new Allowlist(d.Cfg, d.Srv, d.Buf, d.Audit);
        Assert.False(allow.Enforced);
        allow.Add("76561198000000001", "", "");
        Assert.True(allow.Enforced);
        allow.Remove("76561198000000001");
        Assert.False(allow.Enforced);
    }

    // ---- scheduler ----

    [Fact]
    public void Scheduler_SavesSnakeCaseSchema()
    {
        using var s = new Scratch();
        var d = Deps(s, "--scheduler", s.File("sched.json"));
        using (var sched = new Scheduler(d.Cfg, d.Srv, d.Buf, d.Audit))
        {
            Assert.True(sched.TryAdd("announce", "@every 30m", "say hello", out _));
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(s.File("sched.json")));
        var root = doc.RootElement;
        Assert.Equal(new[] { "jobs" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        var job = root.GetProperty("jobs")[0];
        Assert.Equal(
            new[] { "command", "created_at", "enabled", "id", "last_run_at", "run_count", "schedule" },
            job.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("announce", job.GetProperty("id").GetString());
        Assert.Equal("@every 30m", job.GetProperty("schedule").GetString());
        Assert.Equal("say hello", job.GetProperty("command").GetString());
        Assert.True(job.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, job.GetProperty("run_count").GetInt64());
    }

    [Fact]
    public void Scheduler_RoundTripsThroughRestart()
    {
        using var s = new Scratch();
        var d = Deps(s, "--scheduler", s.File("sched.json"));
        using (var sched = new Scheduler(d.Cfg, d.Srv, d.Buf, d.Audit))
        {
            Assert.True(sched.TryAdd("nightly", "0 4 * * *", "say nightly restart", out _));
            Assert.True(sched.TryAdd("frequent", "@every 5m", "say hi", out _));
            Assert.True(sched.SetEnabled("frequent", false));
        }

        var d2 = Deps(s, "--scheduler", s.File("sched.json"));
        using var reloaded = new Scheduler(d2.Cfg, d2.Srv, d2.Buf, d2.Audit);
        var jobs = reloaded.All();
        Assert.Equal(2, jobs.Count);

        var nightly = jobs.First(j => j.Id == "nightly");
        Assert.Equal("0 4 * * *", nightly.Schedule);
        Assert.Equal("say nightly restart", nightly.Command);
        Assert.True(nightly.Enabled);

        // A disabled job stays disabled across the restart — losing this flag would
        // silently resume a job the operator turned off.
        var frequent = jobs.First(j => j.Id == "frequent");
        Assert.False(frequent.Enabled);

        // Reloaded jobs get a real next-fire time, not DateTime.MaxValue.
        Assert.NotEqual(DateTime.MaxValue, reloaded.NextFireFor("nightly"));
    }

    [Fact]
    public void Scheduler_RunCountAndLastRunSurviveReload()
    {
        using var s = new Scratch();
        // Hand-write a file with a run history, which is what a long-lived agent leaves behind.
        File.WriteAllText(s.File("sched.json"), """
            {"jobs":[{"id":"announce","schedule":"@every 1h","command":"say hi","enabled":true,
                      "created_at":"2026-01-01T00:00:00.0000000Z","last_run_at":"2026-01-02T03:04:05.0000000Z",
                      "run_count":42}]}
            """);
        var d = Deps(s, "--scheduler", s.File("sched.json"));
        using var sched = new Scheduler(d.Cfg, d.Srv, d.Buf, d.Audit);
        var job = Assert.Single(sched.All());
        Assert.Equal(42, job.RunCount);
        Assert.Equal("2026-01-02T03:04:05.0000000Z", job.LastRunAt);
        Assert.Equal("2026-01-01T00:00:00.0000000Z", job.CreatedAt);
    }

    [Theory]
    [InlineData("@every 1s", true)]
    [InlineData("@every 90s", true)]
    [InlineData("@every 5m", true)]
    [InlineData("@every 12h", true)]
    [InlineData("@EVERY 5m", true)]
    [InlineData("0 */4 * * *", true)]
    [InlineData("0 4 * * *", true)]
    [InlineData("@every 0s", false)]
    [InlineData("@every 5x", false)]
    [InlineData("@every", false)]
    [InlineData("not a schedule", false)]
    [InlineData("99 99 * * *", false)]
    public void Scheduler_ScheduleParsing(string schedule, bool valid)
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        bool ok = Scheduler.TryComputeNext(schedule, from, out var next, out string err);
        Assert.True(ok == valid, $"'{schedule}' -> ok={ok} err={err}");
        if (ok) Assert.True(next > from);
    }

    [Fact]
    public void Scheduler_EveryDurationsAreExact()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(Scheduler.TryComputeNext("@every 90s", from, out var s90, out _));
        Assert.Equal(from.AddSeconds(90), s90);
        Assert.True(Scheduler.TryComputeNext("@every 5m", from, out var m5, out _));
        Assert.Equal(from.AddMinutes(5), m5);
        Assert.True(Scheduler.TryComputeNext("@every 12h", from, out var h12, out _));
        Assert.Equal(from.AddHours(12), h12);
        Assert.True(Scheduler.TryComputeNext("0 */4 * * *", from, out var cron, out _));
        Assert.Equal(from.AddHours(4), cron);
    }

    [Fact]
    public void Scheduler_RejectsIncompleteJobs()
    {
        using var s = new Scratch();
        var d = Deps(s, "--scheduler", s.File("sched.json"));
        using var sched = new Scheduler(d.Cfg, d.Srv, d.Buf, d.Audit);

        Assert.False(sched.TryAdd("", "@every 5m", "say hi", out string e1));
        Assert.Equal("id required", e1);
        Assert.False(sched.TryAdd("x", "", "say hi", out string e2));
        Assert.Equal("schedule required", e2);
        Assert.False(sched.TryAdd("x", "@every 5m", "", out string e3));
        Assert.Equal("command required", e3);
        Assert.False(sched.TryAdd("x", "nonsense", "say hi", out string e4));
        Assert.Contains("invalid cron", e4);
        Assert.Empty(sched.All());
        Assert.False(File.Exists(s.File("sched.json")));
    }

    // ---- config file ----

    [Fact]
    public void ConfigFile_RoundTripsEveryJsonValueKind()
    {
        using var s = new Scratch();
        var cfgPath = s.File("config.json");
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["exe"] = Configs.InertExe,
            ["game-dir"] = s.Dir,
            ["child-args"] = "+port 13600 +net_query_port 13601",
            ["buffer-size"] = 750,
            ["rcon-password"] = "from-file",
            ["dashboard-disabled"] = true,
            ["no-auto-restart"] = false,
            ["kick-command"] = "kickid {steamid}",
        }));

        var cfg = CliConfig.Parse(new[] { "--config-file", cfgPath });
        Assert.NotNull(cfg);
        Assert.Equal(13600, cfg!.ChildPort);
        Assert.Equal(13601, cfg.QueryPort);
        Assert.Equal(750, cfg.BufferSize);
        Assert.Equal("from-file", cfg.RconPassword);
        Assert.False(cfg.DashboardEnabled);
        Assert.True(cfg.AutoRestart);
        Assert.Equal("kickid {steamid}", cfg.KickCommandTemplate);
    }

    [Fact]
    public void ConfigFile_CliFlagsWin()
    {
        using var s = new Scratch();
        var cfgPath = s.File("config.json");
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["exe"] = Configs.InertExe,
            ["game-dir"] = s.Dir,
            ["child-args"] = "+port 13600",
            ["rcon-password"] = "from-file",
        }));

        var cfg = CliConfig.Parse(new[] { "--config-file", cfgPath, "--rcon-password", "from-cli" });
        Assert.NotNull(cfg);
        Assert.Equal("from-cli", cfg!.RconPassword);
    }

    [Fact]
    public void ConfigFile_MissingOrMalformedIsRejected()
    {
        using var s = new Scratch();
        Assert.Null(CliConfig.Parse(new[] { "--config-file", s.File("nope.json") }));

        var bad = s.File("bad.json");
        File.WriteAllText(bad, "{ nope");
        Assert.Null(CliConfig.Parse(new[] { "--config-file", bad }));
    }
}
