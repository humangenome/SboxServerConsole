using System.Text;

namespace SboxServerConsole;

// JSONL append-only audit log. One event per line. Path optional —
// when null/empty the log is a no-op so callers don't need to branch.
// Rotated at 10MB; keeps audit.1..audit.10, drops the tail.
public sealed class AuditLog : IDisposable
{
    const long RotateBytes = 10L * 1024 * 1024;
    const int KeepGenerations = 10;

    readonly string? _path;
    readonly SemaphoreSlim _sem = new(1, 1);

    public AuditLog(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        if (_path is null) return;
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public bool Enabled => _path is not null;
    public string? Path => _path;

    public void Record(string eventType, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (_path is null) return;
        var sb = new StringBuilder(256);
        sb.Append('{');
        AppendField(sb, "at", DateTime.UtcNow.ToString("o"), first: true);
        AppendField(sb, "event", eventType, first: false);
        if (fields is not null)
        {
            foreach (var kvp in fields) AppendField(sb, kvp.Key, kvp.Value, first: false);
        }
        sb.Append("}\n");
        var line = sb.ToString();
        _sem.Wait();
        try
        {
            RotateIfNeeded();
            File.AppendAllText(_path, line);
        }
        catch { /* best-effort — never break the caller for an audit failure */ }
        finally { _sem.Release(); }
    }

    void RotateIfNeeded()
    {
        if (_path is null) return;
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < RotateBytes) return;
            // Delete the oldest, shift each .N up, then rename current to .1.
            var oldest = $"{_path}.{KeepGenerations}";
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int n = KeepGenerations - 1; n >= 1; n--)
            {
                var src = $"{_path}.{n}";
                var dst = $"{_path}.{n + 1}";
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }
            File.Move(_path, $"{_path}.1", overwrite: true);
        }
        catch { /* best-effort */ }
    }

    static void AppendField(StringBuilder sb, string key, object? value, bool first)
    {
        if (!first) sb.Append(',');
        AppendJsonString(sb, key);
        sb.Append(':');
        switch (value)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case int or long or short or byte: sb.Append(value.ToString()); break;
            default: AppendJsonString(sb, value.ToString() ?? ""); break;
        }
    }

    static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    public void Dispose() => _sem.Dispose();
}
