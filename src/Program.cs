using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using SboxServerConsole;

// FIRST, before anything touches Console or signals: a background job started
// by a non-interactive shell inherits SIGINT as SIG_IGN, and the runtime honors
// that by never hooking an originally-ignored signal — neither CancelKeyPress
// nor PosixSignalRegistration ever fires, and the agent cannot be stopped with
// SIGINT in exactly the contexts init scripts and wrappers use. Reset the
// disposition to default before the runtime captures it, so the shutdown
// handlers registered below always see the signal.
ResetInheritedSignalDispositions();

string AgentVersion() => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

string[] BannerLines()
{
    return new[]
    {
        "============================================================",
        $"  S&box Server Console v{AgentVersion()}",
        "  Process agent + Source RCON + dashboard for s&box",
        "",
        "  Repo:     https://github.com/HumanGenome/SboxServerConsole",
        "  Hosting:  https://www.survivalservers.com (official hosting)",
        "============================================================",
    };
}

var localConsoleLock = new object();
using var childConsoleMirror = CanMirrorChildOutputToLocalConsole()
    ? new BlockingCollection<string>(2048)
    : null;
Thread? childConsoleMirrorThread = null;

void WriteLocalConsoleLine(string line)
{
    try
    {
        lock (localConsoleLock) Console.WriteLine(line);
    }
    catch { }
}

void MirrorChildOutputLine(string line)
{
    if (childConsoleMirror is null) return;
    try { childConsoleMirror.TryAdd(line); }
    catch (InvalidOperationException) { }
}

if (childConsoleMirror is not null)
{
    childConsoleMirrorThread = new Thread(() => ChildConsoleMirrorLoop(childConsoleMirror, WriteLocalConsoleLine))
    {
        IsBackground = true,
        Name = "local-console-output",
    };
    childConsoleMirrorThread.Start();
}

// Print to process stdout for anyone watching the wrapper console (panel, RDP, service log).
foreach (var bl in BannerLines()) WriteLocalConsoleLine(bl);

var config = CliConfig.Parse(args);
if (config is null) return 1;

using var buffer = new MessageBuffer(config.BufferSize);

// Mirror the banner into the message buffer so the same content shows up in
// /history, /stream, the dashboard, and panel-proxied console views.
foreach (var bl in BannerLines()) buffer.Append("agent", bl);
buffer.Append("agent", "Starting up — please wait while sbox-server.exe launches and loads the map.");
using var server = new ServerProcess(config, buffer, MirrorChildOutputLine);
using var audit = new AuditLog(config.AuditLogPath);
using var discord = new DiscordWebhook(config.DiscordWebhookUrl);
using var banlist = new Banlist(config, server, buffer, audit, discord);
using var allowlist = new Allowlist(config, server, buffer, audit);
using var scheduler = new Scheduler(config, server, buffer, audit);
using var a2s = new A2SQuery(config, buffer);
var logs = new LogsBrowser(config.LogsDir);
using var rcon = new RconServer(config, server, buffer, audit);

var done = new ManualResetEventSlim(false);

server.OnSupervisorExit += () =>
{
    Console.WriteLine("[SboxServerConsole] supervisor exit; shutting down");
    audit.Record("supervisor_exit", new Dictionary<string, object?> { ["pid"] = server.ChildPid, ["last_exit_code"] = server.LastExitCode });
    _ = discord.SendAsync(
        "S&box server stopped",
        $"Child process exited (last code={server.LastExitCode}). S&box Server Console shutting down.",
        DiscordWebhook.ColorYellow);
    done.Set();
};

try
{
    server.Start();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[SboxServerConsole] failed to start child: {ex.Message}");
    audit.Record("child_start_failed", new Dictionary<string, object?> { ["error"] = ex.Message });
    return 2;
}

HttpApi http;
try
{
    http = new HttpApi(config, server, buffer, audit, banlist, allowlist, scheduler, a2s, logs);
    http.Start();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[SboxServerConsole] failed to start HTTP listener on {config.BindAddress}:{config.ListenPort}: {ex.Message}");
    audit.Record("http_start_failed", new Dictionary<string, object?> { ["error"] = ex.Message });
    server.Stop(TimeSpan.FromSeconds(5));
    return 3;
}
using var _http = http;

scheduler.Start();
a2s.Start();
try { rcon.Start(); }
catch (Exception ex)
{
    Console.Error.WriteLine($"[SboxServerConsole] failed to start RCON listener on {config.BindAddress}:{config.RconPort}: {ex.Message}");
    audit.Record("rcon_start_failed", new Dictionary<string, object?> { ["error"] = ex.Message });
}

// Status lines also go to both stdout AND the buffer so they appear in panel
// console views (which read /history + /stream).
void Status(string s) { WriteLocalConsoleLine($"[SboxServerConsole] {s}"); buffer.Append("agent", s); }

Status($"listening on http://{config.BindAddress}:{config.ListenPort}");
Status($"child pid={server.ChildPid} exe={config.ChildExe}");
if (config.DashboardEnabled) Status($"dashboard:    http://{config.BindAddress}:{config.ListenPort}/");
if (audit.Enabled) Status($"audit log: {audit.Path}");
if (discord.Enabled) Status("Discord webhook configured");
if (banlist.Persisted) Status($"banlist persisted to {config.BanlistPath} (connect-regex: {(banlist.ConnectRegexConfigured ? "set" : "unset")})");
if (allowlist.Persisted) Status($"allowlist persisted to {config.AllowlistPath} (entries: {allowlist.Count}, enforced: {(allowlist.Enforced ? "yes" : "no")})");
if (scheduler.Persisted) Status($"scheduler persisted to {config.SchedulerPath}");
if (a2s.Enabled) Status($"A2S poller every {config.QueryPollSeconds}s on udp/{config.QueryPort}");
if (logs.Enabled) Status($"logs browser exposing {logs.Root}");
if (rcon.Enabled) Status($"Source RCON: tcp/{config.RconPort}");

Thread? localConsoleInput = null;
if (CanUseLocalConsoleInput())
{
    Status("local console input enabled; type server commands here and press Enter");
    localConsoleInput = new Thread(() => LocalConsoleInputLoop(server, audit, done, WriteLocalConsoleLine))
    {
        IsBackground = true,
        Name = "local-console-input",
    };
    localConsoleInput.Start();
}

audit.Record("startup", new Dictionary<string, object?>
{
    ["listen"] = $"{config.BindAddress}:{config.ListenPort}",
    ["child_pid"] = server.ChildPid,
    ["child_port"] = config.ChildPort,
});
_ = discord.SendAsync(
    "S&box server started",
    $"Listening on `{config.BindAddress}:{config.ListenPort}`. Child pid `{server.ChildPid}` on port `{config.ChildPort}`.",
    DiscordWebhook.ColorGreen);

// Shutdown signals. CancelKeyPress alone is not enough: it only fires for an
// interactive Ctrl-C, so a SIGINT delivered by kill(1), an init system, or any
// non-interactive context never reached it and the agent kept serving as if
// nothing happened. PosixSignalRegistration sees the raw signal on every
// platform. Cancel = true suppresses the runtime's default handling — for
// SIGTERM that default is a hard exit on a ~2 s budget, which used to kill the
// process before the child was stopped and left the game server orphaned.
// With it cancelled, the main thread below runs the same graceful stop for
// SIGINT and SIGTERM alike: ask the child to shut down, then kill the process
// tree if it will not, then exit 0.
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; done.Set(); });
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; done.Set(); });
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();

done.Wait();

server.Stop(TimeSpan.FromSeconds(10));
childConsoleMirror?.CompleteAdding();
try { childConsoleMirrorThread?.Join(1000); } catch { }

return 0;

static void ResetInheritedSignalDispositions()
{
    if (OperatingSystem.IsWindows()) return;
    try { _ = UnixSignal(2 /* SIGINT */, IntPtr.Zero /* SIG_DFL */); }
    catch { /* exotic libc without signal(2) — nothing to reset */ }

    [DllImport("libc", EntryPoint = "signal", SetLastError = true)]
    static extern IntPtr UnixSignal(int signum, IntPtr handler);
}

static bool CanMirrorChildOutputToLocalConsole()
{
    try { return !Console.IsOutputRedirected; }
    catch { return false; }
}

static bool CanUseLocalConsoleInput()
{
    try { return !Console.IsInputRedirected; }
    catch { return false; }
}

static void LocalConsoleInputLoop(ServerProcess server, AuditLog audit, ManualResetEventSlim done, Action<string> writeLine)
{
    while (!done.IsSet)
    {
        string? line;
        try { line = Console.ReadLine(); }
        catch (Exception ex)
        {
            if (!done.IsSet) writeLine($"[SboxServerConsole] local console input stopped: {ex.Message}");
            return;
        }

        if (line is null) return;
        if (done.IsSet) return;
        var cmd = line.Trim();
        if (cmd.Length == 0) continue;

        bool sent = server.TrySendCommand(cmd);
        audit.Record("local_console_execute", new Dictionary<string, object?>
        {
            ["cmd"] = cmd,
            ["success"] = sent,
        });
        if (!sent) writeLine("[SboxServerConsole] child is not running; command was not sent");
    }
}

static void ChildConsoleMirrorLoop(BlockingCollection<string> lines, Action<string> writeLine)
{
    try
    {
        foreach (var line in lines.GetConsumingEnumerable()) writeLine(line);
    }
    catch { }
}
