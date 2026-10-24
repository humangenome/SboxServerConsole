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
    // Last poll outcome, so the console gets one line when answers stop and one when
    // they come back, not a line every poll while a server loads or sits stopped.
    string? _lastError;
    bool _everAnswered;
    // Polls in a row without an answer. After StaleAfterMisses the last answer is
    // dropped, so /status and /players stop showing a server that went quiet.
    int _misses;
    const int StaleAfterMisses = 2;

    // Raised after every successful A2S_INFO answer.
    public event Action<InfoSnapshot>? OnSnapshot;

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
                if (info is null) NoteFailure("the reply was not an A2S_INFO answer");
                else
                {
                    IReadOnlyList<PlayerEntry> players;
                    try { players = QueryPlayers(udp); }
                    catch (Exception) { players = Array.Empty<PlayerEntry>(); }
                    lock (_lock)
                    {
                        _latestInfo = info;
                        _latestPlayers = players;
                        _misses = 0;
                    }
                    NoteSuccess();
                    try { OnSnapshot?.Invoke(info); } catch { }
                }
            }
            catch (Exception ex)
            {
                NoteFailure(ex.Message);
            }

            var deadline = DateTime.UtcNow + period;
            while (DateTime.UtcNow < deadline && !_cts.IsCancellationRequested) Thread.Sleep(250);
        }
    }

    void NoteFailure(string reason)
    {
        lock (_lock)
        {
            if (++_misses >= StaleAfterMisses)
            {
                _latestInfo = null;
                _latestPlayers = Array.Empty<PlayerEntry>();
            }
        }
        if (_lastError == reason) return;
        _lastError = reason;
        // Until the first answer this is usually just the server still loading.
        _buffer.Append("system", _everAnswered
            ? $"a2s: udp/{_cfg.QueryPort} stopped answering ({reason})"
            : $"a2s: no answer yet on udp/{_cfg.QueryPort} ({reason}); normal while the server loads");
    }

    void NoteSuccess()
    {
        if (_lastError is not null || !_everAnswered)
            _buffer.Append("system", $"a2s: udp/{_cfg.QueryPort} answering");
        _lastError = null;
        _everAnswered = true;
    }

    // One answer. A long answer (a big player list) arrives split over several packets
    // marked 0xFFFFFFFE; they are put back together here, in order.
    static byte[] Receive(UdpClient udp)
    {
        var ep = (IPEndPoint?)null!;
        byte[] first = udp.Receive(ref ep);
        if (!IsSplit(first)) return first;
        var parts = new List<byte[]> { first };
        int total = first.Length > 8 ? first[8] : 0;
        while (parts.Count < total && parts.Count < 32) parts.Add(udp.Receive(ref ep));
        return AssembleSplit(parts) ?? throw new InvalidDataException("split A2S answer could not be put together");
    }

    static bool IsSplit(byte[] p) => p.Length >= 4 && p[0] == 0xFE && p[1] == 0xFF && p[2] == 0xFF && p[3] == 0xFF;

    // Source split layout: int32 -2, int32 id, byte total, byte number, int16 max size, payload.
    // The payloads joined in packet-number order start with the usual 0xFFFFFFFF header.
    // Compressed answers (id with the top bit set) are not supported.
    internal static byte[]? AssembleSplit(IReadOnlyList<byte[]> packets)
    {
        const int Header = 12;
        if (packets.Count == 0) return null;
        var byNumber = new SortedDictionary<int, byte[]>();
        int id = 0, total = 0;
        foreach (var p in packets)
        {
            if (!IsSplit(p) || p.Length < Header) return null;
            int pid = BitConverter.ToInt32(p, 4);
            if ((pid & unchecked((int)0x80000000)) != 0) return null;
            if (byNumber.Count == 0) { id = pid; total = p[8]; }
            else if (pid != id || p[8] != total) return null;
            byNumber[p[9]] = p;
        }
        if (total == 0 || byNumber.Count != total) return null;
        var ms = new MemoryStream();
        for (int n = 0; n < total; n++)
        {
            if (!byNumber.TryGetValue(n, out var p)) return null;
            ms.Write(p, Header, p.Length - Header);
        }
        var joined = ms.ToArray();
        return joined.Length >= 5 && joined[0] == 0xFF && joined[1] == 0xFF && joined[2] == 0xFF && joined[3] == 0xFF ? joined : null;
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
        byte[] resp = Receive(udp);
        if (resp.Length < 5) return null;
        // Skip 4-byte header (0xFFFFFFFF), look at response type byte.
        if (resp[0] != 0xFF || resp[1] != 0xFF || resp[2] != 0xFF || resp[3] != 0xFF) return null;
        if (resp[4] == 0x41 && resp.Length >= 9) // S2C_CHALLENGE
        {
            int challenge = BitConverter.ToInt32(resp, 5);
            udp.Send(BuildInfoRequest(challenge));
            resp = Receive(udp);
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
        byte[] resp = Receive(udp);
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
        resp = Receive(udp);
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
