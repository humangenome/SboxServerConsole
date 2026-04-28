using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SboxServerConsole;

public sealed class HttpApi : IDisposable
{
    const int MaxBodyBytes = 4 * 1024;
    const int MaxConcurrentStreams = 16;
    const int StreamChannelCapacity = 256;
    const int ExecuteCollectMs = 250;
    static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(15);

    readonly CliConfig _cfg;
    readonly ServerProcess _server;
    readonly MessageBuffer _buffer;
    readonly AuditLog _audit;
    readonly Banlist _banlist;
    readonly Scheduler _scheduler;
    readonly A2SQuery _a2s;
    readonly LogsBrowser _logs;
    readonly HttpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly DateTime _startedAt = DateTime.UtcNow;
    readonly List<Channel<MessageBuffer.Entry>> _streamChannels = new();
    readonly object _streamLock = new();
    readonly byte[]? _dashboardHtml;
    long _executeTotal;
    long _streamClientsActive;
    Thread? _thread;

    public HttpApi(CliConfig cfg, ServerProcess server, MessageBuffer buffer, AuditLog audit, Banlist banlist, Scheduler scheduler, A2SQuery a2s, LogsBrowser logs)
    {
        _cfg = cfg;
        _server = server;
        _buffer = buffer;
        _audit = audit;
        _banlist = banlist;
        _scheduler = scheduler;
        _a2s = a2s;
        _logs = logs;
        _dashboardHtml = cfg.DashboardEnabled ? LoadDashboardBytes() : null;
        _listener = new HttpListener();
        // HttpListener prefix host: "127.0.0.1" works literally; "0.0.0.0" must be translated to "+"
        // (namespace-reservation wildcard) per Microsoft's HttpListener prefix docs.
        string host = cfg.BindAddress switch
        {
            null or "" => "127.0.0.1",
            "0.0.0.0" or "*" or "+" => "+",
            _ => cfg.BindAddress,
        };
        _listener.Prefixes.Add($"http://{host}:{cfg.ListenPort}/");
        _buffer.OnAppend += FanOutToStreams;
    }

    public void Start()
    {
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "sbox-console-http" };
        _thread.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        _buffer.OnAppend -= FanOutToStreams;
        try { _listener.Stop(); } catch { }
        lock (_streamLock)
        {
            foreach (var ch in _streamChannels) ch.Writer.TryComplete();
            _streamChannels.Clear();
        }
    }

    public void Dispose() => Stop();

    void FanOutToStreams(MessageBuffer.Entry e)
    {
        // Never block the producer (stdio capture). Drop the entry for any client whose channel is full.
        Channel<MessageBuffer.Entry>[] snapshot;
        lock (_streamLock) snapshot = _streamChannels.ToArray();
        foreach (var ch in snapshot) ch.Writer.TryWrite(e);
    }

    void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            ThreadPool.QueueUserWorkItem(_ => HandleSafe(ctx));
        }
    }

    void HandleSafe(HttpListenerContext ctx)
    {
        try { Handle(ctx); }
        catch (Exception ex)
        {
            try { Write(ctx, 500, "application/json", $"{{\"error\":{JsonEncodedString(ex.Message)}}}"); } catch { }
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path == "/" || path == "/index.html") { Dashboard(ctx); return; }
        if (path == "/health") { Health(ctx); return; }
        if (path == "/version") { Version(ctx); return; }
        if (path == "/history") { RequireAuth(ctx, () => History(ctx)); return; }
        if (path == "/execute") { RequireAuth(ctx, () => Execute(ctx)); return; }
        if (path == "/chat") { RequireAuth(ctx, () => Chat(ctx)); return; }
        if (path == "/stream") { RequireAuth(ctx, () => Stream(ctx)); return; }
        if (path == "/status") { RequireAuth(ctx, () => Status(ctx)); return; }
        if (path == "/players") { RequireAuth(ctx, () => Players(ctx)); return; }
        if (path == "/bans") { RequireAuth(ctx, () => Bans(ctx)); return; }
        if (path.StartsWith("/bans/", StringComparison.Ordinal)) { RequireAuth(ctx, () => BanOne(ctx, path[6..])); return; }
        if (path == "/scheduler") { RequireAuth(ctx, () => SchedulerList(ctx)); return; }
        if (path.StartsWith("/scheduler/", StringComparison.Ordinal)) { RequireAuth(ctx, () => SchedulerItem(ctx, path[11..])); return; }
        if (path == "/server/start") { RequireAuth(ctx, () => ServerStart(ctx)); return; }
        if (path == "/server/stop") { RequireAuth(ctx, () => ServerStop(ctx)); return; }
        if (path == "/server/restart") { RequireAuth(ctx, () => ServerRestart(ctx)); return; }
        if (path == "/logs") { RequireAuth(ctx, () => LogsList(ctx)); return; }
        if (path.StartsWith("/logs/", StringComparison.Ordinal)) { RequireAuth(ctx, () => LogsTail(ctx, path[6..])); return; }

        Write(ctx, 404, "application/json", "{\"error\":\"not found\"}");
    }

    void RequireAuth(HttpListenerContext ctx, Action next)
    {
        if (string.IsNullOrEmpty(_cfg.RconPassword))
        {
            Write(ctx, 503, "application/json", "{\"error\":\"rcon_password not configured\"}");
            return;
        }
        string? supplied = ctx.Request.Headers["X-RCON-Password"];
        if (supplied is null)
        {
            var authz = ctx.Request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authz) && authz.StartsWith("Bearer ", StringComparison.Ordinal))
                supplied = authz.Substring(7);
        }
        if (supplied is null && ctx.Request.QueryString["password"] is { } qp) supplied = qp;
        if (!ConstantTimeEquals(supplied ?? "", _cfg.RconPassword))
        {
            Write(ctx, 401, "application/json", "{\"error\":\"unauthorized\"}");
            return;
        }
        next();
    }

    void Health(HttpListenerContext ctx)
    {
        var json = $$"""
            {"ok":true,"uptime_sec":{{(int)(DateTime.UtcNow - _startedAt).TotalSeconds}},"child_pid":{{_server.ChildPid}},"child_alive":{{(_server.IsAlive ? "true" : "false")}}}
            """;
        Write(ctx, 200, "application/json", json);
    }

    void Version(HttpListenerContext ctx)
    {
        var asm = typeof(HttpApi).Assembly;
        string ver = asm.GetName().Version?.ToString() ?? "0.0.0";
        var json = $$"""
            {"sidecar":"SboxServerConsole","version":"{{ver}}","child_pid":{{_server.ChildPid}},"child_alive":{{(_server.IsAlive ? "true" : "false")}}}
            """;
        Write(ctx, 200, "application/json", json);
    }

    void History(HttpListenerContext ctx)
    {
        int count = 100;
        if (int.TryParse(ctx.Request.QueryString["count"], out int c)) count = Math.Clamp(c, 1, _cfg.BufferSize);
        var entries = _buffer.Tail(count);
        var sb = new StringBuilder("{\"entries\":[");
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"seq\":").Append(e.SeqNo)
              .Append(",\"at\":\"").Append(e.UtcAt.ToString("o")).Append('"')
              .Append(",\"stream\":\"").Append(e.Stream).Append('"')
              .Append(",\"line\":").Append(JsonEncodedString(e.Line)).Append('}');
        }
        sb.Append("]}");
        Write(ctx, 200, "application/json", sb.ToString());
    }

    void Execute(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            Write(ctx, 405, "application/json", "{\"error\":\"POST required\"}");
            return;
        }
        if (ctx.Request.ContentLength64 > MaxBodyBytes)
        {
            Write(ctx, 413, "application/json", "{\"error\":\"body too large\"}");
            return;
        }
        var raw = new byte[MaxBodyBytes + 1];
        int total = 0;
        var input = ctx.Request.InputStream;
        while (total <= MaxBodyBytes)
        {
            int n = input.Read(raw, total, raw.Length - total);
            if (n <= 0) break;
            total += n;
        }
        if (total > MaxBodyBytes) { Write(ctx, 413, "application/json", "{\"error\":\"body too large\"}"); return; }
        string body = Encoding.UTF8.GetString(raw, 0, total);
        string? cmd;
        try
        {
            using var doc = JsonDocument.Parse(body);
            cmd = doc.RootElement.TryGetProperty("cmd", out var v) ? v.GetString() : null;
        }
        catch (JsonException) { Write(ctx, 400, "application/json", "{\"error\":\"invalid json\"}"); return; }
        if (string.IsNullOrWhiteSpace(cmd)) { Write(ctx, 400, "application/json", "{\"error\":\"cmd required\"}"); return; }
        if (cmd.Length > 1024) { Write(ctx, 400, "application/json", "{\"error\":\"cmd too long\"}"); return; }
        if (cmd.Contains('\n') || cmd.Contains('\r')) { Write(ctx, 400, "application/json", "{\"error\":\"cmd may not contain newline\"}"); return; }

        bool collect = ctx.Request.QueryString["collect"] is { } cv
            && (cv == "1" || cv.Equals("true", StringComparison.OrdinalIgnoreCase));
        long preSeq = _buffer.LastSeq;

        bool ok = _server.TrySendCommand(cmd);
        Interlocked.Increment(ref _executeTotal);
        _audit.Record("execute", new Dictionary<string, object?>
        {
            ["cmd"] = cmd,
            ["client_ip"] = ctx.Request.RemoteEndPoint?.Address.ToString(),
            ["success"] = ok,
        });

        if (!ok)
        {
            Write(ctx, 503, "application/json", "{\"error\":\"child not running\"}");
            return;
        }
        if (!collect)
        {
            Write(ctx, 200, "application/json", "{\"ok\":true}");
            return;
        }
        Thread.Sleep(ExecuteCollectMs);
        var collected = _buffer.SinceSeq(preSeq);
        var sb2 = new StringBuilder("{\"ok\":true,\"output\":[");
        for (int i = 0; i < collected.Count; i++)
        {
            var e = collected[i];
            if (i > 0) sb2.Append(',');
            sb2.Append("{\"seq\":").Append(e.SeqNo)
              .Append(",\"stream\":\"").Append(e.Stream).Append('"')
              .Append(",\"line\":").Append(JsonEncodedString(e.Line)).Append('}');
        }
        sb2.Append("]}");
        Write(ctx, 200, "application/json", sb2.ToString());
    }

    void Chat(HttpListenerContext ctx)
    {
        // s&box's `say` ConCommand IS the documented chat-broadcast path
        // but the engine's command parser (Facepunch/sbox-public#2507) splits
        // arguments at any Unicode whitespace-class char even when quoted, so a
        // literal `say "hello world"` only delivers "hello" to the handler.
        // U+00A0 (NBSP) was tried in v2.0.6 and also got eaten because .NET's
        // char.IsWhiteSpace classifies all Zs-category code points as whitespace.
        // Workaround: substitute ASCII spaces with U+00B7 (middle-dot) — Unicode
        // category Po (Punctuation/Other), guaranteed not whitespace under any
        // tokenizer, renders as a visible word separator in the chat client.
        if (ctx.Request.HttpMethod != "POST")
        {
            Write(ctx, 405, "application/json", "{\"error\":\"POST required\"}");
            return;
        }
        var body = ReadBody(ctx);
        if (body is null) return;
        string? text;
        try
        {
            using var doc = JsonDocument.Parse(body);
            text = doc.RootElement.TryGetProperty("text", out var v) ? v.GetString() : null;
        }
        catch (JsonException) { Write(ctx, 400, "application/json", "{\"error\":\"invalid json\"}"); return; }
        if (string.IsNullOrWhiteSpace(text)) { Write(ctx, 400, "application/json", "{\"error\":\"text required\"}"); return; }
        if (text.Length > 512) { Write(ctx, 400, "application/json", "{\"error\":\"text too long (512 char max)\"}"); return; }
        if (text.Contains('\n') || text.Contains('\r')) { Write(ctx, 400, "application/json", "{\"error\":\"text may not contain newline\"}"); return; }

        string transcribed = text.Replace(' ', '·');
        string cmd = "say " + transcribed;
        bool ok = _server.TrySendCommand(cmd);
        Interlocked.Increment(ref _executeTotal);
        _audit.Record("chat", new Dictionary<string, object?>
        {
            ["text"] = text,
            ["client_ip"] = ctx.Request.RemoteEndPoint?.Address.ToString(),
            ["success"] = ok,
        });
        if (!ok) { Write(ctx, 503, "application/json", "{\"error\":\"child not running\"}"); return; }
        Write(ctx, 200, "application/json", "{\"ok\":true}");
    }

    void Stream(HttpListenerContext ctx)
    {
        Channel<MessageBuffer.Entry> ch;
        lock (_streamLock)
        {
            if (_streamChannels.Count >= MaxConcurrentStreams)
            {
                Write(ctx, 429, "application/json", $"{{\"error\":\"max {MaxConcurrentStreams} concurrent stream clients\"}}");
                return;
            }
            ch = Channel.CreateBounded<MessageBuffer.Entry>(new BoundedChannelOptions(StreamChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            _streamChannels.Add(ch);
        }

        Interlocked.Increment(ref _streamClientsActive);
        try
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Response.SendChunked = true;
            var stream = ctx.Response.OutputStream;

            // Browser EventSource fires onerror if the proxy/load-balancer closes the
            // connection before the first body byte. With SendChunked the response headers
            // do not actually go on the wire until the first Write, so a quiet server
            // (no /chat or /execute traffic) leaves the client hanging until the 15s
            // heartbeat — which is past most browser/proxy connect-idle limits. Emit an
            // SSE comment line and a backlog snapshot before entering the live loop so
            // the client transitions to onopen instantly.
            int historyCount = 0;
            if (int.TryParse(ctx.Request.QueryString["history"], out int hc))
                historyCount = Math.Clamp(hc, 0, _cfg.BufferSize);

            try
            {
                var hello = Encoding.UTF8.GetBytes(": connected\n\n");
                stream.Write(hello, 0, hello.Length);
                stream.Flush();
                if (historyCount > 0)
                {
                    foreach (var e in _buffer.Tail(historyCount))
                    {
                        var payload = $"data: {{\"seq\":{e.SeqNo},\"stream\":\"{e.Stream}\",\"line\":{JsonEncodedString(e.Line)}}}\n\n";
                        var bytes = Encoding.UTF8.GetBytes(payload);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    stream.Flush();
                }
            }
            catch { /* client gone before first byte; loop will exit on next heartbeat */ }

            // Async loop with cancellable WaitToReadAsync prevents abandoned waiters from accumulating.
            StreamLoopAsync(ch, stream).GetAwaiter().GetResult();
        }
        finally
        {
            lock (_streamLock) _streamChannels.Remove(ch);
            ch.Writer.TryComplete();
            try { ctx.Response.OutputStream.Close(); } catch { }
            Interlocked.Decrement(ref _streamClientsActive);
        }
    }

    async Task StreamLoopAsync(Channel<MessageBuffer.Entry> ch, Stream stream)
    {
        while (!_cts.IsCancellationRequested)
        {
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            heartbeatCts.CancelAfter(StreamHeartbeatInterval);
            bool wrote = false;
            try
            {
                bool more = await ch.Reader.WaitToReadAsync(heartbeatCts.Token).ConfigureAwait(false);
                if (!more) break;
                while (ch.Reader.TryRead(out var e))
                {
                    var payload = $"data: {{\"seq\":{e.SeqNo},\"stream\":\"{e.Stream}\",\"line\":{JsonEncodedString(e.Line)}}}\n\n";
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                    wrote = true;
                }
                if (wrote) await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
            {
                // heartbeat tick — emit a comment line so dead clients are detected by write failure
                try
                {
                    var hb = Encoding.UTF8.GetBytes(": heartbeat\n\n");
                    await stream.WriteAsync(hb).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch { break; }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { break; }
        }
    }

    void Status(HttpListenerContext ctx)
    {
        long executeTotal = Interlocked.Read(ref _executeTotal);
        long streamClientsActive = Interlocked.Read(ref _streamClientsActive);
        int uptimeSec = (int)(DateTime.UtcNow - _startedAt).TotalSeconds;
        long childMem = 0;
        double childCpuSec = 0;
        if (_server.IsAlive && _server.ChildPid > 0)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(_server.ChildPid);
                childMem = p.WorkingSet64;
                childCpuSec = p.TotalProcessorTime.TotalSeconds;
            }
            catch { /* child may have exited mid-read */ }
        }
        var info = _a2s.LatestInfo();
        var sb = new StringBuilder("{");
        sb.Append("\"child_alive\":").Append(_server.IsAlive ? "true" : "false");
        sb.Append(",\"child_pid\":").Append(_server.ChildPid);
        sb.Append(",\"child_uptime_sec\":").Append((int)_server.Uptime.TotalSeconds);
        sb.Append(",\"child_memory_bytes\":").Append(childMem);
        sb.Append(",\"child_cpu_seconds\":").Append(childCpuSec.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(",\"sidecar_uptime_sec\":").Append(uptimeSec);
        sb.Append(",\"buffer_capacity\":").Append(_cfg.BufferSize);
        sb.Append(",\"listen_port\":").Append(_cfg.ListenPort);
        sb.Append(",\"child_port\":").Append(_cfg.ChildPort);
        sb.Append(",\"query_port\":").Append(_cfg.QueryPort);
        sb.Append(",\"execute_total\":").Append(executeTotal);
        sb.Append(",\"stream_clients_active\":").Append(streamClientsActive);
        sb.Append(",\"server\":");
        if (info is null) sb.Append("null");
        else
        {
            sb.Append('{');
            sb.Append("\"name\":").Append(JsonEncodedString(info.Name));
            sb.Append(",\"map\":").Append(JsonEncodedString(info.Map));
            sb.Append(",\"folder\":").Append(JsonEncodedString(info.Folder));
            sb.Append(",\"game\":").Append(JsonEncodedString(info.Game));
            sb.Append(",\"players\":").Append(info.Players);
            sb.Append(",\"max_players\":").Append(info.MaxPlayers);
            sb.Append(",\"bots\":").Append(info.Bots);
            sb.Append(",\"fetched_at\":\"").Append(info.FetchedAt.ToString("o")).Append('"');
            sb.Append('}');
        }
        sb.Append('}');
        Write(ctx, 200, "application/json", sb.ToString());
    }

    void Dashboard(HttpListenerContext ctx)
    {
        if (_dashboardHtml is null)
        {
            Write(ctx, 404, "application/json", "{\"error\":\"dashboard disabled\"}");
            return;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = _dashboardHtml.Length;
        ctx.Response.OutputStream.Write(_dashboardHtml, 0, _dashboardHtml.Length);
        ctx.Response.OutputStream.Close();
    }

    static byte[]? LoadDashboardBytes()
    {
        var asm = typeof(HttpApi).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    void Players(HttpListenerContext ctx)
    {
        var roster = _banlist.Online();
        var a2sPlayers = _a2s.LatestPlayers();
        var sb = new StringBuilder("{\"players\":[");
        for (int i = 0; i < roster.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var p = roster[i];
            sb.Append("{\"steamid\":").Append(JsonEncodedString(p.SteamId))
              .Append(",\"name\":").Append(JsonEncodedString(p.Name))
              .Append(",\"seen_at\":\"").Append(p.SeenAt.ToString("o")).Append("\"}");
        }
        sb.Append("],\"a2s_players\":[");
        for (int i = 0; i < a2sPlayers.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var p = a2sPlayers[i];
            sb.Append("{\"name\":").Append(JsonEncodedString(p.Name))
              .Append(",\"score\":").Append(p.Score)
              .Append(",\"duration_sec\":").Append(p.Duration.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
              .Append('}');
        }
        sb.Append("]}");
        Write(ctx, 200, "application/json", sb.ToString());
    }

    void Bans(HttpListenerContext ctx)
    {
        switch (ctx.Request.HttpMethod)
        {
            case "GET":
                {
                    var list = _banlist.All();
                    var sb = new StringBuilder("{\"bans\":[");
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var b = list[i];
                        sb.Append("{\"steamid\":").Append(JsonEncodedString(b.SteamId))
                          .Append(",\"reason\":").Append(JsonEncodedString(b.Reason))
                          .Append(",\"added_at\":").Append(JsonEncodedString(b.AddedAt))
                          .Append(",\"added_by\":").Append(JsonEncodedString(b.AddedBy))
                          .Append('}');
                    }
                    sb.Append("]}");
                    Write(ctx, 200, "application/json", sb.ToString());
                    return;
                }
            case "POST":
                {
                    var body = ReadBody(ctx);
                    if (body is null) return;
                    string? sid; string? reason;
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        sid = doc.RootElement.TryGetProperty("steamid", out var s) ? s.GetString() : null;
                        reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : "";
                    }
                    catch (JsonException) { Write(ctx, 400, "application/json", "{\"error\":\"invalid json\"}"); return; }
                    if (string.IsNullOrWhiteSpace(sid)) { Write(ctx, 400, "application/json", "{\"error\":\"steamid required\"}"); return; }
                    var by = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "";
                    _banlist.Add(sid!, reason ?? "", by);
                    Write(ctx, 200, "application/json", "{\"ok\":true}");
                    return;
                }
            default:
                Write(ctx, 405, "application/json", "{\"error\":\"GET or POST\"}");
                return;
        }
    }

    void BanOne(HttpListenerContext ctx, string steamid)
    {
        steamid = WebUtility.UrlDecode(steamid);
        if (ctx.Request.HttpMethod != "DELETE") { Write(ctx, 405, "application/json", "{\"error\":\"DELETE\"}"); return; }
        if (!_banlist.Remove(steamid)) { Write(ctx, 404, "application/json", "{\"error\":\"not found\"}"); return; }
        Write(ctx, 200, "application/json", "{\"ok\":true}");
    }

    void SchedulerList(HttpListenerContext ctx)
    {
        switch (ctx.Request.HttpMethod)
        {
            case "GET":
                {
                    var list = _scheduler.All();
                    var sb = new StringBuilder("{\"jobs\":[");
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var j = list[i];
                        var nx = _scheduler.NextFireFor(j.Id);
                        string nxStr = nx == DateTime.MaxValue ? "" : nx.ToString("o");
                        sb.Append("{\"id\":").Append(JsonEncodedString(j.Id))
                          .Append(",\"schedule\":").Append(JsonEncodedString(j.Schedule))
                          .Append(",\"command\":").Append(JsonEncodedString(j.Command))
                          .Append(",\"enabled\":").Append(j.Enabled ? "true" : "false")
                          .Append(",\"created_at\":").Append(JsonEncodedString(j.CreatedAt))
                          .Append(",\"last_run_at\":").Append(j.LastRunAt is null ? "null" : JsonEncodedString(j.LastRunAt))
                          .Append(",\"next_fire_at\":").Append(JsonEncodedString(nxStr))
                          .Append(",\"run_count\":").Append(j.RunCount)
                          .Append('}');
                    }
                    sb.Append("]}");
                    Write(ctx, 200, "application/json", sb.ToString());
                    return;
                }
            case "POST":
                {
                    var body = ReadBody(ctx);
                    if (body is null) return;
                    string? id; string? schedule; string? command;
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
                        schedule = doc.RootElement.TryGetProperty("schedule", out var s) ? s.GetString() : null;
                        command = doc.RootElement.TryGetProperty("command", out var c) ? c.GetString() : null;
                    }
                    catch (JsonException) { Write(ctx, 400, "application/json", "{\"error\":\"invalid json\"}"); return; }
                    if (!_scheduler.TryAdd(id ?? "", schedule ?? "", command ?? "", out string err))
                    {
                        Write(ctx, 400, "application/json", $"{{\"error\":{JsonEncodedString(err)}}}");
                        return;
                    }
                    Write(ctx, 200, "application/json", "{\"ok\":true}");
                    return;
                }
            default:
                Write(ctx, 405, "application/json", "{\"error\":\"GET or POST\"}");
                return;
        }
    }

    void ServerStart(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, "application/json", "{\"error\":\"POST required\"}"); return; }
        bool started = _server.TryStartIfStopped();
        _audit.Record("server_start", new Dictionary<string, object?>
        {
            ["client_ip"] = ctx.Request.RemoteEndPoint?.Address.ToString(),
            ["already_running"] = !started,
        });
        if (!started) { Write(ctx, 409, "application/json", "{\"error\":\"already running\"}"); return; }
        Write(ctx, 200, "application/json", "{\"ok\":true}");
    }

    void ServerStop(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, "application/json", "{\"error\":\"POST required\"}"); return; }
        if (!_server.IsAlive) { Write(ctx, 409, "application/json", "{\"error\":\"already stopped\"}"); return; }
        _audit.Record("server_stop", new Dictionary<string, object?>
        {
            ["client_ip"] = ctx.Request.RemoteEndPoint?.Address.ToString(),
        });
        _server.Stop(TimeSpan.FromSeconds(15));
        Write(ctx, 200, "application/json", "{\"ok\":true}");
    }

    void ServerRestart(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST") { Write(ctx, 405, "application/json", "{\"error\":\"POST required\"}"); return; }
        _audit.Record("server_restart", new Dictionary<string, object?>
        {
            ["client_ip"] = ctx.Request.RemoteEndPoint?.Address.ToString(),
        });
        _server.Restart(TimeSpan.FromSeconds(15));
        Write(ctx, 200, "application/json", "{\"ok\":true}");
    }

    void LogsList(HttpListenerContext ctx)
    {
        if (!_logs.Enabled) { Write(ctx, 404, "application/json", "{\"error\":\"logs-dir not configured\"}"); return; }
        var list = _logs.List();
        var sb = new StringBuilder("{\"root\":");
        sb.Append(JsonEncodedString(_logs.Root ?? ""));
        sb.Append(",\"files\":[");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var f = list[i];
            sb.Append("{\"name\":").Append(JsonEncodedString(f.Name))
              .Append(",\"size\":").Append(f.SizeBytes)
              .Append(",\"modified_at\":\"").Append(f.ModifiedUtc.ToString("o")).Append("\"}");
        }
        sb.Append("]}");
        Write(ctx, 200, "application/json", sb.ToString());
    }

    void LogsTail(HttpListenerContext ctx, string nameSegment)
    {
        if (!_logs.Enabled) { Write(ctx, 404, "application/json", "{\"error\":\"logs-dir not configured\"}"); return; }
        string name = WebUtility.UrlDecode(nameSegment);
        if (!_logs.TryResolve(name, out string fullPath))
        {
            Write(ctx, 404, "application/json", "{\"error\":\"not found\"}");
            return;
        }
        int tail = 500;
        if (int.TryParse(ctx.Request.QueryString["tail"], out int t)) tail = Math.Clamp(t, 1, 10000);
        string body = _logs.TailToString(fullPath, tail);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    void SchedulerItem(HttpListenerContext ctx, string rest)
    {
        // /scheduler/<id>          DELETE
        // /scheduler/<id>/enable   POST
        // /scheduler/<id>/disable  POST
        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { Write(ctx, 404, "application/json", "{\"error\":\"id required\"}"); return; }
        string id = WebUtility.UrlDecode(parts[0]);
        if (parts.Length == 1)
        {
            if (ctx.Request.HttpMethod != "DELETE") { Write(ctx, 405, "application/json", "{\"error\":\"DELETE\"}"); return; }
            if (!_scheduler.Remove(id)) { Write(ctx, 404, "application/json", "{\"error\":\"not found\"}"); return; }
            Write(ctx, 200, "application/json", "{\"ok\":true}");
            return;
        }
        string action = parts[1];
        bool? on = action switch { "enable" => true, "disable" => false, _ => null };
        if (on is null) { Write(ctx, 404, "application/json", "{\"error\":\"unknown action\"}"); return; }
        if (!_scheduler.SetEnabled(id, on.Value)) { Write(ctx, 404, "application/json", "{\"error\":\"not found\"}"); return; }
        Write(ctx, 200, "application/json", "{\"ok\":true}");
    }

    string? ReadBody(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBodyBytes)
        {
            Write(ctx, 413, "application/json", "{\"error\":\"body too large\"}");
            return null;
        }
        var raw = new byte[MaxBodyBytes + 1];
        int total = 0;
        var input = ctx.Request.InputStream;
        while (total <= MaxBodyBytes)
        {
            int n = input.Read(raw, total, raw.Length - total);
            if (n <= 0) break;
            total += n;
        }
        if (total > MaxBodyBytes)
        {
            Write(ctx, 413, "application/json", "{\"error\":\"body too large\"}");
            return null;
        }
        return Encoding.UTF8.GetString(raw, 0, total);
    }

    static string JsonEncodedString(string s) => JsonSerializer.Serialize(s);

    static void Write(HttpListenerContext ctx, int status, string contentType, string body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
