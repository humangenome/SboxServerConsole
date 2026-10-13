using System.Text.Json;
using System.Text.Json.Serialization;

namespace SboxServerConsole;

// Symmetric inverse of Banlist: when the allowlist is non-empty, any connecting
// steamid NOT on the list is kicked. An empty allowlist disables enforcement
// entirely (open server, banlist still applies). Persistence and the connect
// hook reuse the same patterns Banlist uses.
public sealed class Allowlist : IDisposable
{
    public sealed class Entry
    {
        [JsonPropertyName("steamid")]   public string SteamId { get; set; } = "";
        [JsonPropertyName("note")]      public string Note { get; set; } = "";
        [JsonPropertyName("added_at")]  public string AddedAt { get; set; } = "";
        [JsonPropertyName("added_by")]  public string AddedBy { get; set; } = "";
    }

    sealed class AllowlistFile
    {
        [JsonPropertyName("allow")] public List<Entry> Allow { get; set; } = new();
    }

    readonly string? _path;
    readonly ServerProcess _server;
    readonly MessageBuffer _buffer;
    readonly AuditLog _audit;
    readonly CliConfig _cfg;
    readonly object _lock = new();
    readonly Dictionary<string, Entry> _allow = new(StringComparer.Ordinal);
    readonly PlayerLineParser _parser;
    bool _storeBlocked;

    public Allowlist(CliConfig cfg, ServerProcess server, MessageBuffer buffer, AuditLog audit)
    {
        _cfg = cfg;
        _server = server;
        _buffer = buffer;
        _audit = audit;
        _path = string.IsNullOrWhiteSpace(cfg.AllowlistPath) ? null : cfg.AllowlistPath;
        _parser = new PlayerLineParser(cfg);
        Load();
        _buffer.OnAppend += OnLine;
    }

    public bool Persisted => _path is not null;
    public bool Enforced { get { lock (_lock) return _allow.Count > 0; } }
    public int Count { get { lock (_lock) return _allow.Count; } }

    public IReadOnlyList<Entry> All()
    {
        lock (_lock) return _allow.Values.OrderBy(b => b.AddedAt).ToList();
    }

    public bool Add(string steamId, string note, string by) => TryAdd(steamId, note, by, out _);

    public bool TryAdd(string steamId, string note, string by, out string error)
    {
        if (!_parser.IsUsableId(steamId, out error)) return false;
        lock (_lock)
        {
            _allow[steamId] = new Entry
            {
                SteamId = steamId,
                Note = note ?? "",
                AddedAt = DateTime.UtcNow.ToString("o"),
                AddedBy = by ?? "",
            };
            Save();
        }
        _audit.Record("allow_add", new Dictionary<string, object?>
        {
            ["steamid"] = steamId,
            ["note"] = note,
            ["added_by"] = by,
        });
        return true;
    }

    public bool Remove(string steamId)
    {
        bool removed;
        lock (_lock)
        {
            removed = _allow.Remove(steamId);
            if (removed) Save();
        }
        if (removed)
        {
            _audit.Record("allow_remove", new Dictionary<string, object?> { ["steamid"] = steamId });
        }
        return removed;
    }

    public bool IsAllowed(string steamId)
    {
        lock (_lock) return _allow.ContainsKey(steamId);
    }

    void OnLine(MessageBuffer.Entry e)
    {
        if (e.Stream != "stdout") return;
        // Fast path: allowlist disabled (empty) means never enforce.
        if (!Enforced) return;
        var r = _parser.Classify(e.Line);
        if (r.Kind != PlayerLineParser.Kind.Join) return;
        if (!IsAllowed(r.SteamId)) TryKick(r.SteamId, "not on allowlist");
    }

    void TryKick(string steamId, string reason)
    {
        var cmd = _cfg.KickCommandTemplate.Replace("{steamid}", steamId);
        bool ok = _server.TrySendCommand(cmd);
        _audit.Record("allow_kick", new Dictionary<string, object?>
        {
            ["steamid"] = steamId,
            ["reason"] = reason,
            ["cmd"] = cmd,
            ["sent"] = ok,
        });
    }

    public void EnforceAgainstRoster(IReadOnlyList<Banlist.Player> snapshot)
    {
        if (!Enforced) return;
        foreach (var p in snapshot)
        {
            if (!IsAllowed(p.SteamId)) TryKick(p.SteamId, "not on allowlist (roster sweep)");
        }
    }

    void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("allow", out var arr)) return;
            foreach (var el in arr.EnumerateArray())
            {
                var e = new Entry
                {
                    SteamId = el.TryGetProperty("steamid", out var s) ? s.GetString() ?? "" : "",
                    Note = el.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "",
                    AddedAt = el.TryGetProperty("added_at", out var a) ? a.GetString() ?? "" : "",
                    AddedBy = el.TryGetProperty("added_by", out var u) ? u.GetString() ?? "" : "",
                };
                if (!string.IsNullOrEmpty(e.SteamId)) _allow[e.SteamId] = e;
            }
        }
        catch (Exception ex)
        {
            // Keep the unreadable file. An allowlist that silently became empty would
            // also open a private server to everyone, so say that plainly.
            string? kept = StoreFile.SetAside(_path);
            _allow.Clear();
            _storeBlocked = kept is null;
            _buffer.Append("system", $"warn: allowlist {_path} could not be read ({ex.Message}); the allowlist is EMPTY, so the server is open to everyone" +
                (kept is null ? ", and changes are NOT saved so the file is left as it is" : $"; the unreadable file was kept as {kept}"));
        }
    }

    void Save()
    {
        if (_path is null || _storeBlocked) return;
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var data = new AllowlistFile { Allow = _allow.Values.OrderBy(b => b.AddedAt).ToList() };
        try { StoreFile.WriteAtomic(_path, JsonSerializer.Serialize(data, opts)); }
        catch (Exception ex)
        {
            _buffer.Append("system", $"allowlist save failed: {ex.Message}");
        }
    }

    public void Dispose() => _buffer.OnAppend -= OnLine;
}
