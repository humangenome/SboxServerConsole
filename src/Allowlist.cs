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

    readonly SteamIdListFile<Entry> _store;
    readonly ServerProcess _server;
    readonly MessageBuffer _buffer;
    readonly AuditLog _audit;
    readonly CliConfig _cfg;
    readonly object _lock = new();
    readonly PlayerLineParser _parser;

    public Allowlist(CliConfig cfg, ServerProcess server, MessageBuffer buffer, AuditLog audit)
    {
        _cfg = cfg;
        _server = server;
        _buffer = buffer;
        _audit = audit;
        _parser = new PlayerLineParser(cfg);
        _store = new SteamIdListFile<Entry>(cfg.AllowlistPath, "allow", "allowlist", buffer,
            el => new Entry
            {
                SteamId = SteamIdListFile<Entry>.Str(el, "steamid"),
                Note = SteamIdListFile<Entry>.Str(el, "note"),
                AddedAt = SteamIdListFile<Entry>.Str(el, "added_at"),
                AddedBy = SteamIdListFile<Entry>.Str(el, "added_by"),
            },
            e => e.SteamId, e => e.AddedAt);
        // An unreadable file is kept. An allowlist that silently became empty would also
        // open a private server to everyone, so the warning says that plainly.
        _store.Load((path, kept, ex) => $"warn: allowlist {path} could not be read ({ex.Message}); the allowlist is EMPTY, so the server is open to everyone" +
            (kept is null ? ", and changes are NOT saved so the file is left as it is" : $"; the unreadable file was kept as {kept}"));
        _buffer.OnAppend += OnLine;
    }

    public bool Persisted => _store.Persisted;
    public bool Enforced { get { lock (_lock) return _store.Items.Count > 0; } }
    public int Count { get { lock (_lock) return _store.Items.Count; } }

    public IReadOnlyList<Entry> All()
    {
        lock (_lock) return _store.Ordered();
    }

    public bool Add(string steamId, string note, string by) => TryAdd(steamId, note, by, out _);

    public bool TryAdd(string steamId, string note, string by, out string error)
    {
        if (!_parser.IsUsableId(steamId, out error)) return false;
        lock (_lock)
        {
            _store.Items[steamId] = new Entry
            {
                SteamId = steamId,
                Note = note ?? "",
                AddedAt = DateTime.UtcNow.ToString("o"),
                AddedBy = by ?? "",
            };
            _store.Save();
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
            removed = _store.Items.Remove(steamId);
            if (removed) _store.Save();
        }
        if (removed)
        {
            _audit.Record("allow_remove", new Dictionary<string, object?> { ["steamid"] = steamId });
        }
        return removed;
    }

    public bool IsAllowed(string steamId)
    {
        lock (_lock) return _store.Items.ContainsKey(steamId);
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

    public void Dispose() => _buffer.OnAppend -= OnLine;
}
