namespace SboxServerConsole;

// File helpers shared by the banlist, allowlist and scheduler stores.
static class StoreFile
{
    // Write to a temp file next to the target, then move it into place, so a crash
    // mid-write never leaves a half-written store behind.
    public static void WriteAtomic(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    // Moves a store that could not be read out of the way, so the next save starts a
    // fresh file instead of overwriting the operator's data. Returns the new name,
    // or null if the file could not be moved.
    public static string? SetAside(string path)
    {
        try
        {
            var kept = $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, kept, overwrite: false);
            return kept;
        }
        catch { return null; }
    }
}
