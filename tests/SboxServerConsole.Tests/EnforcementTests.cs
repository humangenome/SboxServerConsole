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

    // --max-players: a dedicated s&box server ignores +maxplayers, so the agent keeps the limit. A joiner who
    // arrives when that many players are already on is kicked with the reason "Server is full.".
    const string Carol = "76561198000000003";

    CliConfig Capped(int max) => Configs.Parse(_s.Dir,
        "--banlist", _s.File("bans.json"),
        "--allowlist", _s.File("allow.json"),
        "--audit-log", _s.File("audit.jsonl"),
        "--kick-command", "kickid {steamid}",
        "--max-players", max.ToString());

    [Fact]
    public void NoPlayerLimitByDefault()
    {
        Assert.Equal(0, _cfg.MaxPlayers);
        using var bans = new Banlist(_cfg, _srv, _buf, _audit, _discord);
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Connecting("Eve", Bob));
        _buf.Append("stdout", Connecting("Cat", Carol));
        Assert.Empty(AuditOf("full_kick"));
        Assert.Equal(3, bans.Online().Count);
    }

    [Fact]
    public void UnderThePlayerLimitIsLetIn()
    {
        using var bans = new Banlist(Capped(2), _srv, _buf, _audit, _discord);
        var joined = new List<string>();
        bans.OnPlayerJoin += p => joined.Add(p.SteamId);
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Connecting("Eve", Bob));
        Assert.Empty(AuditOf("full_kick"));
        Assert.Equal(new[] { Alice, Bob }, joined);
    }

    [Fact]
    public void AJoinerOverThePlayerLimitIsToldTheServerIsFull()
    {
        using var bans = new Banlist(Capped(2), _srv, _buf, _audit, _discord);
        var joined = new List<string>();
        bans.OnPlayerJoin += p => joined.Add(p.SteamId);
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Connecting("Eve", Bob));
        _buf.Append("stdout", Connecting("Cat", Carol));

        var kick = Assert.Single(AuditOf("full_kick"));
        Assert.Equal(Carol, kick.GetProperty("steamid").GetString());
        Assert.Equal($"kickid {Carol} \"Server is full.\"", kick.GetProperty("cmd").GetString());
        Assert.Equal(2, kick.GetProperty("players").GetInt32());
        Assert.Equal(2, kick.GetProperty("max_players").GetInt32());
        Assert.Equal(new[] { Alice, Bob }, joined);
        Assert.Empty(AuditOf("ban_kick"));
    }

    // The log is untrusted input: a player types chat and picks a display name, and both land on the same output.
    const string Dave = "76561198000000004";
    const string Fake1 = "76561190000000001";
    const string Fake2 = "76561190000000002";
    static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    Banlist CappedBans(int max, out MutableClock clock)
    {
        var c = new MutableClock { At = T0 };
        clock = c;
        return new Banlist(Capped(max), _srv, _buf, _audit, _discord) { Now = () => c.At };
    }

    sealed class MutableClock { public DateTime At; }

    [Fact]
    public void ChatShapedLikeJoinsTakesNoSlots()
    {
        using var bans = CappedBans(2, out _);
        for (int i = 0; i < 10; i++) _buf.Append("stdout", $"01:20:36 Generic  Eve: x [7656119000000{i:D4}] is connecting");
        Assert.Empty(bans.Online());
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Connecting("Sam", Bob));
        Assert.Empty(AuditOf("full_kick"));
        Assert.Equal(2, bans.Online().Count);
    }

    [Fact]
    public void ChatShapedLikeALeaveTakesNoOneOff()
    {
        using var bans = CappedBans(2, out _);
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", $"01:21:00 Generic  Eve: x [{Alice}] disconnected");
        _buf.Append("stdout", $"01:21:00 Generic  Eve: SteamIdSocket - steamid:1: Disconnection ({Alice})");
        Assert.Equal(Alice, Assert.Single(bans.Online()).SteamId);
    }

    [Fact]
    public void AConsoleEchoTakesNoSlot()
    {
        using var bans = CappedBans(1, out _);
        _buf.Append("stdout", $"> LabStandIn [{Fake1}] is connecting");
        Assert.Empty(bans.Online());
        _buf.Append("stdout", Connecting("Joe", Alice));
        Assert.Empty(AuditOf("full_kick"));
    }

    [Fact]
    public void ACraftedNameCannotAddOrRemoveAnyoneElse()
    {
        using var bans = CappedBans(1, out _);
        _buf.Append("stdout", Connecting("Sam", Alice));
        // Their chat, with a name holding the words.
        _buf.Append("stdout", $"01:21:00 Generic  a [{Fake1}] is connecting: hello");
        _buf.Append("stdout", $"01:21:00 Generic  a [{Alice}] disconnected: hello");
        Assert.Equal(Alice, Assert.Single(bans.Online()).SteamId);
        Assert.Empty(AuditOf("full_kick"));
    }

    [Fact]
    public void ACraftedNameIsListedUnderTheirOwnIdAndLeavesUnderIt()
    {
        using var bans = CappedBans(3, out _);
        _buf.Append("stdout", $"01:21:00 Generic  a [{Fake1}] is connecting [{Alice}] is connecting");
        Assert.Equal(Alice, Assert.Single(bans.Online()).SteamId);
        _buf.Append("stdout", $"01:22:00 Generic  a [{Fake1}] is connecting [{Alice}] disconnected");
        Assert.Empty(bans.Online());
    }

    [Fact]
    public void AColonInARealNameMeansABriefOverfillNotARefusal()
    {
        using var bans = CappedBans(1, out _);
        _buf.Append("stdout", Connecting("Joe: The Great", Alice));
        Assert.Empty(bans.Online());
        _buf.Append("stdout", Connecting("Sam", Bob));
        Assert.Empty(AuditOf("full_kick"));
    }

    [Fact]
    public void AFakedLineStillKicksOnlyABannedId()
    {
        using var bans = CappedBans(1, out _);
        bans.Add(Bob, "griefing", "test");
        _buf.Append("stdout", $"01:20:36 Generic  Eve: x [{Bob}] is connecting");
        _buf.Append("stdout", $"01:20:36 Generic  Eve: x [{Alice}] is connecting");
        // Adding the ban sends its own kick, so the faked line makes two, both for the banned ID.
        Assert.All(AuditOf("ban_kick"), e => Assert.Equal(Bob, e.GetProperty("steamid").GetString()));
        Assert.Equal(2, AuditOf("ban_kick").Count);
        Assert.Empty(bans.Online());
    }

    [Fact]
    public void AMissedLeaveIsClearedOnceA2SSaysTheServerIsEmpty()
    {
        using var bans = CappedBans(1, out var clock);
        _buf.Append("stdout", Connecting("Joe", Alice));
        clock.At = T0.AddMinutes(4);
        bans.ObserveA2SPlayers(0);
        Assert.Single(bans.Online());
        clock.At = T0.AddMinutes(5);
        bans.ObserveA2SPlayers(0);
        Assert.Empty(bans.Online());
        Assert.Equal(Alice, Assert.Single(AuditOf("roster_cleared")).GetProperty("steamids").GetString());
        _buf.Append("stdout", Connecting("Sam", Bob));
        Assert.Empty(AuditOf("full_kick"));
    }

    [Fact]
    public void APlayerStillLoadingIsNotClearedByAnEmptyA2S()
    {
        using var bans = CappedBans(1, out var clock);
        _buf.Append("stdout", Connecting("Joe", Alice));
        clock.At = T0.AddSeconds(30);
        bans.ObserveA2SPlayers(0);
        clock.At = T0.AddSeconds(100);
        bans.ObserveA2SPlayers(0);
        Assert.Single(bans.Online());
        Assert.Empty(AuditOf("roster_cleared"));
    }

    [Fact]
    public void A2SHasToStayEmptyToClear()
    {
        using var bans = CappedBans(1, out var clock);
        _buf.Append("stdout", Connecting("Joe", Alice));
        clock.At = T0.AddMinutes(4);
        bans.ObserveA2SPlayers(0);
        clock.At = T0.AddMinutes(4.5);
        bans.ObserveA2SPlayers(1);
        clock.At = T0.AddMinutes(5);
        bans.ObserveA2SPlayers(0);
        Assert.Single(bans.Online());
    }

    [Fact]
    public void AMissedLeaveWithOthersOnDoesNotRefuseARealJoiner()
    {
        using var bans = CappedBans(2, out var clock);
        _buf.Append("stdout", Connecting("Gone", Alice));   // its leave line never came
        _buf.Append("stdout", Connecting("Here", Bob));
        clock.At = T0.AddMinutes(4);
        bans.ObserveA2SPlayers(1);
        _buf.Append("stdout", Connecting("New", Carol));
        Assert.Empty(AuditOf("full_kick"));
        var skip = Assert.Single(AuditOf("full_kick_skipped"));
        Assert.Equal(1, skip.GetProperty("a2s_players").GetInt32());
        Assert.Contains(bans.Online(), p => p.SteamId == Carol);
    }

    [Fact]
    public void AServerA2SAgreesIsFullStillRefuses()
    {
        using var bans = CappedBans(2, out var clock);
        _buf.Append("stdout", Connecting("One", Alice));
        _buf.Append("stdout", Connecting("Two", Bob));
        clock.At = T0.AddMinutes(4);
        bans.ObserveA2SPlayers(2);
        _buf.Append("stdout", Connecting("New", Carol));
        Assert.Single(AuditOf("full_kick"));
        Assert.Empty(AuditOf("full_kick_skipped"));
    }

    [Fact]
    public void PlayersStillLoadingCountBeforeA2SSeesThem()
    {
        using var bans = CappedBans(2, out var clock);
        _buf.Append("stdout", Connecting("One", Alice));
        _buf.Append("stdout", Connecting("Two", Bob));
        clock.At = T0.AddSeconds(10);
        bans.ObserveA2SPlayers(0);
        _buf.Append("stdout", Connecting("New", Carol));
        Assert.Single(AuditOf("full_kick"));
    }

    [Fact]
    public void WithNoA2STheRosterStands()
    {
        using var bans = CappedBans(1, out _);
        _buf.Append("stdout", Connecting("One", Alice));
        _buf.Append("stdout", Connecting("New", Bob));
        Assert.Single(AuditOf("full_kick"));
    }

    [Fact]
    public void AStaleA2SIsNotBelieved()
    {
        using var bans = CappedBans(1, out var clock);
        bans.ObserveA2SPlayers(0);
        _buf.Append("stdout", Connecting("One", Alice));
        clock.At = T0.AddMinutes(10);
        _buf.Append("stdout", Connecting("New", Bob));
        Assert.Single(AuditOf("full_kick"));
    }

    [Fact]
    public void ARejoinIsNotCountedTwice()
    {
        using var bans = new Banlist(Capped(1), _srv, _buf, _audit, _discord);
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Connecting("Joe", Alice));
        Assert.Empty(AuditOf("full_kick"));
        Assert.Single(bans.Online());
    }

    [Fact]
    public void ALeaveFreesTheSlot()
    {
        using var bans = new Banlist(Capped(1), _srv, _buf, _audit, _discord);
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Disconnecting(Alice));
        _buf.Append("stdout", Connecting("Eve", Bob));
        Assert.Empty(AuditOf("full_kick"));
    }

    [Fact]
    public void AKickedJoinerLeavesNoGhost()
    {
        using var bans = new Banlist(Capped(1), _srv, _buf, _audit, _discord);
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Connecting("Eve", Bob));      // full: kicked
        _buf.Append("stdout", Disconnecting(Bob));          // the kick closes the connection
        _buf.Append("stdout", Disconnecting(Alice));
        _buf.Append("stdout", Connecting("Cat", Carol));    // the server is empty again
        var kick = Assert.Single(AuditOf("full_kick"));
        Assert.Equal(Bob, kick.GetProperty("steamid").GetString());
        Assert.Equal(Carol, Assert.Single(bans.Online()).SteamId);
    }

    [Fact]
    public void ABannedJoinerGetsOnlyTheBanKick()
    {
        using var bans = new Banlist(Capped(1), _srv, _buf, _audit, _discord);
        _buf.Append("stdout", Connecting("Joe", Alice));
        bans.Add(Bob, "griefing", "staff");
        _buf.Append("stdout", Connecting("Eve", Bob));
        Assert.Contains(AuditOf("ban_kick"), k => k.GetProperty("steamid").GetString() == Bob);
        Assert.Empty(AuditOf("full_kick"));
    }

    [Fact]
    public void AJoinerTheAllowlistRefusesGetsOnlyThatKick()
    {
        var cfg = Capped(1);
        using var bans = new Banlist(cfg, _srv, _buf, _audit, _discord);
        using var allow = new Allowlist(cfg, _srv, _buf, _audit);
        bans.RefusedElsewhere = id => allow.Enforced && !allow.IsAllowed(id);
        allow.Add(Alice, "owner", "staff");
        _buf.Append("stdout", Connecting("Joe", Alice));
        _buf.Append("stdout", Connecting("Eve", Bob));
        Assert.Equal(Bob, Assert.Single(AuditOf("allow_kick")).GetProperty("steamid").GetString());
        Assert.Empty(AuditOf("full_kick"));
    }
}
