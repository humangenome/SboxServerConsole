using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SboxServerConsole.Tests;

// docs/api.md is the contract third-party clients build against, and it drifted
// badly once: it documented a GET /metrics that never existed while omitting
// /version, the whole /allows API, /logs, and the lifecycle routes. These tests
// compare the document against the source of truth so drift fails the build
// instead of waiting for someone to notice.
public class DocsDriftTests
{
    static string Api => Res.Read("docs.api.md");

    // Routes as the dispatcher sees them. Handle() is one flat if-chain of
    // `path == "/x"` and `path.StartsWith("/x/")`, so the source is the route table.
    static SortedSet<string> RoutesInSource()
    {
        string src = Res.Read("src.HttpApi.cs");
        int start = src.IndexOf("void Handle(HttpListenerContext ctx)", StringComparison.Ordinal);
        Assert.True(start >= 0, "HttpApi.Handle() not found — the route-table test needs updating");
        int end = src.IndexOf("void RequireAuth(", start, StringComparison.Ordinal);
        Assert.True(end > start, "HttpApi.RequireAuth() not found — the route-table test needs updating");
        string body = src[start..end];

        var routes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(body, @"path == ""(?<p>/[^""]*)"""))
            routes.Add(Normalize(m.Groups["p"].Value));
        foreach (Match m in Regex.Matches(body, @"path\.StartsWith\(""(?<p>/[^""]*)"""))
            routes.Add(Normalize(m.Groups["p"].Value));
        return routes;
    }

    // Routes as the document describes them: `GET /health` headings plus the
    // METHOD/path tables in the Bans, Allows, Scheduler, and Logs sections.
    static SortedSet<string> RoutesInDocs()
    {
        var routes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(Api, @"\b(?:GET|POST|PUT|DELETE|PATCH)\s+(?<p>/[A-Za-z0-9_./<>{}-]*)"))
            routes.Add(Normalize(m.Groups["p"].Value));
        return routes;
    }

    // "/bans/<steamid>" and "/scheduler/<id>/enable" are the same dispatcher entry
    // as their parent prefix; "/index.html" is an alias of "/".
    static string Normalize(string path)
    {
        path = path.Split('?')[0];
        if (path == "/index.html") return "/";
        if (path.Length > 1) path = path.TrimEnd('/');
        var kept = new List<string>();
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg.Contains('<') || seg.Contains('{')) break;
            kept.Add(seg);
        }
        return kept.Count == 0 ? "/" : "/" + string.Join('/', kept);
    }

    [Fact]
    public void DocumentedRoutesMatchTheDispatcher()
    {
        var code = RoutesInSource();
        var docs = RoutesInDocs();

        var undocumented = code.Except(docs).ToList();
        var phantom = docs.Except(code).ToList();

        Assert.True(undocumented.Count == 0,
            "routes exist but docs/api.md does not document them: " + string.Join(", ", undocumented));
        Assert.True(phantom.Count == 0,
            "docs/api.md documents routes that do not exist: " + string.Join(", ", phantom));
    }

    [Fact]
    public void RouteExtractionActuallyFoundSomething()
    {
        // Guards against a refactor silently turning both sides into empty sets,
        // which would make the comparison above pass for the wrong reason.
        var code = RoutesInSource();
        Assert.True(code.Count >= 12, "expected the dispatcher to expose at least 12 routes, found " + code.Count);
        Assert.Contains("/health", code);
        Assert.Contains("/server/restart", code);
    }

    // Config keys the parser actually reads, harvested from CliConfig.cs.
    static SortedSet<string> ConfigKeysInSource()
    {
        string src = Res.Read("src.CliConfig.cs");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pattern in new[]
        {
            @"\bGet(?:Int|Bool)?\(""(?<k>[a-z0-9-]+)""",
            @"raw\.TryGetValue\(""(?<k>[a-z0-9-]+)""",
            @"key == ""(?<k>[a-z0-9-]+)""",
            @"== ""--(?<k>[a-z0-9-]+)""",
        })
        {
            foreach (Match m in Regex.Matches(src, pattern)) keys.Add(m.Groups["k"].Value);
        }
        return keys;
    }

    [Fact]
    public void FlagsMentionedInTheApiDocsExist()
    {
        var known = ConfigKeysInSource();
        Assert.True(known.Count >= 20, "config-key extraction found only " + known.Count + " keys");

        var mentioned = Regex.Matches(Api, @"--(?<f>[a-z][a-z0-9-]+)")
            .Select(m => m.Groups["f"].Value)
            .Distinct()
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(mentioned);

        var phantom = mentioned.Where(f => !known.Contains(f)).ToList();
        Assert.True(phantom.Count == 0,
            "docs/api.md references flags the parser does not know: " + string.Join(", ", phantom.Select(f => "--" + f)));
    }

    // Every documented example payload must be a subset of what the route really
    // returns. Catches renamed and removed fields, which is how /status drifted.
    public static TheoryData<string, string> DocumentedPayloads() => new()
    {
        { "### `GET /health`", "/health" },
        { "### `GET /version`", "/version" },
        { "### `GET /status`", "/status" },
        { "### `GET /history?count=N`", "/history" },
        { "### `GET /players`", "/players" },
        { "### Logs", "/logs" },
    };

    [Theory]
    [MemberData(nameof(DocumentedPayloads))]
    public async Task DocumentedPayloadKeysExist(string heading, string route)
    {
        string? example = FirstJsonExampleAfter(Api, heading);
        Assert.NotNull(example);

        using var scratch = new Scratch();
        var logDir = Path.Combine(scratch.Dir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "server.log"), "hello\n");

        using var h = ApiHost.Create("--logs-dir", logDir);
        h.Buffer.Append("stdout", "a line so /history has something to return");

        var live = await h.Json("GET", route);
        using var doc = JsonDocument.Parse(example!);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            Assert.True(live.TryGetProperty(prop.Name, out _),
                $"docs/api.md documents \"{prop.Name}\" on {route} but the response has no such key");
        }
    }

    static string? FirstJsonExampleAfter(string md, string heading)
    {
        int at = md.IndexOf(heading, StringComparison.Ordinal);
        if (at < 0) return null;
        int fence = md.IndexOf("```json", at, StringComparison.Ordinal);
        if (fence < 0) return null;
        int nextHeading = md.IndexOf("\n### ", at + heading.Length, StringComparison.Ordinal);
        if (nextHeading >= 0 && fence > nextHeading) return null; // fence belongs to a later section
        int bodyStart = fence + "```json".Length;
        int close = md.IndexOf("```", bodyStart, StringComparison.Ordinal);
        if (close < 0) return null;
        return md[bodyStart..close].Trim();
    }

    [Fact]
    public void ApiDocsCoverTheAuthContract()
    {
        var api = Api;
        Assert.Contains("X-RCON-Password", api);
        Assert.Contains("Bearer", api);
        Assert.Contains("?password=", api);
        Assert.Contains("503", api);
    }
}
