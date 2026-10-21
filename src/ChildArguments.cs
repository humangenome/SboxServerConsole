using System.Text;

namespace SboxServerConsole;

// How --child-args becomes the server's argument list.
//
// The agent splits the line once, itself: whitespace separates arguments and double
// quotes group words (the quotes are removed). No other character is special, so
// every argument reaches the server exactly as written:
//
//   +hostname "My Server" +port 27015   ->   [+hostname] [My Server] [+port] [27015]
//
// A pair of quotes with nothing between them is an empty argument; a quote that is
// never closed groups everything after it. The list is handed to the operating system
// as a list (an argv on Linux, a quoted Windows command line on Windows) and is never
// read by a shell.
public static class ChildArguments
{
    public static List<string> Split(string? line)
    {
        var args = new List<string>();
        if (string.IsNullOrEmpty(line)) return args;
        var current = new StringBuilder();
        bool inQuotes = false, inArgument = false;
        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                inArgument = true;
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (inArgument) { args.Add(current.ToString()); current.Clear(); inArgument = false; }
            }
            else
            {
                current.Append(c);
                inArgument = true;
            }
        }
        if (inArgument) args.Add(current.ToString());
        return args;
    }

    // One Windows command line from a list, quoted so the C runtime's parser (and
    // CommandLineToArgvW) reads back exactly the same list.
    public static string JoinForWindows(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            AppendWindowsArgument(sb, arg);
        }
        return sb.ToString();
    }

    static void AppendWindowsArgument(StringBuilder sb, string arg)
    {
        bool needsQuotes = arg.Length == 0;
        foreach (char c in arg)
        {
            if (c == '"' || char.IsWhiteSpace(c)) { needsQuotes = true; break; }
        }
        if (!needsQuotes) { sb.Append(arg); return; }

        sb.Append('"');
        for (int i = 0; i < arg.Length; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length)
            {
                // Backslashes before the closing quote are doubled so the quote stays a quote.
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(arg[i]);
            }
        }
        sb.Append('"');
    }
}
