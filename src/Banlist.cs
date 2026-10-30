using System.Text.Json;
using System.Text.Json.Serialization;

namespace SboxServerConsole;

// Persistent banlist with per-line enforcement, plus the online roster.
// Storage is a single JSON file; small enough to load and rewrite atomically.
// When a join line matches, the SteamID is checked; a banned player is kicked
// with the configured kick template. Leave lines keep the roster fresh.
// With --max-players, a joiner who arrives when that many players are already on
// is kicked with the reason "Server is full." (a dedicated s&box server keeps no
// limit of its own: it ignores +maxplayers).
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

    // The reason a joiner over --max-players sees. The engine shows it after its own "Kicked from server."
    public const string ServerFullReason = "Server is full.";

    // How long A2S may report players with no join line seen before we say the
    // join detection is blind.
    static readonly TimeSpan BlindJoinGrace = TimeSpan.FromMinutes(3);

    // A joiner logs its join line well before A2S counts it (about 25 s in one measured join),
    // so a roster entry younger than this cannot be checked against A2S yet.
    static readonly TimeSpan RosterConfirmGrace = TimeSpan.FromMinutes(3);

    // A2S must report an empty server for this long before entries with no leave line are dropped.
    static readonly TimeSpan A2SEmptyFor = TimeSpan.FromSeconds(60);

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
    DateTime? _a2sEmptySince;
    DateTime? _lastA2SAt;
    int _lastA2SPlayers;

    // Test seam for the blind-join grace period.
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    // Test seams: raised for an announced join (not a banned one) and an announced leave.
    internal event Action<Player>? OnPlayerJoin;
    internal event Action<string>? OnPlayerLeave;

    // A joiner another hook already kicks (the allowlist); the player limit leaves them to it.
    internal Func<string, bool>? RefusedElsewhere { get; set; }

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
            _a2sEmptySince = null;
            _lastA2SAt = null;
        }
    }

    void OnLine(MessageBuffer.Entry e)
    {
        if (e.Stream != "stdout") return;
        var r = _parser.Classify(e.Line);
        if (r.Kind == PlayerLineParser.Kind.Join)
        {
            // Chat or a console echo shaped like a join. It takes no slot and no roster place; a ban check
            // is still safe, because it can only kick the SteamID it names.
            if (!r.Trusted)
            {
                if (IsBanned(r.SteamId)) TryKick(r.SteamId);
                return;
            }
            var p = new Player(r.SteamId, r.Name, Now());
            // Players already on, the joiner left out, so a rejoin is not counted twice. The joiner still
            // enters the roster: its disconnected line clears it, and a kick that does not land is not undercounted.
            int alreadyOn;
            lock (_lock)
            {
                alreadyOn = _online.Keys.Count(id => id != r.SteamId);
                _online[r.SteamId] = p;
            }
            if (IsBanned(r.SteamId)) { TryKick(r.SteamId); return; }
            if (_cfg.MaxPlayers > 0 && alreadyOn >= _cfg.MaxPlayers && !(RefusedElsewhere?.Invoke(r.SteamId) ?? false))
            {
                if (ConfirmFull(r.SteamId, alreadyOn)) { KickFull(r.SteamId, alreadyOn); return; }
            }
            try { OnPlayerJoin?.Invoke(p); } catch { }
            _ = _discord.SendAsync("Player joined", $"{DiscordWebhook.Quote(DisplayName(r.Name, r.SteamId))} connected.", DiscordWebhook.ColorBlue);
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
        List<string>? dropped = null;
        lock (_lock)
        {
            var now = Now();
            _lastA2SPlayers = players;
            _lastA2SAt = now;
            // An empty server has no players: entries left behind by a missed leave line go, but not
            // anyone young enough to still be loading in.
            if (players > 0) _a2sEmptySince = null;
            else
            {
                _a2sEmptySince ??= now;
                if (now - _a2sEmptySince.Value >= A2SEmptyFor)
                {
                    dropped = _online.Values.Where(p => now - p.SeenAt >= RosterConfirmGrace).Select(p => p.SteamId).ToList();
                    foreach (var id in dropped) _online.Remove(id);
                }
            }
            if (players <= 0 || _online.Count > 0) _a2sPlayersSince = null;
            else
            {
                _a2sPlayersSince ??= now;
                if (!_blindWarned && now - _a2sPlayersSince.Value >= BlindJoinGrace)
                {
                    _blindWarned = true;
                    warning = $"warn: A2S reports {players} player(s) but no join line has matched --connect-regex; the player roster, bans and the allowlist cannot see who joins on this server build";
                }
            }
        }
        if (dropped is { Count: > 0 })
        {
            _buffer.Append("system", $"A2S reports an empty server: removed {dropped.Count} player(s) from the roster that had no leave line");
            _audit.Record("roster_cleared", new Dictionary<string, object?> { ["steamids"] = string.Join(",", dropped), ["count"] = dropped.Count });
        }
        if (warning is null) return;
        _buffer.Append("system", warning);
        _audit.Record("join_detection_blind", new Dictionary<string, object?> { ["a2s_players"] = players });
    }

    // The roster says the server is full. A wrong "Server is full." costs a real player the join, so ask A2S
    // too: players it counts, plus entries too young for it to have counted. No recent A2S answer: the roster stands.
    bool ConfirmFull(string joiner, int alreadyOn)
    {
        int a2s, recent;
        lock (_lock)
        {
            var now = Now();
            if (_lastA2SAt is null || now - _lastA2SAt.Value > TimeSpan.FromSeconds(Math.Max(30, _cfg.QueryPollSeconds) * 3)) return true;
            a2s = _lastA2SPlayers;
            recent = _online.Values.Count(p => p.SteamId != joiner && now - p.SeenAt < RosterConfirmGrace);
        }
        if (a2s + recent >= _cfg.MaxPlayers) return true;
        _buffer.Append("system", $"warn: the roster shows {alreadyOn} player(s) but A2S counts {a2s}; letting {joiner} in");
        _audit.Record("full_kick_skipped", new Dictionary<string, object?>
        {
            ["steamid"] = joiner,
            ["players"] = alreadyOn,
            ["a2s_players"] = a2s,
            ["recent"] = recent,
            ["max_players"] = _cfg.MaxPlayers,
        });
        return false;
    }

    void KickFull(string steamId, int alreadyOn)
    {
        var cmd = _cfg.KickCommandTemplate.Replace("{steamid}", steamId) + " \"" + ServerFullReason + "\"";
        bool ok = _server.TrySendCommand(cmd);
        _audit.Record("full_kick", new Dictionary<string, object?>
        {
            ["steamid"] = steamId,
            ["players"] = alreadyOn,
            ["max_players"] = _cfg.MaxPlayers,
            ["cmd"] = cmd,
            ["sent"] = ok,
        });
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
