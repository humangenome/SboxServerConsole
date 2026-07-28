using System.Net;
using System.Net.Sockets;
using Xunit;

namespace SboxServerConsole.Tests;

// The RCON listener binds only when a password is configured AND the port is
// positive AND --rcon-disabled was not passed. Getting that wrong either exposes
// an unauthenticated command port or silently drops RCON for everyone, so all of
// the combinations are pinned here.
public class RconServerTests
{
    sealed class Rig : IDisposable
    {
        public Scratch Scratch { get; } = new();
        public CliConfig Cfg { get; }
        public MessageBuffer Buffer { get; }
        public ServerProcess Server { get; }
        public AuditLog Audit { get; }
        public RconServer Rcon { get; }

        public Rig(params string[] extra)
        {
            var args = new List<string> { "--audit-log", Scratch.File("audit.jsonl") };
            args.AddRange(extra);
            Cfg = Configs.Parse(Scratch.Dir, args.ToArray());
            Buffer = new MessageBuffer(Cfg.BufferSize);
            Server = new ServerProcess(Cfg, Buffer);
            Audit = new AuditLog(Cfg.AuditLogPath);
            Rcon = new RconServer(Cfg, Server, Buffer, Audit);
        }

        public void Dispose()
        {
            Rcon.Dispose();
            Server.Dispose();
            Audit.Dispose();
            Buffer.Dispose();
            Scratch.Dispose();
        }
    }

    static bool PortAccepts(int port)
    {
        try
        {
            using var c = new TcpClient();
            var task = c.ConnectAsync(IPAddress.Loopback, port);
            if (!task.Wait(TimeSpan.FromSeconds(3))) return false;
            return c.Connected;
        }
        catch (Exception) { return false; }
    }

    [Fact]
    public void BindsOnlyWithBothAPasswordAndAPort()
    {
        int port = Ports.Free();

        using (var rig = new Rig("--rcon-password", "secret", "--rcon-port", port.ToString()))
        {
            Assert.True(rig.Rcon.Enabled);
            rig.Rcon.Start();
            Assert.True(PortAccepts(port), "password + port should bind");
        }

        using (var rig = new Rig("--rcon-password", "", "--rcon-port", port.ToString()))
        {
            Assert.False(rig.Rcon.Enabled);
            rig.Rcon.Start();
            Assert.False(PortAccepts(port), "empty password must not bind");
        }

        using (var rig = new Rig("--rcon-password", "secret", "--rcon-port", "0"))
        {
            Assert.False(rig.Rcon.Enabled);
            rig.Rcon.Start(); // must be a no-op, not an ephemeral-port bind
        }

        using (var rig = new Rig("--rcon-password", "", "--rcon-port", "0"))
        {
            Assert.False(rig.Rcon.Enabled);
            rig.Rcon.Start();
        }
    }

    [Fact]
    public void RconDisabledFlagWinsOverAValidConfig()
    {
        int port = Ports.Free();
        using var rig = new Rig("--rcon-password", "secret", "--rcon-port", port.ToString(), "--rcon-disabled");
        Assert.False(rig.Rcon.Enabled);
        rig.Rcon.Start();
        Assert.False(PortAccepts(port), "--rcon-disabled must not bind");
    }

    [Fact]
    public async Task HttpApiStillWorksWithRconDisabled()
    {
        // --rcon-disabled turns off the TCP listener only; the password still
        // authenticates the HTTP API.
        using var h = ApiHost.Create("--rcon-disabled");
        Assert.False(h.Rcon.Enabled);
        var (status, _) = await h.Call("GET", "/status");
        Assert.Equal(200, status);
    }

    // ---- wire protocol ----

    static TcpClient Connect(int port)
    {
        var c = new TcpClient();
        c.Connect(IPAddress.Loopback, port);
        c.NoDelay = true;
        c.ReceiveTimeout = 10000;
        return c;
    }

    [Fact]
    public void GoodPasswordMirrorsTheRequestId()
    {
        int port = Ports.Free();
        using var rig = new Rig("--rcon-password", "secret", "--rcon-port", port.ToString());
        rig.Rcon.Start();

        using var client = Connect(port);
        using var net = client.GetStream();
        RconWire.Send(net, 7, RconWire.Auth, "secret");

        // mcrcon expects an empty RESPONSE_VALUE first, then the AUTH_RESPONSE.
        Assert.True(RconWire.TryRead(net, out int id1, out int type1, out string body1));
        Assert.Equal(RconWire.ResponseValue, type1);
        Assert.Equal(7, id1);
        Assert.Equal("", body1);

        Assert.True(RconWire.TryRead(net, out int id2, out int type2, out _));
        Assert.Equal(RconWire.AuthResponse, type2);
        Assert.Equal(7, id2);
    }

    [Fact]
    public void BadPasswordAnswersMinusOneAndClosesTheConnection()
    {
        int port = Ports.Free();
        using var rig = new Rig("--rcon-password", "secret", "--rcon-port", port.ToString());
        rig.Rcon.Start();

        using var client = Connect(port);
        using var net = client.GetStream();
        RconWire.Send(net, 11, RconWire.Auth, "wrong");

        Assert.True(RconWire.TryRead(net, out _, out int type1, out _));
        Assert.Equal(RconWire.ResponseValue, type1);
        Assert.True(RconWire.TryRead(net, out int id2, out int type2, out _));
        Assert.Equal(RconWire.AuthResponse, type2);
        Assert.Equal(-1, id2);

        // Server hangs up after a failed auth.
        Assert.False(RconWire.TryRead(net, out _, out _, out _));
    }

    [Fact]
    public void SameLengthWrongPasswordStillFails()
    {
        int port = Ports.Free();
        using var rig = new Rig("--rcon-password", "secret", "--rcon-port", port.ToString());
        rig.Rcon.Start();

        using var client = Connect(port);
        using var net = client.GetStream();
        RconWire.Send(net, 3, RconWire.Auth, "sekret"); // same length as "secret"

        Assert.True(RconWire.TryRead(net, out _, out _, out _));
        Assert.True(RconWire.TryRead(net, out int id, out int type, out _));
        Assert.Equal(RconWire.AuthResponse, type);
        Assert.Equal(-1, id);
    }

    [Fact]
    public void CommandBeforeAuthIsRefused()
    {
        int port = Ports.Free();
        using var rig = new Rig("--rcon-password", "secret", "--rcon-port", port.ToString());
        rig.Rcon.Start();

        using var client = Connect(port);
        using var net = client.GetStream();
        RconWire.Send(net, 5, RconWire.ExecCommand, "status");

        Assert.True(RconWire.TryRead(net, out int id, out int type, out _));
        Assert.Equal(RconWire.AuthResponse, type);
        Assert.Equal(-1, id);
        Assert.False(RconWire.TryRead(net, out _, out _, out _));
    }

    [Fact]
    public void AuthedCommandWithNoChildReportsIt()
    {
        int port = Ports.Free();
        using var rig = new Rig("--rcon-password", "secret", "--rcon-port", port.ToString());
        rig.Rcon.Start();

        using var client = Connect(port);
        using var net = client.GetStream();
        RconWire.Send(net, 1, RconWire.Auth, "secret");
        Assert.True(RconWire.TryRead(net, out _, out _, out _));
        Assert.True(RconWire.TryRead(net, out _, out _, out _));

        RconWire.Send(net, 2, RconWire.ExecCommand, "status");
        Assert.True(RconWire.TryRead(net, out int id, out int type, out string body));
        Assert.Equal(RconWire.ResponseValue, type);
        Assert.Equal(2, id);
        Assert.Equal("child not running", body);

        // Both the auth and the command are audited.
        var lines = File.ReadAllLines(rig.Cfg.AuditLogPath).Where(l => l.Length > 0).ToList();
        Assert.Contains(lines, l => l.Contains("\"rcon_auth\"") && l.Contains("\"success\":true"));
        Assert.Contains(lines, l => l.Contains("\"rcon_execute\"") && l.Contains("\"cmd\":\"status\""));
    }

    [Fact]
    public void EmptyCommandBodyIsAnEmptyResponse()
    {
        int port = Ports.Free();
        using var rig = new Rig("--rcon-password", "secret", "--rcon-port", port.ToString());
        rig.Rcon.Start();

        using var client = Connect(port);
        using var net = client.GetStream();
        RconWire.Send(net, 1, RconWire.Auth, "secret");
        Assert.True(RconWire.TryRead(net, out _, out _, out _));
        Assert.True(RconWire.TryRead(net, out _, out _, out _));

        RconWire.Send(net, 9, RconWire.ExecCommand, "");
        Assert.True(RconWire.TryRead(net, out int id, out int type, out string body));
        Assert.Equal(RconWire.ResponseValue, type);
        Assert.Equal(9, id);
        Assert.Equal("", body);
    }
}
