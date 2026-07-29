using Etch.Core.Text;
using Xunit;

namespace Etch.Core.Tests.Text;

public class LineEndingsTests
{
    [Theory]
    [InlineData("", 0, 0, 0)]
    [InlineData("no breaks here", 0, 0, 0)]
    [InlineData("a\r\nb", 1, 0, 0)]
    [InlineData("a\nb", 0, 1, 0)]
    [InlineData("a\rb", 0, 0, 1)]
    [InlineData("a\r\nb\r\nc", 2, 0, 0)]
    [InlineData("a\r\nb\nc\rd", 1, 1, 1)]
    [InlineData("\r\n", 1, 0, 0)]
    [InlineData("\n\r", 0, 1, 1)]      // LF then CR is two separate breaks, not a pair
    [InlineData("\r\r\n", 1, 0, 1)]    // lone CR followed by a genuine CRLF
    public void Count_classifies_each_break(string text, int crlf, int lf, int cr)
    {
        var counts = LineEndings.Count(text);

        Assert.Equal(crlf, counts.Crlf);
        Assert.Equal(lf, counts.Lf);
        Assert.Equal(cr, counts.Cr);
    }

    [Fact]
    public void A_crlf_is_never_counted_as_a_separate_cr_and_lf()
    {
        // The classic off-by-one in newline counting: consuming the CR but not the
        // LF makes every Windows file look "mixed".
        var counts = LineEndings.Count("one\r\ntwo\r\nthree");

        Assert.Equal(2, counts.Crlf);
        Assert.Equal(0, counts.Lf);
        Assert.Equal(0, counts.Cr);
        Assert.Equal(2, counts.Total);
        Assert.False(counts.IsMixed);
    }

    [Theory]
    [InlineData("", LineEndingStyle.None)]
    [InlineData("single line", LineEndingStyle.None)]
    [InlineData("a\r\nb", LineEndingStyle.Crlf)]
    [InlineData("a\nb", LineEndingStyle.Lf)]
    [InlineData("a\rb", LineEndingStyle.Cr)]
    [InlineData("a\nb\nc\r\nd", LineEndingStyle.Lf)]        // majority wins
    [InlineData("a\r\nb\r\nc\nd", LineEndingStyle.Crlf)]
    public void Detect_reports_the_dominant_style(string text, LineEndingStyle expected)
    {
        Assert.Equal(expected, LineEndings.Detect(text));
    }

    [Fact]
    public void Ties_resolve_in_favour_of_crlf()
    {
        // Etch is a Windows editor; when the evidence is split, the Windows
        // convention is the least surprising default.
        Assert.Equal(LineEndingStyle.Crlf, new LineEndingCounts(1, 1, 1).Dominant);
        Assert.Equal(LineEndingStyle.Lf, new LineEndingCounts(0, 1, 1).Dominant);
        Assert.Equal(LineEndingStyle.Cr, new LineEndingCounts(0, 0, 1).Dominant);
        Assert.Equal(LineEndingStyle.None, new LineEndingCounts(0, 0, 0).Dominant);
    }

    [Fact]
    public void Mixed_endings_are_flagged()
    {
        Assert.True(LineEndings.Count("a\r\nb\nc").IsMixed);
        Assert.False(LineEndings.Count("a\r\nb\r\nc").IsMixed);
        Assert.False(LineEndings.Count("no breaks").IsMixed);
    }

    [Fact]
    public void A_crlf_straddling_the_sample_boundary_is_not_split()
    {
        // With a 2-character sample the window ends mid-CRLF. Naive sampling would
        // see a lone CR and report a classic-Mac file, which is wrong and would
        // corrupt line endings the moment anything rewrites the buffer.
        const string Text = "a\r\nbbbbbbbbbb";

        Assert.Equal(LineEndingStyle.Crlf, LineEndings.Detect(Text, sampleLength: 2));
    }

    [Fact]
    public void Detection_only_inspects_the_sample()
    {
        // The first 4 characters are LF-terminated; everything after is CRLF.
        // A bounded detector must answer from the prefix alone — that is the whole
        // reason detection stays constant-cost on a 50 MB file.
        var text = "a\nb\n" + string.Concat(Enumerable.Repeat("x\r\n", 500));

        Assert.Equal(LineEndingStyle.Lf, LineEndings.Detect(text, sampleLength: 4));
        Assert.Equal(LineEndingStyle.Crlf, LineEndings.Detect(text));
    }

    [Fact]
    public void Sample_length_longer_than_the_text_is_harmless()
    {
        Assert.Equal(LineEndingStyle.Lf, LineEndings.Detect("a\nb", sampleLength: 1_000_000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_nonpositive_sample_length_is_rejected(int sampleLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LineEndings.Detect("a\nb", sampleLength));
    }

    [Theory]
    [InlineData(LineEndingStyle.Crlf, "\r\n", "CRLF")]
    [InlineData(LineEndingStyle.Lf, "\n", "LF")]
    [InlineData(LineEndingStyle.Cr, "\r", "CR")]
    [InlineData(LineEndingStyle.None, "", "—")]
    public void Styles_render_as_literals_and_labels(LineEndingStyle style, string literal, string label)
    {
        Assert.Equal(literal, LineEndings.ToLiteral(style));
        Assert.Equal(label, LineEndings.ToDisplayName(style));
    }

    [Fact]
    public void Round_trip_a_literal_back_through_detection()
    {
        foreach (var style in new[] { LineEndingStyle.Crlf, LineEndingStyle.Lf, LineEndingStyle.Cr })
        {
            var literal = LineEndings.ToLiteral(style);

            Assert.Equal(style, LineEndings.Detect($"a{literal}b"));
        }
    }
}
