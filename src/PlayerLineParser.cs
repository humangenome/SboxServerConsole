using System.Text.RegularExpressions;

namespace SboxServerConsole;

// Classifies one server output line as a player join, a player leave, or neither.
// Shared by the banlist (roster + bans) and the allowlist so both read every line
// the same way.
//
// Player names are attacker-controlled text that lands on the same line as the
// real SteamID. The engine prints the real SteamID last ("<name> [<id>] is
// connecting"), so both regexes run right to left and, when a line matches both,
// the match that ends further right wins. A player named
// "Bob [76561198000000000] is connecting" therefore cannot make the agent kick,
// ban-check or list somebody else.
//
// Other text reaches the same output. A dedicated server logs every chat line as
// "<time> <channel>  <name>: <message>", and the console echoes a typed command as
// "> <command>". So a matching line only counts as a real join when it is the
// engine's own: its time prefix, nothing shaped like chat or an echo before the
// SteamID, and nothing after the closing words. Trusted says which it was. An untrusted join may still be ban-checked
// (that can only kick the SteamID it names), but it must not enter the roster or
// take a slot. An untrusted leave is ignored, so chat cannot take a player off the
// roster either.
public sealed class PlayerLineParser
{
    public enum Kind { None, Join, Leave }

    public readonly record struct Result(Kind Kind, string SteamId, string Name, bool Trusted = true);

    // The engine's own log prefix, for example "10:30:48 Generic  ".
    static readonly Regex TimePrefix = new(@"^\d{1,2}:\d{2}:\d{2}\s+\S+\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // That prefix alone on a line. The server's console redraw can split a log line
    // right after it, so the next line is the rest of the same log line.
    static readonly Regex BareTimePrefix = new(@"^\d{1,2}:\d{2}:\d{2}(?:\s+\S+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    bool _afterBarePrefix;

    // SteamID64: the only form the default connect regex can capture.
    static readonly Regex SteamId64 = new(@"^\d{17}$", RegexOptions.CultureInvariant);

    readonly Regex? _connect;
    readonly Regex? _disconnect;

    public PlayerLineParser(CliConfig cfg)
    {
        _connect = Compile(cfg.ConnectLineRegex);
        _disconnect = Compile(cfg.DisconnectLineRegex);
        UsesDefaultConnectRegex = cfg.ConnectLineRegex == CliConfig.DefaultConnectLineRegex;
        _checkLeaveOrigin = cfg.DisconnectLineRegex == CliConfig.DefaultDisconnectLineRegex;
    }

    // A custom regex describes its own line shape, so these checks only apply to the defaults.
    readonly bool _checkLeaveOrigin;

    public bool ConnectRegexConfigured => _connect is not null;
    public bool DisconnectRegexConfigured => _disconnect is not null;
    public bool UsesDefaultConnectRegex { get; }

    static Regex? Compile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.RightToLeft); }
        catch (ArgumentException) { return null; }
    }

    // Feed every stdout line in order: a line split after its time prefix is read across two calls.
    public Result Classify(string line)
    {
        bool continued = _afterBarePrefix;
        _afterBarePrefix = BareTimePrefix.IsMatch(line.Trim());
        Match? join = _connect?.Match(line);
        Match? leave = _disconnect?.Match(line);
        bool j = join is { Success: true } && join.Groups["steamid"].Success && join.Groups["steamid"].Length > 0;
        bool l = leave is { Success: true } && leave.Groups["steamid"].Success && leave.Groups["steamid"].Length > 0;
        if (j && l)
        {
            if (join!.Index + join.Length >= leave!.Index + leave.Length) l = false;
            else j = false;
        }
        if (j)
        {
            string name = join!.Groups["name"].Success ? join.Groups["name"].Value.Trim() : "";
            string steamId = join.Groups["steamid"].Value;
            if (!UsesDefaultConnectRegex) return new Result(Kind.Join, steamId, name);
            // The "[" before the SteamID.
            var origin = ReadOrigin(line[..Math.Max(0, join.Groups["steamid"].Index - 1)]);
            bool trusted = !origin.Echo && !origin.Chat && (origin.HasPrefix || continued) && EndsTheLine(line, join);
            if (trusted && !origin.HasPrefix && origin.Text.Length > 0) name = origin.Text;
            return new Result(Kind.Join, steamId, name, trusted);
        }
        if (l)
        {
            if (_checkLeaveOrigin)
            {
                var origin = ReadOrigin(line[..leave!.Index]);
                if (origin.Echo || origin.Chat) return new Result(Kind.None, "", "");
                // "[id] disconnected" in the middle of a line is a name, as in "<name>: hello".
                if (leave.Value.StartsWith('[') && !EndsTheLine(line, leave)) return new Result(Kind.None, "", "");
            }
            return new Result(Kind.Leave, leave!.Groups["steamid"].Value, "");
        }
        return new Result(Kind.None, "", "");
    }

    // The engine's join and leave lines stop right after those words. A name holding the same words is
    // followed by more text in any other line that prints it, for example "<name>: hello".
    static bool EndsTheLine(string line, Match m) => m.Index + m.Length >= line.TrimEnd().Length;

    readonly record struct Origin(bool HasPrefix, bool Echo, bool Chat, string Text);

    // Reads the text before the SteamID. Chat always has ": " after the sender's name, even
    // when the message starts with the fake SteamID (then the text ends in ":").
    static Origin ReadOrigin(string before)
    {
        var text = before.Trim();
        var prefix = TimePrefix.Match(text + " ");
        if (prefix.Success) text = (text + " ")[prefix.Length..].Trim();
        return new Origin(prefix.Success, text.StartsWith('>'), text.Contains(": ", StringComparison.Ordinal) || text.EndsWith(':'), text);
    }

    // Validates an ID an operator wants to ban or allow. With the default connect
    // regex only a SteamID64 can ever match a join line, so anything else would be
    // a ban that silently never fires. A custom regex may capture other forms, so
    // there the check is only that the ID is one plain token.
    public bool IsUsableId(string? id, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(id)) { error = "steamid required"; return false; }
        if (UsesDefaultConnectRegex)
        {
            if (SteamId64.IsMatch(id)) return true;
            error = "steamid must be a 17-digit SteamID64 (for example 76561197960287930)";
            return false;
        }
        if (id.Length > 64 || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            error = "steamid must be one token of at most 64 characters";
            return false;
        }
        return true;
    }
}
