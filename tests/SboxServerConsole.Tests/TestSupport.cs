using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

// Several tests bind real TCP ports and spawn real child processes. Running them
// one at a time keeps port selection and process lifetime deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SboxServerConsole.Tests;

// Throwaway directory for banlist/allowlist/scheduler/audit files.
sealed class Scratch : IDisposable
{
    public string Dir { get; }

    public Scratch()
    {
        Dir = Path.Combine(Path.GetTempPath(), "ssc-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Dir);
    }

    public string File(string name) => Path.Combine(Dir, name);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}

static class Ports
{
    // Bind :0, read what the OS handed out, release it. Good enough for a
    // serialized test run; nothing else on the box is racing for the port.
    public static int Free()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

static class Configs
{
    // Any existing file satisfies CliConfig's --exe check. Tests that never start
    // the child use the test host itself so the path is valid on every platform.
    public static string InertExe => Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;

    // A child that reads commands from stdin and writes results to stdout, which is
    // exactly the contract ServerProcess expects of sbox-server. "exit" ends it.
    public const string ShellChild = "/bin/sh";

    public static bool CanSpawnShellChild => (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        && System.IO.File.Exists(ShellChild);

    public static CliConfig Parse(string gameDir, params string[] extra)
    {
        var args = new List<string>
        {
            "--exe", InertExe,
            "--game-dir", gameDir,
            "--child-args", "+port 27015",
            "--query-poll-sec", "0",
        };
        args.AddRange(extra);
        var cfg = CliConfig.Parse(args.ToArray());
        Assert.NotNull(cfg);
        return cfg!;
    }
}

// Wires up the same object graph Program.cs builds and serves the HTTP API and
// the RCON listener on loopback ports. The child process is opt-in (StartChild).
sealed class ApiHost : IDisposable
{
    public const string Password = "test-password";

    public Scratch Scratch { get; }
    public CliConfig Config { get; }
    public MessageBuffer Buffer { get; }
    public ServerProcess Server { get; }
    public AuditLog Audit { get; }
    public Banlist Banlist { get; }
    public Allowlist Allowlist { get; }
    public Scheduler Scheduler { get; }
    public A2SQuery A2s { get; }
    public LogsBrowser Logs { get; }
    public RconServer Rcon { get; }
    public HttpApi Api { get; }
    public string BaseUrl { get; }

    readonly DiscordWebhook _discord;
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    ApiHost(string[] extra)
    {
        Scratch = new Scratch();
        var args = new List<string>
        {
            "--listen-port", Ports.Free().ToString(),
            "--rcon-port", Ports.Free().ToString(),
            "--rcon-password", Password,
            "--audit-log", Scratch.File("audit.jsonl"),
            "--banlist", Scratch.File("bans.json"),
            "--allowlist", Scratch.File("allow.json"),
            "--scheduler", Scratch.File("scheduler.json"),
        };
        args.AddRange(extra);
        Config = Configs.Parse(Scratch.Dir, args.ToArray());

        Buffer = new MessageBuffer(Config.BufferSize);
        Server = new ServerProcess(Config, Buffer);
        Audit = new AuditLog(Config.AuditLogPath);
        _discord = new DiscordWebhook(Config.DiscordWebhookUrl);
        Banlist = new Banlist(Config, Server, Buffer, Audit, _discord);
        Allowlist = new Allowlist(Config, Server, Buffer, Audit);
        Scheduler = new Scheduler(Config, Server, Buffer, Audit);
        A2s = new A2SQuery(Config, Buffer);
        Logs = new LogsBrowser(Config.LogsDir);
        Rcon = new RconServer(Config, Server, Buffer, Audit);
        Api = new HttpApi(Config, Server, Buffer, Audit, Banlist, Allowlist, Scheduler, A2s, Logs);
        BaseUrl = $"http://127.0.0.1:{Config.ListenPort}";
    }

    public static ApiHost Create(params string[] extra)
    {
        for (int attempt = 0; ; attempt++)
        {
            var host = new ApiHost(extra);
            try
            {
                host.Api.Start();
                host.Rcon.Start();
                return host;
            }
            catch (Exception) when (attempt < 4)
            {
                host.Dispose();
            }
        }
    }

    // Starts the shell child and waits for it to come up.
    public void StartChild()
    {
        Server.Start();
        Assert.True(Wait.For(() => Server.IsAlive), "child process did not come up");
    }

    public async Task<(int Status, string Body)> Call(
        string method,
        string path,
        string? json = null,
        string? password = Password,
        bool bearer = false)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), BaseUrl + path);
        if (password is not null)
        {
            if (bearer) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + password);
            else req.Headers.TryAddWithoutValidation("X-RCON-Password", password);
        }
        if (json is not null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        else if (method is "POST" or "PUT")
            req.Content = new ByteArrayContent(Array.Empty<byte>()); // Content-Length: 0
        using var resp = await Client.SendAsync(req).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ((int)resp.StatusCode, body);
    }

    public async Task<JsonElement> Json(string method, string path, string? json = null)
    {
        var (status, body) = await Call(method, path, json).ConfigureAwait(false);
        Assert.True(status == 200, $"{method} {path} -> {status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public IReadOnlyList<JsonElement> AuditEvents()
    {
        var path = Config.AuditLogPath;
        if (!System.IO.File.Exists(path)) return Array.Empty<JsonElement>();
        var list = new List<JsonElement>();
        foreach (var line in System.IO.File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            list.Add(JsonDocument.Parse(line).RootElement.Clone());
        }
        return list;
    }

    public void Dispose()
    {
        try { Api.Dispose(); } catch { }
        try { Rcon.Dispose(); } catch { }
        try { Scheduler.Dispose(); } catch { }
        try { A2s.Dispose(); } catch { }
        try { Allowlist.Dispose(); } catch { }
        try { Banlist.Dispose(); } catch { }
        try { Server.Dispose(); } catch { }
        try { _discord.Dispose(); } catch { }
        try { Audit.Dispose(); } catch { }
        try { Buffer.Dispose(); } catch { }
        Scratch.Dispose();
    }
}

// Minimal Source RCON client: enough of the Valve binary protocol to prove the
// server speaks it the way mcrcon and friends expect.
static class RconWire
{
    public const int ResponseValue = 0;
    public const int AuthResponse = 2;
    public const int ExecCommand = 2;
    public const int Auth = 3;

    public static void Send(NetworkStream net, int id, int type, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        int size = 4 + 4 + bodyBytes.Length + 2;
        var pkt = new byte[4 + size];
        System.Buffer.BlockCopy(BitConverter.GetBytes(size), 0, pkt, 0, 4);
        System.Buffer.BlockCopy(BitConverter.GetBytes(id), 0, pkt, 4, 4);
        System.Buffer.BlockCopy(BitConverter.GetBytes(type), 0, pkt, 8, 4);
        if (bodyBytes.Length > 0) System.Buffer.BlockCopy(bodyBytes, 0, pkt, 12, bodyBytes.Length);
        net.Write(pkt, 0, pkt.Length);
        net.Flush();
    }

    public static bool TryRead(NetworkStream net, out int id, out int type, out string body)
    {
        id = 0; type = 0; body = "";
        var hdr = new byte[4];
        if (!ReadExact(net, hdr, 4)) return false;
        int size = BitConverter.ToInt32(hdr, 0);
        if (size < 10 || size > 16384) return false;
        var buf = new byte[size];
        if (!ReadExact(net, buf, size)) return false;
        id = BitConverter.ToInt32(buf, 0);
        type = BitConverter.ToInt32(buf, 4);
        int end = 8;
        while (end < buf.Length && buf[end] != 0) end++;
        body = Encoding.UTF8.GetString(buf, 8, end - 8);
        return true;
    }

    static bool ReadExact(NetworkStream net, byte[] dst, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n;
            try { n = net.Read(dst, total, count - total); }
            catch (IOException) { return false; }
            if (n <= 0) return false;
            total += n;
        }
        return true;
    }
}

static class Res
{
    // Reads one of the files embedded by the .csproj (source + docs, for the drift tests).
    public static string Read(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"embedded resource missing: {logicalName}");
        using var sr = new StreamReader(s);
        return sr.ReadToEnd();
    }
}

static class Wait
{
    public static bool For(Func<bool> cond, int timeoutMs = 10000, int pollMs = 50)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            Thread.Sleep(pollMs);
        }
        return cond();
    }
}
