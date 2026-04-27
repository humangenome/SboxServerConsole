using System.Text;

namespace SboxServerConsole;

// Read-only access to log files inside a configured root directory.
// Used to expose recent server / engine logs through the dashboard so
// operators can grab what they need without RDP/FTP. All path resolution
// happens via Path.GetFullPath + a prefix check; we never trust the
// caller's filename relative to anywhere except the configured root.
public sealed class LogsBrowser
{
    public sealed record LogFile(string Name, long SizeBytes, DateTime ModifiedUtc);

    readonly string? _root;

    public LogsBrowser(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) { _root = null; return; }
        try
        {
            var full = Path.GetFullPath(root);
            _root = Directory.Exists(full) ? full : null;
        }
        catch { _root = null; }
    }

    public bool Enabled => _root is not null;
    public string? Root => _root;

    public IReadOnlyList<LogFile> List()
    {
        if (_root is null) return Array.Empty<LogFile>();
        try
        {
            var dir = new DirectoryInfo(_root);
            return dir.GetFiles()
                .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new LogFile(f.Name, f.Length, f.LastWriteTimeUtc))
                .ToList();
        }
        catch { return Array.Empty<LogFile>(); }
    }

    public bool TryResolve(string requestedName, out string fullPath)
    {
        fullPath = "";
        if (_root is null) return false;
        if (string.IsNullOrWhiteSpace(requestedName)) return false;
        if (requestedName.Contains('/') || requestedName.Contains('\\')) return false;
        if (requestedName.Contains("..")) return false;
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(_root, requestedName));
            // Defense in depth: confirm resolved path is still under root.
            if (!candidate.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return false;
            if (!File.Exists(candidate)) return false;
            fullPath = candidate;
            return true;
        }
        catch { return false; }
    }

    public string TailToString(string fullPath, int tailLines)
    {
        try
        {
            // Read whole file (logs are bounded by disk + rotation; capping read at 4MB).
            const long MaxRead = 4L * 1024 * 1024;
            var fi = new FileInfo(fullPath);
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fi.Length > MaxRead) fs.Seek(fi.Length - MaxRead, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var all = sr.ReadToEnd();
            if (tailLines <= 0) return all;
            var lines = all.Split('\n');
            int start = Math.Max(0, lines.Length - tailLines);
            return string.Join('\n', lines.Skip(start));
        }
        catch (Exception ex)
        {
            return $"[error reading {Path.GetFileName(fullPath)}: {ex.Message}]";
        }
    }
}
