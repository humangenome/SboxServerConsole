using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxServerConsole;

// Persistent banlist with per-line regex enforcement.
// Storage is a single JSON file; small enough to load and rewrite atomically.
// When a connect-line matches, we extract the steamid; if banned we run the
// configured kick template via stdin. Disconnect-line matches keep the
// online roster fresh for the dashboard.
public sealed class Banlist : IDisposable
{
    public sealed class Ban
    {
        public string SteamId { get; set; } = "";
        public string Reason { get; set; } = "";
        public string AddedAt { get; set; } = "";
        public string AddedBy { get; set; } = "";
    }

    public sealed record Player(string SteamId, string Name, DateTime SeenAt);

    readonly string? _path;
    readonly ServerProcess _server;
    readonly MessageBuffer _buffer;
    readonly AuditLog _audit;
    readonly DiscordWebhook _discord;
    readonly CliConfig _cfg;
    readonly object _lock = new();
    readonly Dictionary<string, Ban> _bans = new(StringComparer.Ordinal);
    readonly Dictionary<string, Player> _online = new(StringComparer.Ordinal);
    readonly Regex? _connectRe;
    readonly Regex? _disconnectRe;

    public event Action<Player>? OnPlayerJoin;
    public event Action<string>? OnPlayerLeave;

    public Banlist(CliConfig cfg, ServerProcess server, MessageBuffer buffer, AuditLog audit, DiscordWebhook discord)
    {
        _cfg = cfg;
        _server = server;
        _buffer = buffer;
        _audit = audit;
        _discord = discord;
        _path = string.IsNullOrWhiteSpace(cfg.BanlistPath) ? null : cfg.BanlistPath;
        _connectRe = TryCompile(cfg.ConnectLineRegex);
        _disconnectRe = TryCompile(cfg.DisconnectLineRegex);
        Load();
        _buffer.OnAppend += OnLine;
    }

    static Regex? TryCompile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return null; }
    }

    public bool ConnectRegexConfigured => _connectRe is not null;
    public bool DisconnectRegexConfigured => _disconnectRe is not null;
    public bool Persisted => _path is not null;

    public IReadOnlyList<Ban> All()
    {
        lock (_lock) return _bans.Values.OrderBy(b => b.AddedAt).ToList();
    }

    public IReadOnlyList<Player> Online()
    {
        lock (_lock) return _online.Values.OrderBy(p => p.Name).ToList();
    }

    public bool Add(string steamId, string reason, string by)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return false;
        lock (_lock)
        {
            _bans[steamId] = new Ban
            {
                SteamId = steamId,
                Reason = reason ?? "",
                AddedAt = DateTime.UtcNow.ToString("o"),
                AddedBy = by ?? "",
            };
            Save();
        }
        _audit.Record("ban_add", new Dictionary<string, object?>
        {
            ["steamid"] = steamId,
            ["reason"] = reason,
            ["added_by"] = by,
        });
        // Kick now if currently online.
        TryKick(steamId);
        return true;
    }

    public bool Remove(string steamId)
    {
        bool removed;
        lock (_lock)
        {
            removed = _bans.Remove(steamId);
            if (removed) Save();
        }
        if (removed)
        {
            _audit.Record("ban_remove", new Dictionary<string, object?> { ["steamid"] = steamId });
        }
        return removed;
    }

    public bool IsBanned(string steamId)
    {
        lock (_lock) return _bans.ContainsKey(steamId);
    }

    void OnLine(MessageBuffer.Entry e)
    {
        if (e.Stream != "stdout") return;
        if (_connectRe is not null)
        {
            var m = _connectRe.Match(e.Line);
            if (m.Success)
            {
                string sid = m.Groups["steamid"].Value;
                string name = m.Groups["name"].Success ? m.Groups["name"].Value : "";
                if (!string.IsNullOrEmpty(sid))
                {
                    var p = new Player(sid, name, DateTime.UtcNow);
                    lock (_lock) _online[sid] = p;
                    if (IsBanned(sid)) TryKick(sid);
                    else
                    {
                        try { OnPlayerJoin?.Invoke(p); } catch { }
                        _ = _discord.SendAsync("Player joined", $"`{(string.IsNullOrEmpty(name) ? sid : name)}` connected.", DiscordWebhook.ColorBlue);
                    }
                }
            }
        }
        if (_disconnectRe is not null)
        {
            var m = _disconnectRe.Match(e.Line);
            if (m.Success)
            {
                string sid = m.Groups["steamid"].Value;
                if (!string.IsNullOrEmpty(sid))
                {
                    string? name = null;
                    lock (_lock)
                    {
                        if (_online.TryGetValue(sid, out var p)) name = p.Name;
                        _online.Remove(sid);
                    }
                    try { OnPlayerLeave?.Invoke(sid); } catch { }
                    _ = _discord.SendAsync("Player left", $"`{(string.IsNullOrEmpty(name) ? sid : name)}` disconnected.", DiscordWebhook.ColorYellow);
                }
            }
        }
    }

    void TryKick(string steamId)
    {
        var cmd = _cfg.KickCommandTemplate.Replace("{steamid}", steamId);
        bool ok = _server.TrySendCommand(cmd);
        _audit.Record("ban_kick", new Dictionary<string, object?>
        {
            ["steamid"] = steamId,
            ["cmd"] = cmd,
            ["sent"] = ok,
        });
    }

    public void UpdateOnlineFromStatus(IReadOnlyList<Player> snapshot)
    {
        lock (_lock)
        {
            _online.Clear();
            foreach (var p in snapshot) _online[p.SteamId] = p;
        }
        // Enforce banlist against fresh roster.
        foreach (var p in snapshot)
        {
            if (IsBanned(p.SteamId)) TryKick(p.SteamId);
        }
    }

    void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("bans", out var arr)) return;
            foreach (var el in arr.EnumerateArray())
            {
                var b = new Ban
                {
                    SteamId = el.TryGetProperty("steamid", out var s) ? s.GetString() ?? "" : "",
                    Reason = el.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                    AddedAt = el.TryGetProperty("added_at", out var a) ? a.GetString() ?? "" : "",
                    AddedBy = el.TryGetProperty("added_by", out var u) ? u.GetString() ?? "" : "",
                };
                if (!string.IsNullOrEmpty(b.SteamId)) _bans[b.SteamId] = b;
            }
        }
        catch (Exception ex)
        {
            _buffer.Append("system", $"banlist load failed: {ex.Message}");
        }
    }

    void Save()
    {
        if (_path is null) return;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var data = new { bans = _bans.Values.OrderBy(b => b.AddedAt).ToList() };
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, opts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _buffer.Append("system", $"banlist save failed: {ex.Message}");
        }
    }

    public void Dispose() => _buffer.OnAppend -= OnLine;
}
