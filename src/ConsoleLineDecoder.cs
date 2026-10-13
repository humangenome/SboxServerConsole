using System.Text;

namespace SboxServerConsole;

// Turns the child's raw output bytes into text lines.
//
// The child's output arrives in arbitrary chunks, so two things must survive a
// chunk boundary: a terminal escape sequence (ConPTY emits cursor and colour
// sequences constantly) and a multi-byte UTF-8 character (player names and chat
// are not ASCII). Both used to be handled one byte at a time with no state, so a
// split escape leaked its tail ("32m", "1;1H") into the console and every
// non-ASCII character came out as two or three Latin-1 symbols.
//
// One instance per child process; Feed() is called from the output pump thread only.
public sealed class ConsoleLineDecoder
{
    // Longest line kept in one piece. Longer output is split, never mid-character.
    public const int MaxLineBytes = 4096;

    enum State { Text, Escape, Csi, Osc, OscEscape, Charset }

    State _state = State.Text;
    readonly List<byte> _line = new(256);
    readonly Action<string> _emit;
    readonly bool _breakOnCursorMove;

    // breakOnCursorMove: treat a cursor-positioning sequence as the end of a line.
    // Used when the agent is the child's terminal (Linux PTY): the server's console
    // repositions the cursor to redraw its status and input lines, and without the
    // break those fragments would be glued onto the next log line.
    public ConsoleLineDecoder(Action<string> emit, bool breakOnCursorMove = false)
    {
        _emit = emit;
        _breakOnCursorMove = breakOnCursorMove;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            switch (_state)
            {
                case State.Escape:
                    _state = b switch
                    {
                        (byte)'[' => State.Csi,
                        (byte)']' => State.Osc,
                        // ESC ( B and friends designate a character set and carry one more byte.
                        (byte)'(' or (byte)')' or (byte)'*' or (byte)'+' => State.Charset,
                        _ => State.Text,
                    };
                    continue;
                case State.Charset:
                    _state = State.Text;
                    continue;
                case State.Csi:
                    // Parameters and intermediates run 0x20-0x3F; the final byte is 0x40-0x7E.
                    if (b >= 0x40 && b <= 0x7E)
                    {
                        _state = State.Text;
                        if (_breakOnCursorMove && b is (byte)'H' or (byte)'f' or (byte)'A' or (byte)'B' or (byte)'G' or (byte)'d')
                            Flush();
                    }
                    else if (b == 0x1B) _state = State.Escape;
                    continue;
                case State.Osc:
                    // An OSC ends with BEL or with ST (ESC \).
                    if (b == 0x07) _state = State.Text;
                    else if (b == 0x1B) _state = State.OscEscape;
                    continue;
                case State.OscEscape:
                    _state = State.Text;
                    continue;
            }

            if (b == 0x1B) { _state = State.Escape; continue; }
            if (b == '\n') { Flush(); continue; }
            if (b == '\r') continue;
            if ((b < 0x20 && b != '\t') || b == 0x7F) continue;

            // Split an over-long line only in front of a byte that starts a character,
            // so a multi-byte UTF-8 sequence is never cut in half.
            if (_line.Count >= MaxLineBytes && (b & 0xC0) != 0x80) Flush();
            _line.Add(b);
        }
    }

    // Emits whatever is pending (call when the child's output ends).
    public void Flush()
    {
        if (_line.Count == 0) return;
        var text = Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_line));
        _line.Clear();
        _emit(text);
    }
}
