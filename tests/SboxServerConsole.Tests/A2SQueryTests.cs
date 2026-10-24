using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace SboxServerConsole.Tests;

// The A2S client: split answers put back together, and a server that stops answering
// no longer shows its last answer as current.
public class A2SQueryTests
{
    static byte[] SplitPacket(int id, int total, int number, byte[] payload)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF });
        ms.Write(BitConverter.GetBytes(id));
        ms.WriteByte((byte)total);
        ms.WriteByte((byte)number);
        ms.Write(BitConverter.GetBytes((short)1248));
        ms.Write(payload);
        return ms.ToArray();
    }

    static byte[] PlayerAnswer(int count)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x44, (byte)count });
        for (int i = 0; i < count; i++)
        {
            ms.WriteByte((byte)i);
            ms.Write(Encoding.UTF8.GetBytes($"player number {i} with a long name"));
            ms.WriteByte(0);
            ms.Write(BitConverter.GetBytes(i));
            ms.Write(BitConverter.GetBytes(12.5f));
        }
        return ms.ToArray();
    }

    [Fact]
    public void SplitAnswersAreJoinedInPacketOrder()
    {
        var whole = PlayerAnswer(64);
        int third = whole.Length / 3;
        var p0 = SplitPacket(77, 3, 0, whole[..third]);
        var p1 = SplitPacket(77, 3, 1, whole[third..(2 * third)]);
        var p2 = SplitPacket(77, 3, 2, whole[(2 * third)..]);

        // UDP may deliver them in any order.
        var joined = A2SQuery.AssembleSplit(new[] { p2, p0, p1 });
        Assert.NotNull(joined);
        Assert.Equal(whole, joined);
    }

    [Fact]
    public void BrokenSplitAnswersAreRefused()
    {
        var whole = PlayerAnswer(10);
        int half = whole.Length / 2;
        // a missing piece
        Assert.Null(A2SQuery.AssembleSplit(new[] { SplitPacket(5, 2, 0, whole[..half]) }));
        // pieces of two different answers
        Assert.Null(A2SQuery.AssembleSplit(new[] { SplitPacket(5, 2, 0, whole[..half]), SplitPacket(6, 2, 1, whole[half..]) }));
        // compressed (top bit of the id), which this client does not read
        Assert.Null(A2SQuery.AssembleSplit(new[] { SplitPacket(unchecked((int)0x80000005), 2, 0, whole[..half]), SplitPacket(unchecked((int)0x80000005), 2, 1, whole[half..]) }));
    }

    // A fake A2S server on loopback that answers A2S_INFO until told to stop.
    sealed class FakeA2S : IDisposable
    {
        readonly UdpClient _udp;
        readonly Thread _thread;
        volatile bool _answer = true;
        volatile bool _stop;
        public int Port { get; }

        public FakeA2S()
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            _udp.Client.ReceiveTimeout = 200;
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        public void GoQuiet() => _answer = false;

        void Loop()
        {
            while (!_stop)
            {
                try
                {
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    var req = _udp.Receive(ref ep);
                    if (!_answer || req.Length < 5) continue;
                    if (req[4] == (byte)'T') _udp.Send(InfoAnswer(), ep);
                    else if (req[4] == (byte)'U') _udp.Send(PlayerAnswer(2), ep);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        }

        static byte[] InfoAnswer()
        {
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 17 });
            foreach (var s in new[] { "Test Server", "Sandbox", "sbox", "s&box" }) { ms.Write(Encoding.UTF8.GetBytes(s)); ms.WriteByte(0); }
            ms.Write(new byte[] { 0, 0, 2, 32, 0, (byte)'d', (byte)'w', 0, 0 });
            return ms.ToArray();
        }

        public void Dispose() { _stop = true; _udp.Dispose(); }
    }

    [Fact]
    public void AServerThatStopsAnsweringLosesItsLastAnswer()
    {
        using var fake = new FakeA2S();
        using var s = new Scratch();
        var cfg = Configs.Parse(s.Dir, "--query-port", fake.Port.ToString(), "--query-poll-sec", "1");
        using var buf = new MessageBuffer(100);
        using var a2s = new A2SQuery(cfg, buf);
        a2s.Start();

        Assert.True(Wait.For(() => a2s.LatestInfo() is not null, 10000), "no A2S answer was read");
        Assert.Equal("Test Server", a2s.LatestInfo()!.Name);
        Assert.Equal(2, a2s.LatestPlayers().Count);

        fake.GoQuiet();
        Assert.True(Wait.For(() => a2s.LatestInfo() is null, 15000), "the last answer was still shown after the server went quiet");
        Assert.Empty(a2s.LatestPlayers());
    }
}
