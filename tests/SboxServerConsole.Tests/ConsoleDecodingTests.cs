using System.Text;
using Xunit;

namespace SboxServerConsole.Tests;

// The child's output arrives in arbitrary chunks. These pin the two things that
// used to break at a chunk boundary: escape sequences and multi-byte characters.
public class ConsoleDecodingTests
{
    static List<string> Decode(bool breakOnCursorMove, params byte[][] chunks)
    {
        var lines = new List<string>();
        var d = new ConsoleLineDecoder(lines.Add, breakOnCursorMove);
        foreach (var c in chunks) d.Feed(c);
        d.Flush();
        return lines;
    }

    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void NonAsciiTextSurvivesIntact()
    {
        var lines = Decode(false, B("01:20:36 Generic  Zoë 漢字 Ödön [76561198000000001] is connecting\n"));
        Assert.Equal("01:20:36 Generic  Zoë 漢字 Ödön [76561198000000001] is connecting", Assert.Single(lines));
    }

    [Fact]
    public void AMultiByteCharacterSplitAcrossReadsIsNotMangled()
    {
        var bytes = B("名前\n");
        // Split inside the first character's three-byte sequence.
        var lines = Decode(false, bytes[..1], bytes[1..4], bytes[4..]);
        Assert.Equal("名前", Assert.Single(lines));
    }

    [Fact]
    public void AnEscapeSequenceSplitAcrossReadsLeaksNothing()
    {
        var lines = Decode(false, B("hello \x1b"), B("[3"), B("2mworld\x1b[0"), B("m\n"));
        Assert.Equal("hello world", Assert.Single(lines));
    }

    [Fact]
    public void OscTitlesEndWithBelOrSt()
    {
        var lines = Decode(false, B("\x1b]0;C:\\x\\powershell.exe\u0007a\n\x1b]0;title\x1b\\b\n"));
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void CharsetDesignationIsDropped()
    {
        var lines = Decode(false, B("\x1b(Bok\x1b=\n"));
        Assert.Equal("ok", Assert.Single(lines));
    }

    [Fact]
    public void CursorMovesSplitLinesOnlyWhenAsked()
    {
        var raw = B("status line\x1b[1;1Hlog line\n");
        Assert.Equal(new[] { "status linelog line" }, Decode(false, raw));
        Assert.Equal(new[] { "status line", "log line" }, Decode(true, raw));
    }

    [Fact]
    public void AnOverlongLineIsNeverCutMidCharacter()
    {
        var text = new string('a', ConsoleLineDecoder.MaxLineBytes - 1) + "é" + "tail";
        var lines = Decode(false, B(text + "\n"));
        Assert.True(lines.Count == 2, string.Join("|", lines.Select(l => l.Length)));
        Assert.DoesNotContain('\uFFFD', string.Concat(lines));
        Assert.Equal(text, string.Concat(lines));
    }
}
