using System.Reflection;
using SboxServerConsole;

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

// Print to process stdout for anyone watching the wrapper console (panel, RDP, service log).
foreach (var bl in BannerLines()) Console.WriteLine(bl);

var config = CliConfig.Parse(args);
if (config is null) return 1;

using var buffer = new MessageBuffer(config.BufferSize);

// Mirror the banner into the message buffer so the same content shows up in
// /history, /stream, the dashboard, and panel-proxied console views.
foreach (var bl in BannerLines()) buffer.Append("agent", bl);
buffer.Append("agent", "Starting up — please wait while sbox-server.exe launches and loads the map.");
using var server = new ServerProcess(config, buffer);
using var audit = new AuditLog(config.AuditLogPath);
using var discord = new DiscordWebhook(config.DiscordWebhookUrl);
using var banlist = new Banlist(config, server, buffer, audit, discord);
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
    http = new HttpApi(config, server, buffer, audit, banlist, scheduler, a2s, logs);
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
void Status(string s) { Console.WriteLine($"[SboxServerConsole] {s}"); buffer.Append("agent", s); }

Status($"listening on http://{config.BindAddress}:{config.ListenPort}");
Status($"child pid={server.ChildPid} exe={config.ChildExe}");
if (config.DashboardEnabled) Status($"dashboard:    http://{config.BindAddress}:{config.ListenPort}/");
if (audit.Enabled) Status($"audit log: {audit.Path}");
if (discord.Enabled) Status("Discord webhook configured");
if (banlist.Persisted) Status($"banlist persisted to {config.BanlistPath} (connect-regex: {(banlist.ConnectRegexConfigured ? "set" : "unset")})");
if (scheduler.Persisted) Status($"scheduler persisted to {config.SchedulerPath}");
if (a2s.Enabled) Status($"A2S poller every {config.QueryPollSeconds}s on udp/{config.QueryPort}");
if (logs.Enabled) Status($"logs browser exposing {logs.Root}");
if (rcon.Enabled) Status($"Source RCON: tcp/{config.RconPort}");

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

Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();

done.Wait();

server.Stop(TimeSpan.FromSeconds(10));

return 0;
