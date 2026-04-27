using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SboxServerConsole;

// Source RCON-compatible TCP listener. Speaks the Valve binary protocol so
// every existing RCON tool (mcrcon, BattleMetrics, GameDig, RustyConnector,
// Pterodactyl, ...) can connect with zero changes. Bridges directly to
// ServerProcess.TrySendCommand and harvests the per-call output window the
// same way HTTP /execute?collect=1 does.
//
// Protocol (little-endian):
//   int32 size  (length of remainder, = 4 + 4 + body + 1 + 1)
//   int32 id    (client-chosen, mirrored in response)
//   int32 type  (3=auth req, 2=auth resp / exec req, 0=response value)
//   bytes body  (NUL-terminated)
//   byte  pad   (NUL — second terminator)
//
// Auth: client sends type=3 with password body; server replies empty
// SERVERDATA_RESPONSE_VALUE (mcrcon expects this), then SERVERDATA_AUTH_RESPONSE
// with id mirrored on success or id=-1 on failure.
//
// Exec: client sends type=2 with command body; server collects output for
// ResponseCollectMs and returns as a single SERVERDATA_RESPONSE_VALUE.
// Body is split into multiple response packets if the output exceeds
// MaxBodyBytes.
public sealed class RconServer : IDisposable
{
    const int MaxBodyBytes = 4096;
    const int ResponseCollectMs = 250;
    const int MaxConcurrentClients = 8;
    const int IdleTimeoutMs = 600_000;
    static readonly TimeSpan AcceptCancelPoll = TimeSpan.FromMilliseconds(200);

    const int SERVERDATA_RESPONSE_VALUE = 0;
    const int SERVERDATA_AUTH_RESPONSE = 2;
    const int SERVERDATA_EXECCOMMAND = 2;
    const int SERVERDATA_AUTH = 3;

    readonly CliConfig _cfg;
    readonly ServerProcess _server;
    readonly MessageBuffer _buffer;
    readonly AuditLog _audit;
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    int _activeClients;
    Thread? _acceptThread;

    public RconServer(CliConfig cfg, ServerProcess server, MessageBuffer buffer, AuditLog audit)
    {
        _cfg = cfg;
        _server = server;
        _buffer = buffer;
        _audit = audit;
        IPAddress addr = ResolveBind(cfg.BindAddress);
        _listener = new TcpListener(addr, cfg.RconPort);
    }

    static IPAddress ResolveBind(string s) => s switch
    {
        null or "" => IPAddress.Loopback,
        "0.0.0.0" or "*" or "+" => IPAddress.Any,
        _ => IPAddress.TryParse(s, out var ip) ? ip : IPAddress.Loopback,
    };

    public bool Enabled => !string.IsNullOrEmpty(_cfg.RconPassword) && _cfg.RconPort > 0;

    public void Start()
    {
        if (!Enabled) return;
        _listener.Start();
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rcon-accept" };
        _acceptThread.Start();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _acceptThread?.Join(2000); } catch { }
    }

    void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!_listener.Pending())
                {
                    Thread.Sleep(AcceptCancelPoll);
                    continue;
                }
                var client = _listener.AcceptTcpClient();
                if (Interlocked.Increment(ref _activeClients) > MaxConcurrentClients)
                {
                    Interlocked.Decrement(ref _activeClients);
                    try { client.Close(); } catch { }
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    _buffer.Append("system", $"rcon accept error: {ex.Message}");
            }
        }
    }

    void HandleClient(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = IdleTimeoutMs;
            client.SendTimeout = 5000;
            using var net = client.GetStream();
            bool authed = false;
            while (!_cts.IsCancellationRequested)
            {
                if (!TryReadPacket(net, out int id, out int type, out string body))
                    break;
                if (!authed)
                {
                    if (type != SERVERDATA_AUTH)
                    {
                        SendPacket(net, -1, SERVERDATA_AUTH_RESPONSE, "");
                        break;
                    }
                    bool ok = ConstantTimeEquals(body, _cfg.RconPassword);
                    // mcrcon expects the empty mirror RESPONSE_VALUE before AUTH_RESPONSE.
                    SendPacket(net, id, SERVERDATA_RESPONSE_VALUE, "");
                    SendPacket(net, ok ? id : -1, SERVERDATA_AUTH_RESPONSE, "");
                    _audit.Record("rcon_auth", new Dictionary<string, object?>
                    {
                        ["client"] = remote,
                        ["success"] = ok,
                    });
                    if (!ok) break;
                    authed = true;
                    continue;
                }
                if (type != SERVERDATA_EXECCOMMAND)
                {
                    SendPacket(net, id, SERVERDATA_RESPONSE_VALUE, "");
                    continue;
                }
                if (string.IsNullOrEmpty(body))
                {
                    SendPacket(net, id, SERVERDATA_RESPONSE_VALUE, "");
                    continue;
                }
                long preSeq = _buffer.LastSeq;
                bool sent = _server.TrySendCommand(body);
                _audit.Record("rcon_execute", new Dictionary<string, object?>
                {
                    ["client"] = remote,
                    ["cmd"] = body,
                    ["success"] = sent,
                });
                if (!sent)
                {
                    SendPacket(net, id, SERVERDATA_RESPONSE_VALUE, "child not running");
                    continue;
                }
                Thread.Sleep(ResponseCollectMs);
                var collected = _buffer.SinceSeq(preSeq);
                var sb = new StringBuilder();
                foreach (var e in collected)
                {
                    if (e.Stream == "input") continue;
                    sb.Append(e.Line).Append('\n');
                }
                SendBodyChunked(net, id, sb.ToString());
            }
        }
        catch (Exception ex)
        {
            _buffer.Append("system", $"rcon client {remote} error: {ex.Message}");
        }
        finally
        {
            try { client.Close(); } catch { }
            Interlocked.Decrement(ref _activeClients);
        }
    }

    void SendBodyChunked(NetworkStream net, int id, string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            SendPacket(net, id, SERVERDATA_RESPONSE_VALUE, "");
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(body);
        for (int o = 0; o < bytes.Length; o += MaxBodyBytes)
        {
            int len = Math.Min(MaxBodyBytes, bytes.Length - o);
            string chunk = Encoding.UTF8.GetString(bytes, o, len);
            SendPacket(net, id, SERVERDATA_RESPONSE_VALUE, chunk);
        }
    }

    static bool TryReadPacket(NetworkStream net, out int id, out int type, out string body)
    {
        id = 0; type = 0; body = "";
        var hdr = new byte[4];
        if (!ReadExact(net, hdr, 0, 4)) return false;
        int size = BitConverter.ToInt32(hdr, 0);
        if (size < 10 || size > MaxBodyBytes + 32) return false;
        var buf = new byte[size];
        if (!ReadExact(net, buf, 0, size)) return false;
        id = BitConverter.ToInt32(buf, 0);
        type = BitConverter.ToInt32(buf, 4);
        int bodyEnd = 8;
        while (bodyEnd < buf.Length && buf[bodyEnd] != 0) bodyEnd++;
        body = Encoding.UTF8.GetString(buf, 8, bodyEnd - 8);
        return true;
    }

    static bool ReadExact(NetworkStream net, byte[] dst, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = net.Read(dst, offset + total, count - total);
            if (n <= 0) return false;
            total += n;
        }
        return true;
    }

    static void SendPacket(NetworkStream net, int id, int type, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        int size = 4 + 4 + bodyBytes.Length + 2;
        var pkt = new byte[4 + size];
        Buffer.BlockCopy(BitConverter.GetBytes(size), 0, pkt, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(id), 0, pkt, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(type), 0, pkt, 8, 4);
        if (bodyBytes.Length > 0) Buffer.BlockCopy(bodyBytes, 0, pkt, 12, bodyBytes.Length);
        // Last two bytes already 0 (NUL body terminator + NUL pad).
        net.Write(pkt, 0, pkt.Length);
        net.Flush();
    }

    static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
