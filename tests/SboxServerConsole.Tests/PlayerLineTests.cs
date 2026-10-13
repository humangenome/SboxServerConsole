using System.Text.Json;
using Xunit;

namespace SboxServerConsole.Tests;

// Join and leave detection, including player names chosen to fool it. Line shapes
// come from the engine source (NetworkSystem.Handshake.cs, NetworkSystem.Connections.cs).
public class PlayerLineTests : IDisposable
{
    const string Real = "76561198000000001";
    const string Victim = "76561198000000002";

    readonly Scratch _s = new();
    readonly CliConfig _cfg;
    readonly PlayerLineParser _p;

    public PlayerLineTests()
    {
        _cfg = Configs.Parse(_s.Dir, "--audit-log", _s.File("audit.jsonl"));
        _p = new PlayerLineParser(_cfg);
    }

    public void Dispose() => _s.Dispose();

    [Fact]
    public void ANameWithSpacesIsCapturedWhole()
    {
        var r = _p.Classify($"01:20:36 Generic  Joe The Builder [{Real}] is connecting");
        Assert.Equal(PlayerLineParser.Kind.Join, r.Kind);
        Assert.Equal(Real, r.SteamId);
        Assert.Equal("Joe The Builder", r.Name);
    }

    [Fact]
    public void WithoutTheTimePrefixTheLastWordIsTheName()
    {
        var r = _p.Classify($"Joe [{Real}] is connecting");
        Assert.Equal(PlayerLineParser.Kind.Join, r.Kind);
        Assert.Equal("Joe", r.Name);
    }

    [Fact]
    public void ANameCannotSmuggleInAnotherSteamIdOnJoin()
    {
        var r = _p.Classify($"01:20:36 Generic  Bob [{Victim}] is connecting [{Real}] is connecting");
        Assert.Equal(PlayerLineParser.Kind.Join, r.Kind);
        Assert.Equal(Real, r.SteamId);
    }

    [Fact]
    public void ANameCannotTurnAJoinIntoALeave()
    {
        var r = _p.Classify($"01:20:36 Generic  Bob [{Victim}] disconnected [{Real}] is connecting");
        Assert.Equal(PlayerLineParser.Kind.Join, r.Kind);
        Assert.Equal(Real, r.SteamId);
    }

    [Fact]
    public void ANameCannotTurnALeaveIntoAJoin()
    {
        var r = _p.Classify($"01:20:36 Generic  Bob [{Victim}] is connecting [{Real}] disconnected");
        Assert.Equal(PlayerLineParser.Kind.Leave, r.Kind);
        Assert.Equal(Real, r.SteamId);
    }

    [Fact]
    public void TheCurrentDisconnectLineMatches()
    {
        var r = _p.Classify($"01:25:00 Generic  Joe [{Real}] disconnected");
        Assert.Equal(PlayerLineParser.Kind.Leave, r.Kind);
        Assert.Equal(Real, r.SteamId);
    }

    [Fact]
    public void KickNoticesAreNeitherJoinNorLeave()
    {
        Assert.Equal(PlayerLineParser.Kind.None, _p.Classify($"01:20:36 Generic  Kicking Joe [{Real}] - banned").Kind);
        Assert.Equal(PlayerLineParser.Kind.None, _p.Classify($"01:20:36 Generic  Joe [{Real}] is connected").Kind);
    }

    [Theory]
    [InlineData("76561198000000001", true)]
    [InlineData("STEAM_0:1:12345", false)]
    [InlineData("7656119800000000", false)]
    [InlineData("76561198000000001\nquit", false)]
    [InlineData("", false)]
    public void OnlyASteamId64IsAcceptedWithTheDefaultRegex(string id, bool ok)
    {
        Assert.Equal(ok, _p.IsUsableId(id, out _));
    }

    [Fact]
    public void ACustomRegexAcceptsOtherIdForms()
    {
        var cfg = Configs.Parse(_s.Dir, "--connect-regex", @"(?<steamid>STEAM_\S+) joined");
        var p = new PlayerLineParser(cfg);
        Assert.True(p.IsUsableId("STEAM_0:1:12345", out _));
        Assert.False(p.IsUsableId("two words", out _));
    }

    [Fact]
    public void ALeaveIsAnnouncedOnceEvenWhenTwoLinesReportIt()
    {
        using var buf = new MessageBuffer(100);
        using var srv = new ServerProcess(_cfg, buf);
        using var audit = new AuditLog(_cfg.AuditLogPath);
        using var discord = new DiscordWebhook("");
        using var bans = new Banlist(_cfg, srv, buf, audit, discord);
        int leaves = 0;
        bans.OnPlayerLeave += _ => leaves++;

        buf.Append("stdout", $"01:20:36 Generic  Joe [{Real}] is connecting");
        buf.Append("stdout", $"SteamIdSocket - steamid:12345: Disconnection ({Real})");
        buf.Append("stdout", $"01:21:00 Generic  Joe [{Real}] disconnected");
        Assert.Equal(1, leaves);
        Assert.Empty(bans.Online());
    }

    [Fact]
    public void TheRosterEmptiesWhenTheServerExits()
    {
        using var buf = new MessageBuffer(100);
        using var srv = new ServerProcess(_cfg, buf);
        using var audit = new AuditLog(_cfg.AuditLogPath);
        using var discord = new DiscordWebhook("");
        using var bans = new Banlist(_cfg, srv, buf, audit, discord);
        buf.Append("stdout", $"01:20:36 Generic  Joe [{Real}] is connecting");
        Assert.Single(bans.Online());
        bans.ClearRoster();
        Assert.Empty(bans.Online());
    }

    [Fact]
    public void BlindJoinDetectionIsReportedOnce()
    {
        using var buf = new MessageBuffer(100);
        using var srv = new ServerProcess(_cfg, buf);
        using var audit = new AuditLog(_cfg.AuditLogPath);
        using var discord = new DiscordWebhook("");
        using var bans = new Banlist(_cfg, srv, buf, audit, discord);
        var t = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        bans.Now = () => t;
        int Warnings() => buf.Tail(50).Count(e => e.Line.Contains("no join line has matched"));

        bans.ObserveA2SPlayers(2);
        t = t.AddMinutes(2);
        bans.ObserveA2SPlayers(2);
        Assert.Equal(0, Warnings());

        t = t.AddMinutes(2);
        bans.ObserveA2SPlayers(2);
        Assert.Equal(1, Warnings());

        t = t.AddMinutes(10);
        bans.ObserveA2SPlayers(3);
        Assert.Equal(1, Warnings());
    }

    [Fact]
    public void ASeenJoinMeansDetectionIsNotBlind()
    {
        using var buf = new MessageBuffer(100);
        using var srv = new ServerProcess(_cfg, buf);
        using var audit = new AuditLog(_cfg.AuditLogPath);
        using var discord = new DiscordWebhook("");
        using var bans = new Banlist(_cfg, srv, buf, audit, discord);
        var t = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        bans.Now = () => t;

        buf.Append("stdout", $"01:20:36 Generic  Joe [{Real}] is connecting");
        bans.ObserveA2SPlayers(1);
        t = t.AddMinutes(30);
        bans.ObserveA2SPlayers(1);
        Assert.DoesNotContain(buf.Tail(50), e => e.Line.Contains("no join line has matched"));
    }
}
