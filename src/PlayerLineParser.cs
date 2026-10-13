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
public sealed class PlayerLineParser
{
    public enum Kind { None, Join, Leave }

    public readonly record struct Result(Kind Kind, string SteamId, string Name);

    // SteamID64: the only form the default connect regex can capture.
    static readonly Regex SteamId64 = new(@"^\d{17}$", RegexOptions.CultureInvariant);

    readonly Regex? _connect;
    readonly Regex? _disconnect;

    public PlayerLineParser(CliConfig cfg)
    {
        _connect = Compile(cfg.ConnectLineRegex);
        _disconnect = Compile(cfg.DisconnectLineRegex);
        UsesDefaultConnectRegex = cfg.ConnectLineRegex == CliConfig.DefaultConnectLineRegex;
    }

    public bool ConnectRegexConfigured => _connect is not null;
    public bool DisconnectRegexConfigured => _disconnect is not null;
    public bool UsesDefaultConnectRegex { get; }

    static Regex? Compile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.RightToLeft); }
        catch (ArgumentException) { return null; }
    }

    public Result Classify(string line)
    {
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
            return new Result(Kind.Join, join.Groups["steamid"].Value, name);
        }
        if (l) return new Result(Kind.Leave, leave!.Groups["steamid"].Value, "");
        return new Result(Kind.None, "", "");
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
