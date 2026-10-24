using System.Text.Json;

namespace SboxServerConsole;

// The on-disk list behind the banlist and the allowlist: one JSON object holding one
// array of SteamID entries ({"bans":[...]} or {"allow":[...]}), read once at start and
// rewritten atomically on every change. Not thread-safe: the owner holds its own lock
// around every call.
sealed class SteamIdListFile<T> where T : class
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    readonly string? _path;
    readonly string _arrayKey;
    readonly string _label;
    readonly MessageBuffer _buffer;
    readonly Func<JsonElement, T> _read;
    readonly Func<T, string> _id;
    readonly Func<T, string> _addedAt;
    bool _blocked;

    public Dictionary<string, T> Items { get; } = new(StringComparer.Ordinal);
    public bool Persisted => _path is not null;

    public SteamIdListFile(string? path, string arrayKey, string label, MessageBuffer buffer,
        Func<JsonElement, T> read, Func<T, string> id, Func<T, string> addedAt)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _arrayKey = arrayKey;
        _label = label;
        _buffer = buffer;
        _read = read;
        _id = id;
        _addedAt = addedAt;
    }

    // Reads the file. A file that cannot be read is set aside, never overwritten; if it
    // cannot be moved either, changes stop being saved so the file stays as it is.
    // warning(path, keptAs or null, error) is the console line that says so.
    public void Load(Func<string, string?, Exception, string> warning)
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (!doc.RootElement.TryGetProperty(_arrayKey, out var arr)) return;
            foreach (var el in arr.EnumerateArray())
            {
                var item = _read(el);
                string id = _id(item);
                if (!string.IsNullOrEmpty(id)) Items[id] = item;
            }
        }
        catch (Exception ex)
        {
            string? kept = StoreFile.SetAside(_path);
            Items.Clear();
            _blocked = kept is null;
            _buffer.Append("system", warning(_path, kept, ex));
        }
    }

    public List<T> Ordered() => Items.Values.OrderBy(_addedAt, StringComparer.Ordinal).ToList();

    public void Save()
    {
        if (_path is null || _blocked) return;
        var data = new Dictionary<string, List<T>> { [_arrayKey] = Ordered() };
        try { StoreFile.WriteAtomic(_path, JsonSerializer.Serialize(data, Indented)); }
        catch (Exception ex) { _buffer.Append("system", $"{_label} save failed: {ex.Message}"); }
    }

    public static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
}
