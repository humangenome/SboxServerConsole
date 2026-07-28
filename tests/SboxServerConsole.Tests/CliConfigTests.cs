using Xunit;

namespace SboxServerConsole.Tests;

// Port derivation is the piece every deployment depends on: an installer passes
// --child-args and expects the HTTP and RCON ports to land at +4 and +5.
public class CliConfigTests
{
    [Fact]
    public void PortsDeriveFromTheChildPort()
    {
        using var s = new Scratch();
        var cfg = CliConfig.Parse(new[]
        {
            "--exe", Configs.InertExe,
            "--game-dir", s.Dir,
            "--child-args", "+port 13600 +net_query_port 13601",
        });
        Assert.NotNull(cfg);
        Assert.Equal(13600, cfg!.ChildPort);
        Assert.Equal(13601, cfg.QueryPort);
        Assert.Equal(13604, cfg.ListenPort);
        Assert.Equal(13605, cfg.RconPort);
    }

    [Fact]
    public void QueryPortFallsBackToTheChildPort()
    {
        using var s = new Scratch();
        var cfg = Configs.Parse(s.Dir, "--child-args", "+port 13600");
        Assert.Equal(13600, cfg.QueryPort);
    }

    [Fact]
    public void ExplicitPortsWinOverDerivedOnes()
    {
        using var s = new Scratch();
        var cfg = Configs.Parse(s.Dir,
            "--child-args", "+port 13600",
            "--listen-port", "9001",
            "--rcon-port", "9002",
            "--query-port", "9003");
        Assert.Equal(9001, cfg.ListenPort);
        Assert.Equal(9002, cfg.RconPort);
        Assert.Equal(9003, cfg.QueryPort);
    }

    [Fact]
    public void ChildPortMustBeKnowable()
    {
        using var s = new Scratch();
        Assert.Null(CliConfig.Parse(new[]
        {
            "--exe", Configs.InertExe,
            "--game-dir", s.Dir,
            "--child-args", "+hostname \"no port here\"",
        }));

        var cfg = CliConfig.Parse(new[]
        {
            "--exe", Configs.InertExe,
            "--game-dir", s.Dir,
            "--child-args", "+hostname \"no port here\"",
            "--child-port", "13600",
        });
        Assert.NotNull(cfg);
        Assert.Equal(13604, cfg!.ListenPort);
    }

    [Fact]
    public void RequiredArgsAreEnforced()
    {
        using var s = new Scratch();
        Assert.Null(CliConfig.Parse(Array.Empty<string>()));
        Assert.Null(CliConfig.Parse(new[] { "--game-dir", s.Dir }));
        Assert.Null(CliConfig.Parse(new[] { "--exe", Configs.InertExe }));
        Assert.Null(CliConfig.Parse(new[] { "--exe", Path.Combine(s.Dir, "nope.exe"), "--game-dir", s.Dir }));
        Assert.Null(CliConfig.Parse(new[] { "--exe", Configs.InertExe, "--game-dir", Path.Combine(s.Dir, "nope") }));
        Assert.Null(CliConfig.Parse(new[] { "--exe", Configs.InertExe, "--game-dir", s.Dir, "--child-port" })); // missing value
        Assert.Null(CliConfig.Parse(new[] { "positional", "--exe", Configs.InertExe }));                        // stray arg
        Assert.Null(CliConfig.Parse(new[] { "--help" }));
    }

    [Fact]
    public void NumericOptionsAreClamped()
    {
        using var s = new Scratch();
        Assert.Equal(10, Configs.Parse(s.Dir, "--buffer-size", "1").BufferSize);
        Assert.Equal(10000, Configs.Parse(s.Dir, "--buffer-size", "999999").BufferSize);
        Assert.Equal(500, Configs.Parse(s.Dir, "--buffer-size", "not-a-number").BufferSize);
        Assert.Equal(3600, Configs.Parse(s.Dir, "--query-poll-sec", "99999").QueryPollSeconds);
        Assert.Equal(1, Configs.Parse(s.Dir, "--restart-backoff-sec", "0").RestartBackoffSeconds);
        Assert.Equal(100, Configs.Parse(s.Dir, "--restart-max-attempts", "1000").RestartMaxAttempts);
        Assert.Equal(60, Configs.Parse(s.Dir, "--restart-window-sec", "1").RestartWindowSeconds);
    }

    [Fact]
    public void BareFlagsNeedNoValue()
    {
        using var s = new Scratch();
        var cfg = Configs.Parse(s.Dir, "--dashboard-disabled", "--no-auto-restart", "--rcon-disabled");
        Assert.False(cfg.DashboardEnabled);
        Assert.False(cfg.AutoRestart);
        Assert.True(cfg.RconDisabled);

        var defaults = Configs.Parse(s.Dir);
        Assert.True(defaults.DashboardEnabled);
        Assert.True(defaults.AutoRestart);
        Assert.False(defaults.RconDisabled);
    }

    [Fact]
    public void DefaultsMatchTheDocumentedOnes()
    {
        using var s = new Scratch();
        var cfg = Configs.Parse(s.Dir);
        Assert.Equal("127.0.0.1", cfg.BindAddress);
        Assert.Equal(500, cfg.BufferSize);
        Assert.Equal("quit", cfg.ShutdownCommand);
        Assert.Equal("kick {steamid}", cfg.KickCommandTemplate);
        Assert.Equal(5, cfg.RestartBackoffSeconds);
        Assert.Equal(5, cfg.RestartMaxAttempts);
        Assert.Equal(600, cfg.RestartWindowSeconds);
        Assert.Equal("", cfg.RconPassword);
        Assert.Equal("", cfg.LogsDir);
    }

    [Fact]
    public void LaterFlagsOverrideEarlierOnes()
    {
        using var s = new Scratch();
        var cfg = Configs.Parse(s.Dir, "--bind", "0.0.0.0", "--bind", "127.0.0.1");
        Assert.Equal("127.0.0.1", cfg.BindAddress);
    }
}
