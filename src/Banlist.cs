using System.Text.Json;
using System.Text.Json.Serialization;

namespace SboxServerConsole;

// Persistent banlist with per-line enforcement, plus the online roster.
// Storage is a single JSON file; small enough to load and rewrite atomically.
// When a join line matches, the SteamID is checked; a banned player is kicked
// with the configured kick template. Leave lines keep the roster fresh.
public sealed class Banlist : IDisposable
{
    public sealed class Ban
    {
        [JsonPropertyName("steamid")]   public string SteamId { get; set; } = "";
        [JsonPropertyName("reason")]    public string Reason { get; set; } = "";
        [JsonPropertyName("added_at")]  public string AddedAt { get; set; } = "";
        [JsonPropertyName("added_by")]  public string AddedBy { get; set; } = "";
    }

    public sealed record Player(string SteamId, string Name, DateTime SeenAt);

    // How long A2S may report players with no join line seen before we say the
    // join detection is blind.
    static readonly TimeSpan BlindJoinGrace = TimeSpan.FromMinutes(3);

    readonly SteamIdListFile<Ban> _store;
    readonly ServerProcess _server;
    readonly MessageBuffer _buffer;
    readonly AuditLog _audit;
    readonly DiscordWebhook _discord;
    readonly CliConfig _cfg;
    readonly PlayerLineParser _parser;
    readonly object _lock = new();
    readonly Dictionary<string, Player> _online = new(StringComparer.Ordinal);
    DateTime? _a2sPlayersSince;
    bool _blindWarned;

    // Test seam for the blind-join grace period.
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    // Test seams: raised for an announced join (not a banned one) and an announced leave.
    internal event Action<Player>? OnPlayerJoin;
    internal event Action<string>? OnPlayerLeave;

    public Banlist(CliConfig cfg, ServerProcess server, MessageBuffer buffer, AuditLog audit, DiscordWebhook discord)
    {
        _cfg = cfg;
        _server = server;
        _buffer = buffer;
        _audit = audit;
        _discord = discord;
        _parser = new PlayerLineParser(cfg);
        _store = new SteamIdListFile<Ban>(cfg.BanlistPath, "bans", "banlist", buffer,
            el => new Ban
            {
                SteamId = SteamIdListFile<Ban>.Str(el, "steamid"),
                Reason = SteamIdListFile<Ban>.Str(el, "reason"),
                AddedAt = SteamIdListFile<Ban>.Str(el, "added_at"),
                AddedBy = SteamIdListFile<Ban>.Str(el, "added_by"),
            },
            b => b.SteamId, b => b.AddedAt);
        // An unreadable file is kept: the next save would otherwise overwrite every ban in it.
        _store.Load((path, kept, ex) => $"warn: banlist {path} could not be read ({ex.Message}); starting with no bans" +
            (kept is null ? " and NOT saving changes, so the file is left as it is" : $", the unreadable file was kept as {kept}"));
        _buffer.OnAppend += OnLine;
    }

    public bool ConnectRegexConfigured => _parser.ConnectRegexConfigured;
    public bool DisconnectRegexConfigured => _parser.DisconnectRegexConfigured;
    public bool Persisted => _store.Persisted;

    public IReadOnlyList<Ban> All()
    {
        lock (_lock) return _store.Ordered();
    }

    public IReadOnlyList<Player> Online()
    {
        lock (_lock) return _online.Values.OrderBy(p => p.Name).ToList();
    }

    public bool Add(string steamId, string reason, string by) => TryAdd(steamId, reason, by, out _);

    public bool TryAdd(string steamId, string reason, string by, out string error)
    {
        if (!_parser.IsUsableId(steamId, out error)) return false;
        lock (_lock)
        {
            _store.Items[steamId] = new Ban
            {
                SteamId = steamId,
                Reason = reason ?? "",
                AddedAt = DateTime.UtcNow.ToString("o"),
                AddedBy = by ?? "",
            };
            _store.Save();
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
            removed = _store.Items.Remove(steamId);
            if (removed) _store.Save();
        }
        if (removed)
        {
            _audit.Record("ban_remove", new Dictionary<string, object?> { ["steamid"] = steamId });
        }
        return removed;
    }

    public bool IsBanned(string steamId)
    {
        lock (_lock) return _store.Items.ContainsKey(steamId);
    }

    // The child exited: nobody is connected any more.
    public void ClearRoster()
    {
        lock (_lock)
        {
            _online.Clear();
            _a2sPlayersSince = null;
            _blindWarned = false;
        }
    }

    void OnLine(MessageBuffer.Entry e)
    {
        if (e.Stream != "stdout") return;
        var r = _parser.Classify(e.Line);
        if (r.Kind == PlayerLineParser.Kind.Join)
        {
            var p = new Player(r.SteamId, r.Name, DateTime.UtcNow);
            lock (_lock) _online[r.SteamId] = p;
            if (IsBanned(r.SteamId)) TryKick(r.SteamId);
            else
            {
                try { OnPlayerJoin?.Invoke(p); } catch { }
                _ = _discord.SendAsync("Player joined", $"{DiscordWebhook.Quote(DisplayName(r.Name, r.SteamId))} connected.", DiscordWebhook.ColorBlue);
            }
        }
        else if (r.Kind == PlayerLineParser.Kind.Leave)
        {
            string? name = null;
            bool wasOnline;
            lock (_lock)
            {
                wasOnline = _online.TryGetValue(r.SteamId, out var p);
                if (wasOnline) name = p!.Name;
                _online.Remove(r.SteamId);
            }
            // Some builds print more than one line per disconnect; announce a leave once.
            if (!wasOnline) return;
            try { OnPlayerLeave?.Invoke(r.SteamId); } catch { }
            _ = _discord.SendAsync("Player left", $"{DiscordWebhook.Quote(DisplayName(name, r.SteamId))} disconnected.", DiscordWebhook.ColorYellow);
        }
    }

    static string DisplayName(string? name, string steamId) => string.IsNullOrEmpty(name) ? steamId : name;

    // Called with each A2S answer. If the server reports players for a while but no
    // join line has ever matched, the connect regex is not seeing this build's
    // output, and the roster, bans and the allowlist are blind. Say so once.
    public void ObserveA2SPlayers(int players)
    {
        string? warning = null;
        lock (_lock)
        {
            if (players <= 0 || _online.Count > 0) { _a2sPlayersSince = null; return; }
            var now = Now();
            _a2sPlayersSince ??= now;
            if (!_blindWarned && now - _a2sPlayersSince.Value >= BlindJoinGrace)
            {
                _blindWarned = true;
                warning = $"warn: A2S reports {players} player(s) but no join line has matched --connect-regex; the player roster, bans and the allowlist cannot see who joins on this server build";
            }
        }
        if (warning is null) return;
        _buffer.Append("system", warning);
        _audit.Record("join_detection_blind", new Dictionary<string, object?> { ["a2s_players"] = players });
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

    public void Dispose() => _buffer.OnAppend -= OnLine;
}
