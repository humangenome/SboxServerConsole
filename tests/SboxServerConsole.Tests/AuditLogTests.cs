using System.Text.Json;
using Xunit;

namespace SboxServerConsole.Tests;

// The audit log is hand-rolled JSONL rather than JsonSerializer output, so its
// escaping and append behaviour are worth pinning: it is the only forensic record
// of who executed what, and a malformed line makes the whole file unparseable for
// whatever tool is reading it.
public class AuditLogTests
{
    static List<JsonElement> Lines(string path)
        => File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();

    [Fact]
    public void AppendsOneJsonObjectPerLine()
    {
        using var s = new Scratch();
        var path = s.File("audit.jsonl");
        using (var audit = new AuditLog(path))
        {
            audit.Record("startup");
            audit.Record("execute", new Dictionary<string, object?> { ["cmd"] = "status", ["success"] = true });
            audit.Record("ban_add", new Dictionary<string, object?> { ["steamid"] = "76561198000000001" });
        }

        var lines = Lines(path);
        Assert.Equal(3, lines.Count);
        Assert.Equal(new[] { "startup", "execute", "ban_add" }, lines.Select(l => l.GetProperty("event").GetString()));
        Assert.All(lines, l => Assert.True(DateTime.TryParse(l.GetProperty("at").GetString(), out _)));

        // "at" and "event" lead every record; tooling relies on that.
        Assert.Equal("at", lines[0].EnumerateObject().First().Name);
        Assert.Equal("event", lines[0].EnumerateObject().Skip(1).First().Name);
    }

    [Fact]
    public void TypesKeepTheirJsonKind()
    {
        using var s = new Scratch();
        var path = s.File("audit.jsonl");
        using (var audit = new AuditLog(path))
        {
            audit.Record("mixed", new Dictionary<string, object?>
            {
                ["flag"] = true,
                ["off"] = false,
                ["count"] = 42,
                ["big"] = 9_000_000_000L,
                ["text"] = "hello",
                ["nothing"] = null,
            });
        }

        var e = Assert.Single(Lines(path));
        Assert.Equal(JsonValueKind.True, e.GetProperty("flag").ValueKind);
        Assert.Equal(JsonValueKind.False, e.GetProperty("off").ValueKind);
        Assert.Equal(42, e.GetProperty("count").GetInt32());
        Assert.Equal(9_000_000_000L, e.GetProperty("big").GetInt64());
        Assert.Equal("hello", e.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("nothing").ValueKind);
    }

    [Fact]
    public void EscapesEverythingThatWouldBreakTheLine()
    {
        using var s = new Scratch();
        var path = s.File("audit.jsonl");
        const string nasty = "quote\" backslash\\ newline\n carriage\r tab\t bell end";
        using (var audit = new AuditLog(path))
        {
            audit.Record("execute", new Dictionary<string, object?> { ["cmd"] = nasty });
        }

        // A newline inside a value must not split the record into two lines.
        Assert.Single(File.ReadAllLines(path), l => l.Length > 0);
        var e = Assert.Single(Lines(path));
        Assert.Equal(nasty, e.GetProperty("cmd").GetString());
    }

    [Fact]
    public void EscapesRawControlCharacters()
    {
        using var s = new Scratch();
        var path = s.File("audit.jsonl");
        string nasty = "before" + (char)0x01 + (char)0x1F + "after";
        using (var audit = new AuditLog(path))
        {
            audit.Record("execute", new Dictionary<string, object?> { ["cmd"] = nasty });
        }

        var e = Assert.Single(Lines(path));
        Assert.Equal(nasty, e.GetProperty("cmd").GetString());
    }

    [Fact]
    public void ConcurrentWritersDoNotInterleave()
    {
        using var s = new Scratch();
        var path = s.File("audit.jsonl");
        using (var audit = new AuditLog(path))
        {
            Parallel.For(0, 8, t =>
            {
                for (int i = 0; i < 50; i++)
                    audit.Record("execute", new Dictionary<string, object?> { ["thread"] = t, ["i"] = i });
            });
        }

        var lines = Lines(path); // throws if any line is torn
        Assert.Equal(400, lines.Count);
        Assert.Equal(400, lines.Select(l => (l.GetProperty("thread").GetInt32(), l.GetProperty("i").GetInt32())).Distinct().Count());
    }

    [Fact]
    public void DisabledWhenNoPathIsConfigured()
    {
        using var s = new Scratch();
        using var audit = new AuditLog(null);
        Assert.False(audit.Enabled);
        Assert.Null(audit.Path);
        audit.Record("startup", new Dictionary<string, object?> { ["x"] = 1 }); // must not throw
        Assert.Empty(Directory.GetFiles(s.Dir));

        using var blank = new AuditLog("   ");
        Assert.False(blank.Enabled);
        blank.Record("startup");
    }

    [Fact]
    public void CreatesTheParentDirectory()
    {
        using var s = new Scratch();
        var path = Path.Combine(s.Dir, "nested", "deeper", "audit.jsonl");
        using var audit = new AuditLog(path);
        audit.Record("startup");
        Assert.True(File.Exists(path));
    }
}
