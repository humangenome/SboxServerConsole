using System.Text.Json;
using Xunit;

namespace SboxServerConsole.Tests;

// Ban / allowlist enforcement is driven entirely off the child's stdout through the
// configured connect/disconnect regexes. The sample lines below are the real
// sbox-server.exe shapes documented in CliConfig.cs.
//
// No child process is running in these tests, so TrySendCommand fails and the kick
// is recorded with "sent": false. What is under test is the decision to kick, the
// command that would be sent, and the roster bookkeeping.
public class EnforcementTests : IDisposable
{
    const string Alice = "76561198966650247";
    const string Bob = "76561198000000002";

    static string Connecting(string name, string steamid) => $"01:20:36 Generic  {name} [{steamid}] is connecting";
    static string Disconnecting(string steamid) => $"SteamIdSocket - steamid:12345: Disconnection ({steamid})";

    readonly Scratch _s = new();
    readonly CliConfig _cfg;
    readonly MessageBuffer _buf;
    readonly ServerProcess _srv;
    readonly AuditLog _audit;
    readonly DiscordWebhook _discord;

    public EnforcementTests()
    {
        _cfg = Configs.Parse(_s.Dir,
            "--banlist", _s.File("bans.json"),
            "--allowlist", _s.File("allow.json"),
            "--audit-log", _s.File("audit.jsonl"),
            "--kick-command", "kickid {steamid}");
        _buf = new MessageBuffer(_cfg.BufferSize);
        _srv = new ServerProcess(_cfg, _buf);
        _audit = new AuditLog(_cfg.AuditLogPath);
        _discord = new DiscordWebhook(_cfg.DiscordWebhookUrl);
    }

    public void Dispose()
    {
        _audit.Dispose();
        _discord.Dispose();
        _srv.Dispose();
        _buf.Dispose();
        _s.Dispose();
    }

    IReadOnlyList<JsonElement> Audit()
    {
        if (!File.Exists(_cfg.AuditLogPath)) return Array.Empty<JsonElement>();
        return File.ReadAllLines(_cfg.AuditLogPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();
    }

    IReadOnlyList<JsonElement> AuditOf(string type)
        => Audit().Where(e => e.GetProperty("event").GetString() == type).ToList();

    [Fact]
    public void ConnectLine_PopulatesRoster()
    {
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        Assert.True(bans.ConnectRegexConfigured);
        Assert.True(bans.DisconnectRegexConfigured);

        _buf.Append("stdout", Connecting("Joe", Alice));
        var online = bans.Online();
        Assert.Single(online);
        Assert.Equal(Alice, online[0].SteamId);
        Assert.Equal("Joe", online[0].Name);
    }

    [Fact]
    public void DisconnectLine_ClearsRoster()
    {
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Disconnecting(Alice));
        Assert.Empty(bans.Online());
    }

    [Fact]
    public void DisconnectLine_ColonFormAlsoMatches()
    {
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", $"SteamIdSocket - steamid:{Alice}: Disconnection");
        Assert.Empty(bans.Online());
    }

    [Fact]
    public void NonStdoutStreamsAreIgnored()
    {
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        _buf.Append("input", Connecting("Joe", Alice));
        _buf.Append("system", Connecting("Joe", Alice));
        _buf.Append("chat", Connecting("Joe", Alice));
        Assert.Empty(bans.Online());
    }

    [Fact]
    public void BannedPlayerIsKickedOnConnect()
    {
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        bans.Add(Alice, "griefing", "staff");

        bool joinFired = false;
        bans.OnPlayerJoin += _ => joinFired = true;

        _buf.Append("stdout", Connecting("Joe", Alice));

        var kicks = AuditOf("ban_kick");
        Assert.Equal(2, kicks.Count); // one from Add() (kick-if-online), one from the connect hook
        Assert.All(kicks, k => Assert.Equal(Alice, k.GetProperty("steamid").GetString()));
        // The {steamid} placeholder must be substituted, not passed through literally.
        Assert.All(kicks, k => Assert.Equal($"kickid {Alice}", k.GetProperty("cmd").GetString()));
        Assert.False(joinFired);
    }

    [Fact]
    public void UnbannedPlayerIsNotKicked()
    {
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        bans.Add(Bob, "griefing", "staff");

        bool joinFired = false;
        bans.OnPlayerJoin += _ => joinFired = true;

        _buf.Append("stdout", Connecting("Joe", Alice));

        Assert.DoesNotContain(AuditOf("ban_kick"), k => k.GetProperty("steamid").GetString() == Alice);
        Assert.True(joinFired);
    }

    [Fact]
    public void UnbanStopsTheKicking()
    {
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        bans.Add(Alice, "griefing", "staff");
        Assert.True(bans.Remove(Alice));
        int before = AuditOf("ban_kick").Count;

        _buf.Append("stdout", Connecting("Joe", Alice));

        Assert.Equal(before, AuditOf("ban_kick").Count);
        Assert.Single(AuditOf("ban_remove"));
    }

    [Fact]
    public void RosterSweepEnforcesBans()
    {
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        bans.Add(Bob, "cheating", "staff");
        bans.UpdateOnlineFromStatus(new[]
        {
            new Banlist.Player(Alice, "Joe", DateTime.UtcNow),
            new Banlist.Player(Bob, "Eve", DateTime.UtcNow),
        });

        Assert.Equal(2, bans.Online().Count);
        Assert.Contains(AuditOf("ban_kick"), k => k.GetProperty("steamid").GetString() == Bob);
        Assert.DoesNotContain(AuditOf("ban_kick"), k => k.GetProperty("steamid").GetString() == Alice);
    }

    [Fact]
    public void EmptyAllowlistNeverKicks()
    {
        using var allow = new Allowlist(_cfg, _srv, _buf, _audit);
        Assert.False(allow.Enforced);

        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Connecting("Eve", Bob));

        Assert.Empty(AuditOf("allow_kick"));
    }

    [Fact]
    public void NonEmptyAllowlistKicksEveryoneElse()
    {
        using var allow = new Allowlist(_cfg, _srv, _buf, _audit);
        allow.Add(Alice, "owner", "staff");
        Assert.True(allow.Enforced);

        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Connecting("Eve", Bob));

        var kicks = AuditOf("allow_kick");
        var kicked = Assert.Single(kicks);
        Assert.Equal(Bob, kicked.GetProperty("steamid").GetString());
        Assert.Equal($"kickid {Bob}", kicked.GetProperty("cmd").GetString());
        Assert.Equal("not on allowlist", kicked.GetProperty("reason").GetString());
    }

    [Fact]
    public void AllowlistRosterSweepOnlyRunsWhenEnforced()
    {
        using var allow = new Allowlist(_cfg, _srv, _buf, _audit);
        var roster = new[] { new Banlist.Player(Bob, "Eve", DateTime.UtcNow) };

        allow.EnforceAgainstRoster(roster);
        Assert.Empty(AuditOf("allow_kick"));

        allow.Add(Alice, "owner", "staff");
        allow.EnforceAgainstRoster(roster);
        Assert.Single(AuditOf("allow_kick"));
    }

    [Fact]
    public void GarbledLinesDoNotMatch()
    {
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        _buf.Append("stdout", "01:20:36 Generic  Joe [123] is connecting");            // steamid too short
        _buf.Append("stdout", "01:20:36 Generic  Joe [76561198966650247] is connected"); // post-handshake line
        _buf.Append("stdout", "Physics 1.23ms, Network 4.56ms");
        Assert.Empty(bans.Online());
    }

    [Fact]
    public void BanAndAllowlistShareOneConnectHook()
    {
        // Both listeners attach to the same MessageBuffer; a banned player who is also
        // on the allowlist still gets kicked by the banlist.
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        using var allow = new Allowlist(_cfg, _srv, _buf, _audit);
        bans.Add(Alice, "griefing", "staff");
        allow.Add(Alice, "owner", "staff");

        _buf.Append("stdout", Connecting("Joe", Alice));

        Assert.Contains(AuditOf("ban_kick"), k => k.GetProperty("steamid").GetString() == Alice);
        Assert.Empty(AuditOf("allow_kick"));
    }
}
