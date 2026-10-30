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
        Assert.False(r.Trusted);
    }

    [Fact]
    public void TheEnginesOwnJoinLineIsTrusted()
    {
        var r = _p.Classify($"01:20:36 Generic  Joe The Builder [{Real}] is connecting");
        Assert.True(r.Trusted);
        // sbox pads its output lines.
        Assert.True(_p.Classify($"01:20:36 Generic  Joe [{Real}] is connecting                 ").Trusted);
    }

    // What a dedicated server logs for chat: "<name>: <message>". The message is the player's own text.
    [Theory]
    [InlineData("01:20:36 Generic  Joe: x [76561198000000002] is connecting")]
    [InlineData("01:20:36 Generic  Joe: [76561198000000002] is connecting")]
    [InlineData("01:20:36 Generic  Joe: 01:20:36 Generic  x [76561198000000002] is connecting")]
    [InlineData("01:20:36 Generic  Server: x [76561198000000002] is connecting")]
    public void AChatLineShapedLikeAJoinIsNotTrusted(string line)
    {
        var r = _p.Classify(line);
        Assert.Equal(PlayerLineParser.Kind.Join, r.Kind);
        Assert.False(r.Trusted);
    }

    [Theory]
    [InlineData("01:20:36 Generic  Joe: x [76561198000000002] disconnected")]
    [InlineData("01:20:36 Generic  Joe: [76561198000000002] disconnected")]
    [InlineData("01:20:36 Generic  Joe: SteamIdSocket - steamid:1: Disconnection (76561198000000002)")]
    [InlineData("> x [76561198000000002] disconnected")]
    public void AChatLineOrEchoShapedLikeALeaveIsIgnored(string line)
    {
        Assert.Equal(PlayerLineParser.Kind.None, _p.Classify(line).Kind);
    }

    // What the console echoes when a command is typed; seen on a real server.
    [Fact]
    public void ATypedCommandEchoIsNotTrusted()
    {
        Assert.False(_p.Classify($"> LabStandIn [{Victim}] is connecting").Trusted);
    }

    // A display name is the player's own text too, printed by every line that names them.
    [Fact]
    public void ANameHoldingAJoinIsNotAJoinInTheirChat()
    {
        var r = _p.Classify($"01:20:36 Generic  a [{Victim}] is connecting: hello");
        Assert.False(r.Kind == PlayerLineParser.Kind.Join && r.Trusted);
    }

    [Fact]
    public void ANameHoldingAJoinIsNotAJoinInAKickNotice()
    {
        var r = _p.Classify($"01:20:36 Generic  Kicked a [{Victim}] is connecting [{Real}] - bad password");
        Assert.False(r.Kind == PlayerLineParser.Kind.Join && r.Trusted);
    }

    [Fact]
    public void ANameHoldingALeaveIsNotALeaveInTheirChat()
    {
        Assert.NotEqual(PlayerLineParser.Kind.Leave, _p.Classify($"01:20:36 Generic  a [{Victim}] disconnected: hello").Kind);
    }

    [Fact]
    public void ANameHoldingAJoinStillJoinsAsThemselves()
    {
        var r = _p.Classify($"01:20:36 Generic  a [{Victim}] is connecting [{Real}] is connecting");
        Assert.Equal(PlayerLineParser.Kind.Join, r.Kind);
        Assert.Equal(Real, r.SteamId);
        Assert.True(r.Trusted);
    }

    [Fact]
    public void ANameHoldingALeaveStillLeavesAsThemselves()
    {
        var r = _p.Classify($"01:20:36 Generic  a [{Victim}] disconnected [{Real}] disconnected");
        Assert.Equal(PlayerLineParser.Kind.Leave, r.Kind);
        Assert.Equal(Real, r.SteamId);
    }

    // The console redraw can split a log line after its prefix; seen on a real server.
    [Fact]
    public void AJoinSplitAfterItsPrefixIsTrustedAndKeepsTheWholeName()
    {
        Assert.Equal(PlayerLineParser.Kind.None, _p.Classify("01:20:36 Generic").Kind);
        var r = _p.Classify($" Joe The Builder [{Real}] is connecting");
        Assert.Equal(PlayerLineParser.Kind.Join, r.Kind);
        Assert.True(r.Trusted);
        Assert.Equal("Joe The Builder", r.Name);
    }

    [Fact]
    public void ASplitOnlyCoversTheVeryNextLine()
    {
        _p.Classify("01:20:36 Generic");
        _p.Classify("something else");
        Assert.False(_p.Classify($"Joe [{Real}] is connecting").Trusted);
    }

    [Fact]
    public void AChatLineSplitAfterItsPrefixIsStillNotTrusted()
    {
        _p.Classify("01:20:36 Generic");
        Assert.False(_p.Classify($"Joe: x [{Victim}] is connecting").Trusted);
    }

    [Fact]
    public void ACustomRegexIsTakenAtItsWord()
    {
        var p = new PlayerLineParser(Configs.Parse(_s.Dir, "--connect-regex", @"(?<steamid>\d{17}) joined", "--disconnect-regex", @"(?<steamid>\d{17}) left: \w+"));
        Assert.True(p.Classify($"{Real} joined").Trusted);
        Assert.Equal(PlayerLineParser.Kind.Leave, p.Classify($"Joe: {Real} left: bye").Kind);
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
