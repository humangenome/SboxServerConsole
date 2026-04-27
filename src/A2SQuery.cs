using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SboxServerConsole;

// Source A2S query client. Speaks the Valve A2S_INFO + A2S_PLAYER protocol
// against the child's UDP query port (sbox uses +net_query_port). Lets us
// surface authoritative server name / map / player count without scraping
// stdout. Server browsers, BattleMetrics, and GameDig speak this protocol —
// we use it as the canonical "what's the player count" source.
//
// Protocol summary:
//   A2S_INFO  : 0xFF 0xFF 0xFF 0xFF 'T' "Source Engine Query" 0x00
//               + challenge bytes if server replied with S2C_CHALLENGE (0x41)
//   A2S_PLAYER: 0xFF 0xFF 0xFF 0xFF 'U' <i32 challenge>
//               (challenge fetched via 'U' + 0xFFFFFFFF)
public sealed class A2SQuery : IDisposable
{
    public sealed record InfoSnapshot(
        string Name,
        string Map,
        string Folder,
        string Game,
        int Players,
        int MaxPlayers,
        int Bots,
        DateTime FetchedAt);

    public sealed record PlayerEntry(string Name, int Score, float Duration);

    readonly CliConfig _cfg;
    readonly MessageBuffer _buffer;
    readonly CancellationTokenSource _cts = new();
    readonly object _lock = new();
    InfoSnapshot? _latestInfo;
    IReadOnlyList<PlayerEntry> _latestPlayers = Array.Empty<PlayerEntry>();
    Thread? _thread;

    public A2SQuery(CliConfig cfg, MessageBuffer buffer)
    {
        _cfg = cfg;
        _buffer = buffer;
    }

    public bool Enabled => _cfg.QueryPort > 0 && _cfg.QueryPollSeconds > 0;

    public void Start()
    {
        if (!Enabled) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "a2s-poller" };
        _thread.Start();
    }

    public InfoSnapshot? LatestInfo() { lock (_lock) return _latestInfo; }
    public IReadOnlyList<PlayerEntry> LatestPlayers() { lock (_lock) return _latestPlayers; }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _thread?.Join(2000); } catch { }
    }

    void Loop()
    {
        var period = TimeSpan.FromSeconds(_cfg.QueryPollSeconds);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Loopback, _cfg.QueryPort);
                using var udp = new UdpClient { Client = { ReceiveTimeout = 1500, SendTimeout = 1500 } };
                udp.Connect(endpoint);
                var info = QueryInfo(udp);
                IReadOnlyList<PlayerEntry> players = info is null ? Array.Empty<PlayerEntry>() : QueryPlayers(udp);
                lock (_lock)
                {
                    if (info is not null) _latestInfo = info;
                    _latestPlayers = players;
                }
            }
            catch (Exception ex)
            {
                _buffer.Append("system", $"a2s poll error: {ex.Message}");
            }

            var deadline = DateTime.UtcNow + period;
            while (DateTime.UtcNow < deadline && !_cts.IsCancellationRequested) Thread.Sleep(250);
        }
    }

    static byte[] BuildInfoRequest(int? challenge)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(unchecked((int)0xFFFFFFFF));
        bw.Write((byte)'T');
        var payload = Encoding.ASCII.GetBytes("Source Engine Query");
        bw.Write(payload);
        bw.Write((byte)0);
        if (challenge.HasValue) bw.Write(challenge.Value);
        return ms.ToArray();
    }

    InfoSnapshot? QueryInfo(UdpClient udp)
    {
        udp.Send(BuildInfoRequest(null));
        var ep = (IPEndPoint?)null!;
        byte[] resp = udp.Receive(ref ep);
        if (resp.Length < 5) return null;
        // Skip 4-byte header (0xFFFFFFFF), look at response type byte.
        if (resp[0] != 0xFF || resp[1] != 0xFF || resp[2] != 0xFF || resp[3] != 0xFF) return null;
        if (resp[4] == 0x41 && resp.Length >= 9) // S2C_CHALLENGE
        {
            int challenge = BitConverter.ToInt32(resp, 5);
            udp.Send(BuildInfoRequest(challenge));
            resp = udp.Receive(ref ep);
            if (resp.Length < 5 || resp[4] != 0x49) return null;
        }
        else if (resp[4] != 0x49) return null;
        return ParseInfo(resp);
    }

    static InfoSnapshot? ParseInfo(byte[] resp)
    {
        // Layout (after 4-byte header + type 'I' (0x49)):
        //   byte protocol, str name, str map, str folder, str game,
        //   short app_id, byte players, byte max_players, byte bots, ...
        try
        {
            int o = 5;
            o += 1;                        // protocol
            string name = ReadCString(resp, ref o);
            string map = ReadCString(resp, ref o);
            string folder = ReadCString(resp, ref o);
            string game = ReadCString(resp, ref o);
            o += 2;                        // app id
            int players = resp[o++];
            int max = resp[o++];
            int bots = resp[o++];
            return new InfoSnapshot(name, map, folder, game, players, max, bots, DateTime.UtcNow);
        }
        catch { return null; }
    }

    IReadOnlyList<PlayerEntry> QueryPlayers(UdpClient udp)
    {
        // First request: challenge sentinel 0xFFFFFFFF -> server replies S2C_CHALLENGE
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(unchecked((int)0xFFFFFFFF));
        bw.Write((byte)'U');
        bw.Write(unchecked((int)0xFFFFFFFF));
        udp.Send(ms.ToArray());
        var ep = (IPEndPoint?)null!;
        byte[] resp = udp.Receive(ref ep);
        if (resp.Length < 9) return Array.Empty<PlayerEntry>();
        int challenge;
        if (resp[4] == 0x41) challenge = BitConverter.ToInt32(resp, 5);
        else if (resp[4] == 0x44) return ParsePlayers(resp);
        else return Array.Empty<PlayerEntry>();

        ms = new MemoryStream();
        bw = new BinaryWriter(ms);
        bw.Write(unchecked((int)0xFFFFFFFF));
        bw.Write((byte)'U');
        bw.Write(challenge);
        udp.Send(ms.ToArray());
        resp = udp.Receive(ref ep);
        if (resp.Length < 6 || resp[4] != 0x44) return Array.Empty<PlayerEntry>();
        return ParsePlayers(resp);
    }

    static IReadOnlyList<PlayerEntry> ParsePlayers(byte[] resp)
    {
        try
        {
            int o = 5;
            int n = resp[o++];
            var list = new List<PlayerEntry>(n);
            for (int i = 0; i < n; i++)
            {
                o += 1; // index
                string name = ReadCString(resp, ref o);
                int score = BitConverter.ToInt32(resp, o); o += 4;
                float dur = BitConverter.ToSingle(resp, o); o += 4;
                list.Add(new PlayerEntry(name, score, dur));
            }
            return list;
        }
        catch { return Array.Empty<PlayerEntry>(); }
    }

    static string ReadCString(byte[] buf, ref int offset)
    {
        int start = offset;
        while (offset < buf.Length && buf[offset] != 0) offset++;
        var s = Encoding.UTF8.GetString(buf, start, offset - start);
        if (offset < buf.Length) offset++;
        return s;
    }
}
