using System.Text.Json;

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
    public required int BufferSize { get; init; }
    public required string ShutdownCommand { get; init; }
    public required string AuditLogPath { get; init; }
    public required string DiscordWebhookUrl { get; init; }
    public required string BanlistPath { get; init; }
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
            if (key == "dashboard-disabled" || key == "no-auto-restart")
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

        string Get(string key, string fallback) => raw.TryGetValue(key, out var v) ? v : fallback;
        int GetInt(string key, int fallback) => raw.TryGetValue(key, out var v) && int.TryParse(v, out int n) ? n : fallback;
        bool GetBool(string key, bool fallback) => raw.TryGetValue(key, out var v) && bool.TryParse(v, out bool b) ? b : fallback;

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
        int bufferSize = Math.Clamp(GetInt("buffer-size", 500), 10, 10000);
        string shutdownCommand = Get("shutdown-command", "quit");
        string auditLogPath = Get("audit-log", "");
        string discordWebhookUrl = Get("discord-webhook", "");
        string banlistPath = Get("banlist", "");
        string schedulerPath = Get("scheduler", "");
        string kickCommandTemplate = Get("kick-command", "kick {steamid}");
        // sbox-server.exe stdout shapes (verified live on a 207139 demo run):
        //   "01:20:36 Generic  Joe [76561198966650247] is connecting"   - connect
        //   "01:21:25 Generic  Joe [76561198966650247] is connected"    - connect (post-handshake)
        //   "SteamIdSocket - steamid:N: Disconnection (N)"              - disconnect
        // sbox prefixes stdout with "HH:MM:SS Category  " so the connect regex must NOT
        // be ^-anchored. \b on the name capture keeps spurious matches out of garbled lines.
        // Disconnect alternates: SteamID64 in parens (preferred) OR colon-prefix network id.
        // Override with --connect-regex/--disconnect-regex if a future sbox build drifts.
        string connectLineRegex = Get("connect-regex",
            @"\b(?<name>\S+)\s+\[(?<steamid>\d{17})\]\s+is\s+connecting\b");
        string disconnectLineRegex = Get("disconnect-regex",
            @"SteamIdSocket\s*-\s*steamid:\d+:\s*Disconnection\s*\((?<steamid>\d{17})\)|SteamIdSocket\s*-\s*steamid:(?<steamid>\d{17}):\s*Disconnection");
        // Frame-stats + running-status header are pure noise (emitted every tick).
        // sbox concatenates them into one logical line through the ConPTY stream:
        //   "<hostname> (n/max) [h:mm:ss]   <padding>   Physics F.FFms, ... Network F.FFms"
        // The first regex branch catches that combined shape via the unique
        // "(n/m) [h:mm:ss]" marker, the second catches a stand-alone frame-stats line.
        // Engine errors ("11:54:58 engine/R Error ...") and connect lines (no brackets) are unaffected.
        // Override with --suppress-regex (pass empty string to disable suppression).
        string suppressLineRegex = Get("suppress-regex",
            @"\(\d+/\d+\)\s+\[\d+:\d{2}:\d{2}\]|^\w+\s+[\d.]+ms,");
        bool dashboardEnabled = !GetBool("dashboard-disabled", false);
        bool autoRestart = !GetBool("no-auto-restart", false);
        int restartBackoffSeconds = Math.Clamp(GetInt("restart-backoff-sec", 5), 1, 600);
        int restartMaxAttempts = Math.Clamp(GetInt("restart-max-attempts", 5), 1, 100);
        int restartWindowSeconds = Math.Clamp(GetInt("restart-window-sec", 600), 60, 86400);
        string logsDir = Get("logs-dir", "");

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
        queryPort ??= TryExtractNetQueryPort(childArgs) ?? childPort.Value;

        return new CliConfig
        {
            ChildExe = exe,
            GameDir = gameDir,
            ChildArgs = childArgs,
            ChildPort = childPort.Value,
            ListenPort = listenPort.Value,
            RconPort = rconPort.Value,
            BindAddress = bindAddress,
            RconPassword = rconPassword,
            BufferSize = bufferSize,
            ShutdownCommand = shutdownCommand,
            AuditLogPath = auditLogPath,
            DiscordWebhookUrl = discordWebhookUrl,
            BanlistPath = banlistPath,
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
        };
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
              --query-port <int>        sbox A2S UDP port; inferred from +net_query_port
              --query-poll-sec <n>      A2S poll interval; 0=disabled (default 30)
              --bind <addr>             listen address (default 127.0.0.1; use 0.0.0.0 for LAN)
              --rcon-password <str>     required for /execute, /stream, /history (any non-empty)
              --buffer-size <int>       log ring buffer (10..10000, default 500)
              --shutdown-command <str>  console command sent on graceful stop (default "quit")
              --audit-log <path>        JSONL audit; rotated at 10MB, 10 generations
              --discord-webhook <url>   Discord-compatible webhook for lifecycle notifications
              --banlist <path>          JSON banlist; auto-kicks banned steamids on connect
              --kick-command <tpl>      template for kick command, {steamid} placeholder (default "kick {steamid}")
              --connect-regex <re>      regex with named (?<steamid>...) capture; banlist enforced when matches
                                        default matches sbox "<name> [STEAMID64] is connecting"
              --disconnect-regex <re>   regex with named (?<steamid>...) capture
                                        default matches sbox "SteamIdSocket - steamid:N: Disconnection"
              --suppress-regex <re>     drop matching stdout lines BEFORE buffer/stream/dashboard
                                        default suppresses sbox per-frame stats and status header
                                        (pass empty string to disable suppression)
              --scheduler <path>        JSON scheduled-commands store
              --logs-dir <path>         directory exposed via /logs (read-only)
              --no-auto-restart         do not auto-restart child on unexpected exit
              --restart-backoff-sec <n> seconds to wait before restart (default 5)
              --restart-max-attempts <n>  cap restarts inside window (default 5)
              --restart-window-sec <n>  window for max-attempts cap (default 600)
              --dashboard-disabled      do not serve the / dashboard page
              --help                    print this help
            """);
    }
}
