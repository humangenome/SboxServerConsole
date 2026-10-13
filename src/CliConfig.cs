using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxServerConsole;

public sealed class CliConfig
{
    public required string ChildExe { get; init; }
    public required string GameDir { get; init; }
    public required string ChildArgs { get; init; }
    public required int ChildPort { get; init; }
    public required int ListenPort { get; init; }
    public required int RconPort { get; init; }
    public required string BindAddress { get; init; }
    public required string RconPassword { get; init; }
    public required bool RconDisabled { get; init; }
    public required int BufferSize { get; init; }
    public required string ShutdownCommand { get; init; }
    public required string AuditLogPath { get; init; }
    public required string DiscordWebhookUrl { get; init; }
    public required string BanlistPath { get; init; }
    public required string AllowlistPath { get; init; }
    public required string SchedulerPath { get; init; }
    public required string KickCommandTemplate { get; init; }
    public required string ConnectLineRegex { get; init; }
    public required string DisconnectLineRegex { get; init; }
    public required string SuppressLineRegex { get; init; }
    public required bool DashboardEnabled { get; init; }
    public required int QueryPort { get; init; }
    public required int QueryPollSeconds { get; init; }
    public required bool AutoRestart { get; init; }
    public required int RestartBackoffSeconds { get; init; }
    public required int RestartMaxAttempts { get; init; }
    public required int RestartWindowSeconds { get; init; }
    public required string LogsDir { get; init; }
    public required bool WindowsService { get; init; }

    // Problems worth saying out loud at startup: unknown keys (a typo silently
    // ignored is how an RCON password or a banlist path goes missing) and regexes
    // that do not compile.
    public List<string> Warnings { get; } = new();

    // The engine's own default when +net_query_port is not on the command line.
    public const int SboxDefaultQueryPort = 27016;

    public const string DefaultConnectLineRegex =
        @"(?:^\d{1,2}:\d{2}:\d{2}\s+\S+\s+(?<name>.+?)|\b(?<name>\S+))\s+\[(?<steamid>\d{17})\]\s+is\s+connecting\b";
    public const string DefaultDisconnectLineRegex =
        @"\[(?<steamid>\d{17})\]\s+disconnected\b|SteamIdSocket\s*-\s*steamid:\d+:\s*Disconnection\s*\((?<steamid>\d{17})\)|SteamIdSocket\s*-\s*steamid:(?<steamid>\d{17}):\s*Disconnection";

    public static CliConfig? Parse(string[] args)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config-file" && i + 1 < args.Length)
            {
                var path = args[i + 1];
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"--config-file not found: {path}");
                    return null;
                }
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        raw[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString() ?? "",
                            JsonValueKind.Number => prop.Value.GetRawText(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            _ => prop.Value.GetRawText(),
                        };
                    }
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine($"--config-file parse error: {ex.Message}");
                    return null;
                }
            }
        }

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h") { PrintHelp(); return null; }
            if (a == "--config-file") { i++; continue; }
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"unknown arg: {a}");
                PrintHelp();
                return null;
            }
            string key = a[2..];
            if (key == "dashboard-disabled" || key == "no-auto-restart" || key == "rcon-disabled" || key == "windows-service")
            {
                raw[key] = "true";
                continue;
            }
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"missing value for {a}");
                return null;
            }
            raw[key] = args[++i];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "child-port", "listen-port", "rcon-port", "query-port" };
        string Get(string key, string fallback) { seen.Add(key); return raw.TryGetValue(key, out var v) ? v : fallback; }
        int GetInt(string key, int fallback) { seen.Add(key); return raw.TryGetValue(key, out var v) && int.TryParse(v, out int n) ? n : fallback; }
        bool GetBool(string key, bool fallback) { seen.Add(key); return raw.TryGetValue(key, out var v) && bool.TryParse(v, out bool b) ? b : fallback; }

        string exe = Get("exe", "");
        string gameDir = Get("game-dir", "");
        string childArgs = Get("child-args", "");
        int? childPort = raw.TryGetValue("child-port", out var cp) && int.TryParse(cp, out int cpv) ? cpv : null;
        int? listenPort = raw.TryGetValue("listen-port", out var lp) && int.TryParse(lp, out int lpv) ? lpv : null;
        int? rconPort = raw.TryGetValue("rcon-port", out var rp) && int.TryParse(rp, out int rpv) ? rpv : null;
        int? queryPort = raw.TryGetValue("query-port", out var qp) && int.TryParse(qp, out int qpv) ? qpv : null;
        int queryPollSeconds = Math.Clamp(GetInt("query-poll-sec", 30), 0, 3600);
        string bindAddress = Get("bind", "127.0.0.1");
        string rconPassword = Get("rcon-password", "");
        bool rconDisabled = GetBool("rcon-disabled", false);
        int bufferSize = Math.Clamp(GetInt("buffer-size", 500), 10, 10000);
        string shutdownCommand = Get("shutdown-command", "quit");
        string auditLogPath = Get("audit-log", "");
        string discordWebhookUrl = Get("discord-webhook", "");
        string banlistPath = Get("banlist", "");
        string allowlistPath = Get("allowlist", "");
        string schedulerPath = Get("scheduler", "");
        string kickCommandTemplate = Get("kick-command", "kick {steamid}");
        // s&box server output shapes (engine source, NetworkSystem.Handshake.cs and
        // NetworkSystem.Connections.cs, checked against build 26.10.02):
        //   "01:20:36 Generic  Joe Bloggs [76561198966650247] is connecting"  - connect
        //   "01:21:25 Generic  Joe Bloggs [76561198966650247] disconnected"   - disconnect
        //   "SteamIdSocket - steamid:N: Disconnection (N)"                    - disconnect, older builds
        // The connect regex takes the whole name after the "HH:MM:SS Category  "
        // prefix, and falls back to the last word when the prefix is missing. Lines
        // are matched right to left (see PlayerLineParser), so a player name that
        // contains "[<another SteamID>] is connecting" cannot stand in for the real one.
        // Override with --connect-regex/--disconnect-regex if a future build drifts.
        // Empty means "use the default", not "switch join detection off": an empty
        // value copied from an old example config silently disabled bans for everyone.
        string connectLineRegex = Get("connect-regex", "");
        if (string.IsNullOrWhiteSpace(connectLineRegex)) connectLineRegex = DefaultConnectLineRegex;
        string disconnectLineRegex = Get("disconnect-regex", "");
        if (string.IsNullOrWhiteSpace(disconnectLineRegex)) disconnectLineRegex = DefaultDisconnectLineRegex;
        // Frame-stats, running-status header, and startup resource-reload spam are pure noise.
        // sbox concatenates them into one logical line through the ConPTY stream:
        //   "<hostname> (n/max) [h:mm:ss]   <padding>   Physics F.FFms, ... Network F.FFms"
        // The first regex branch catches that combined shape via the unique
        // "(n/m) [h:mm:ss]" marker, the second catches a stand-alone frame-stats line,
        // and the final branches catch high-volume reload/baseline chatter that makes
        // an RDP console unusable for typing commands.
        // Engine errors ("11:54:58 engine/R Error ...") and connect lines (no brackets) are unaffected.
        // Override with --suppress-regex (pass empty string to disable suppression).
        string suppressLineRegex = Get("suppress-regex",
            @"\(\d+/\d+\)\s+\[\d+:\d{2}:\d{2}\]|^\w+\s+[\d.]+ms,|\bReloaded\s+\d+\s+resident\s+symlinked\s+resources\s+in\s+\d+ms\b|\bCleanupSystem:\s+Captured\s+baseline\s+with\s+\d+\s+objects\.");
        bool dashboardEnabled = !GetBool("dashboard-disabled", false);
        bool autoRestart = !GetBool("no-auto-restart", false);
        int restartBackoffSeconds = Math.Clamp(GetInt("restart-backoff-sec", 5), 1, 600);
        int restartMaxAttempts = Math.Clamp(GetInt("restart-max-attempts", 5), 1, 100);
        int restartWindowSeconds = Math.Clamp(GetInt("restart-window-sec", 600), 60, 86400);
        string logsDir = Get("logs-dir", "");
        bool windowsService = GetBool("windows-service", false);

        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(gameDir))
        {
            Console.Error.WriteLine("--exe and --game-dir are required");
            PrintHelp();
            return null;
        }
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"child exe not found: {exe}");
            return null;
        }
        if (!Directory.Exists(gameDir))
        {
            Console.Error.WriteLine($"game-dir not found: {gameDir}");
            return null;
        }

        childPort ??= TryExtractPort(childArgs);
        if (childPort is null)
        {
            Console.Error.WriteLine("could not infer --child-port from --child-args; pass --child-port explicitly");
            return null;
        }
        listenPort ??= childPort.Value + 4;
        rconPort ??= childPort.Value + 5;
        // Without +net_query_port the engine answers A2S on its own default port, not
        // on the game port.
        queryPort ??= TryExtractNetQueryPort(childArgs) ?? SboxDefaultQueryPort;

        var config = new CliConfig
        {
            ChildExe = exe,
            GameDir = gameDir,
            ChildArgs = childArgs,
            ChildPort = childPort.Value,
            ListenPort = listenPort.Value,
            RconPort = rconPort.Value,
            BindAddress = bindAddress,
            RconPassword = rconPassword,
            RconDisabled = rconDisabled,
            BufferSize = bufferSize,
            ShutdownCommand = shutdownCommand,
            AuditLogPath = auditLogPath,
            DiscordWebhookUrl = discordWebhookUrl,
            BanlistPath = banlistPath,
            AllowlistPath = allowlistPath,
            SchedulerPath = schedulerPath,
            KickCommandTemplate = kickCommandTemplate,
            ConnectLineRegex = connectLineRegex,
            DisconnectLineRegex = disconnectLineRegex,
            SuppressLineRegex = suppressLineRegex,
            DashboardEnabled = dashboardEnabled,
            QueryPort = queryPort.Value,
            QueryPollSeconds = queryPollSeconds,
            AutoRestart = autoRestart,
            RestartBackoffSeconds = restartBackoffSeconds,
            RestartMaxAttempts = restartMaxAttempts,
            RestartWindowSeconds = restartWindowSeconds,
            LogsDir = logsDir,
            WindowsService = windowsService,
        };

        foreach (var key in raw.Keys)
        {
            if (!seen.Contains(key))
                config.Warnings.Add($"unknown setting \"{key}\" was ignored (check the spelling; settings are kebab-case, like rcon-password)");
        }
        CheckRegex(config.Warnings, "connect-regex", connectLineRegex, "the player roster, bans and the allowlist will not see joins");
        CheckRegex(config.Warnings, "disconnect-regex", disconnectLineRegex, "the player roster will not see players leave");
        CheckRegex(config.Warnings, "suppress-regex", suppressLineRegex, "no output lines will be suppressed");
        if (string.IsNullOrEmpty(rconPassword))
            config.Warnings.Add("rcon-password is empty: the HTTP API answers 503 on every authenticated route and Source RCON stays off");
        return config;
    }

    static void CheckRegex(List<string> warnings, string key, string pattern, string consequence)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return;
        try { _ = new Regex(pattern, RegexOptions.CultureInvariant); }
        catch (ArgumentException ex) { warnings.Add($"{key} is not a valid regex ({ex.Message}); {consequence}"); }
    }

    static int? TryExtractPort(string args)
        => TryExtractFlagPort(args, "+port");

    static int? TryExtractNetQueryPort(string args)
        => TryExtractFlagPort(args, "+net_query_port");

    static int? TryExtractFlagPort(string args, string flag)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == flag && int.TryParse(parts[i + 1], out int p) && p is > 0 and < 65536)
                return p;
        }
        return null;
    }

    static void PrintHelp()
    {
        Console.Error.WriteLine("""
            SboxServerConsole: process agent for sbox-server.exe
            Required:
              --game-dir <path>         server install/working directory
              --exe <path>              path to sbox-server.exe
              --child-args "<string>"   args passed verbatim to child
            Optional:
              --config-file <path>      JSON file of kebab-case keys; CLI flags override
              --child-port <int>        inferred from +port in --child-args if omitted
              --listen-port <int>       HTTP API port (default child-port + 4)
              --rcon-port <int>         Source RCON TCP port (default child-port + 5)
              --query-port <int>        sbox A2S UDP port; inferred from +net_query_port, else 27016
              --query-poll-sec <n>      A2S poll interval; 0=disabled (default 30)
              --bind <addr>             listen address (default 127.0.0.1; use 0.0.0.0 for LAN)
              --rcon-password <str>     required for /execute, /stream, /history (any non-empty)
              --rcon-disabled           do not bind the Source RCON TCP listener (HTTP API still works)
              --buffer-size <int>       log ring buffer (10..10000, default 500)
              --shutdown-command <str>  console command sent on graceful stop (default "quit")
              --audit-log <path>        JSONL audit; rotated at 10MB, 10 generations
              --discord-webhook <url>   Discord-compatible webhook for lifecycle notifications
              --banlist <path>          JSON banlist; auto-kicks banned steamids on connect
              --allowlist <path>        JSON allowlist; when non-empty, kicks any steamid NOT on the list
                                        on connect. Empty allowlist disables enforcement (open server).
              --kick-command <tpl>      template for kick command, {steamid} placeholder (default "kick {steamid}")
              --connect-regex <re>      regex with named (?<steamid>...) capture; banlist enforced when matches
                                        default matches s&box "<name> [STEAMID64] is connecting"
              --disconnect-regex <re>   regex with named (?<steamid>...) capture
                                        default matches s&box "<name> [STEAMID64] disconnected"
              --suppress-regex <re>     drop matching stdout lines BEFORE buffer/stream/dashboard
                                        default suppresses sbox per-frame stats, status header, and reload spam
                                        (pass empty string to disable suppression)
              --scheduler <path>        JSON scheduled-commands store
              --logs-dir <path>         directory exposed via /logs (read-only)
              --no-auto-restart         do not auto-restart child on unexpected exit
              --restart-backoff-sec <n> seconds to wait before restart (default 5)
              --restart-max-attempts <n>  cap restarts inside window (default 5)
              --restart-window-sec <n>  window for max-attempts cap (default 600)
              --dashboard-disabled      do not serve the / dashboard page
              --windows-service         run under the Windows service manager (set by scripts/install-service.ps1)
              --help                    print this help
            """);
    }
}
