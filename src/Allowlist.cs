using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    readonly Regex? _connectRe;

    public Allowlist(CliConfig cfg, ServerProcess server, MessageBuffer buffer, AuditLog audit)
    {
        _cfg = cfg;
        _server = server;
        _buffer = buffer;
        _audit = audit;
        _path = string.IsNullOrWhiteSpace(cfg.AllowlistPath) ? null : cfg.AllowlistPath;
        _connectRe = TryCompile(cfg.ConnectLineRegex);
        Load();
        _buffer.OnAppend += OnLine;
    }

    static Regex? TryCompile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return null; }
    }

    public bool Persisted => _path is not null;
    public bool Enforced { get { lock (_lock) return _allow.Count > 0; } }
    public int Count { get { lock (_lock) return _allow.Count; } }

    public IReadOnlyList<Entry> All()
    {
        lock (_lock) return _allow.Values.OrderBy(b => b.AddedAt).ToList();
    }

    public bool Add(string steamId, string note, string by)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return false;
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
        if (_connectRe is null) return;
        // Fast path: allowlist disabled (empty) → never enforce.
        if (!Enforced) return;
        var m = _connectRe.Match(e.Line);
        if (!m.Success) return;
        string sid = m.Groups["steamid"].Value;
        if (string.IsNullOrEmpty(sid)) return;
        if (!IsAllowed(sid)) TryKick(sid, "not on allowlist");
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
            _buffer.Append("system", $"allowlist load failed: {ex.Message}");
        }
    }

    void Save()
    {
        if (_path is null) return;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var data = new AllowlistFile { Allow = _allow.Values.OrderBy(b => b.AddedAt).ToList() };
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, opts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _buffer.Append("system", $"allowlist save failed: {ex.Message}");
        }
    }

    public void Dispose() => _buffer.OnAppend -= OnLine;
}
