using Etch.Core.Documents;
using Xunit;

namespace Etch.Core.Tests.Documents;

public class DocumentSizePolicyTests
{
    private const long Kib = 1024;
    private const long Mib = 1024 * Kib;

    [Theory]
    [InlineData(0, DocumentTier.Full)]
    [InlineData(1, DocumentTier.Full)]
    [InlineData(2 * Mib, DocumentTier.Full)]              // exactly at the threshold stays full
    [InlineData((2 * Mib) + 1, DocumentTier.Reduced)]     // one byte over degrades
    [InlineData(10 * Mib, DocumentTier.Reduced)]
    [InlineData((10 * Mib) + 1, DocumentTier.PlainText)]
    [InlineData(100 * Mib, DocumentTier.PlainText)]
    [InlineData((100 * Mib) + 1, DocumentTier.Rejected)]
    public void Evaluate_assigns_the_tier_for_a_size(long sizeInBytes, DocumentTier expected)
    {
        var capabilities = DocumentSizePolicy.Default.Evaluate(sizeInBytes);

        Assert.Equal(expected, capabilities.Tier);
    }

    [Fact]
    public void Small_documents_get_everything()
    {
        var capabilities = DocumentSizePolicy.Default.Evaluate(64 * Kib);

        Assert.Equal(DocumentTier.Full, capabilities.Tier);
        Assert.True(capabilities.CanOpen);
        Assert.True(capabilities.SyntaxHighlighting);
        Assert.True(capabilities.Folding);
        Assert.True(capabilities.DetectOnEdit);
        Assert.True(capabilities.Journaling);
        Assert.False(capabilities.IsDegraded);
        Assert.Null(capabilities.Notice);
    }

    [Fact]
    public void Reduced_documents_lose_folding_but_keep_highlighting_and_journaling()
    {
        var capabilities = DocumentSizePolicy.Default.Evaluate(5 * Mib);

        Assert.False(capabilities.Folding);
        Assert.False(capabilities.DetectOnEdit);
        Assert.True(capabilities.SyntaxHighlighting);
        Assert.True(capabilities.Journaling);
        Assert.True(capabilities.CanOpen);
    }

    [Fact]
    public void Plain_text_documents_are_not_journaled()
    {
        // Journaling a 50 MB buffer would thrash the disk for no benefit; this is
        // the one place the no-save-dialog promise is deliberately not kept, so it
        // gets an explicit test rather than being an accident of the thresholds.
        var capabilities = DocumentSizePolicy.Default.Evaluate(50 * Mib);

        Assert.Equal(DocumentTier.PlainText, capabilities.Tier);
        Assert.True(capabilities.CanOpen);
        Assert.False(capabilities.Journaling);
        Assert.False(capabilities.SyntaxHighlighting);
    }

    [Fact]
    public void Rejected_documents_cannot_be_opened_and_say_why()
    {
        var capabilities = DocumentSizePolicy.Default.Evaluate(250 * Mib);

        var notice = capabilities.Notice;

        Assert.False(capabilities.CanOpen);
        Assert.NotNull(notice);
        Assert.Contains("exceeds", notice!, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_degraded_tier_explains_itself()
    {
        foreach (var size in new[] { 5 * Mib, 50 * Mib, 250 * Mib })
        {
            var capabilities = DocumentSizePolicy.Default.Evaluate(size);

            Assert.True(capabilities.IsDegraded);
            Assert.False(string.IsNullOrWhiteSpace(capabilities.Notice));
        }
    }

    [Theory]
    [InlineData(0, 10, 20)]         // reduced threshold must be positive
    [InlineData(-1, 10, 20)]
    [InlineData(10, 10, 20)]        // thresholds must strictly ascend
    [InlineData(10, 5, 20)]
    [InlineData(10, 20, 20)]
    [InlineData(10, 20, 15)]
    public void Nonsensical_thresholds_are_rejected(long reduced, long plainText, long ceiling)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DocumentSizePolicy(reduced, plainText, ceiling));
    }

    [Fact]
    public void Negative_sizes_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentSizePolicy.Default.Evaluate(-1));
    }

    [Fact]
    public void Custom_thresholds_are_honoured()
    {
        var policy = new DocumentSizePolicy(1 * Kib, 2 * Kib, 4 * Kib);

        Assert.Equal(DocumentTier.Full, policy.Evaluate(1 * Kib).Tier);
        Assert.Equal(DocumentTier.Reduced, policy.Evaluate((1 * Kib) + 1).Tier);
        Assert.Equal(DocumentTier.PlainText, policy.Evaluate((2 * Kib) + 1).Tier);
        Assert.Equal(DocumentTier.Rejected, policy.Evaluate((4 * Kib) + 1).Tier);
    }

    [Theory]
    [InlineData(64 * Kib)]
    [InlineData(5 * Mib)]
    [InlineData(50 * Mib)]
    public void Without_long_lines_both_ways_of_asking_agree(long sizeInBytes)
    {
        // The overload without the line length is the old API. It has to go on meaning
        // exactly what it meant, notice text included, or every existing caller's
        // behaviour moved when a parameter was added beside it.
        Assert.Equal(
            DocumentSizePolicy.Default.Evaluate(sizeInBytes),
            DocumentSizePolicy.Default.Evaluate(sizeInBytes, hasLongLines: false));
    }

    [Theory]
    [InlineData(64 * Kib)]
    [InlineData(5 * Mib)]
    public void A_long_line_turns_highlighting_and_folding_off_whatever_the_size(long sizeInBytes)
    {
        // The measured failure: a 2 MB line of minified JSON is well inside the full tier,
        // and highlighting it did not finish in three minutes.
        var plain = DocumentSizePolicy.Default.Evaluate(sizeInBytes);
        var capabilities = DocumentSizePolicy.Default.Evaluate(sizeInBytes, hasLongLines: true);

        Assert.False(capabilities.SyntaxHighlighting);
        Assert.False(capabilities.Folding);

        // Only the view is affected: the tier, detection and auto-save are about how much
        // text there is, and a long line is not more text.
        Assert.Equal(plain.Tier, capabilities.Tier);
        Assert.Equal(plain.DetectOnEdit, capabilities.DetectOnEdit);
        Assert.Equal(plain.Journaling, capabilities.Journaling);
        Assert.True(capabilities.CanOpen);
    }

    [Fact]
    public void A_long_line_says_what_was_taken_away_and_where_the_limit_is()
    {
        var notice = DocumentSizePolicy.Default.Evaluate(64 * Kib, hasLongLines: true).Notice;

        Assert.NotNull(notice);
        Assert.Contains("10,000", notice!, StringComparison.Ordinal);
        Assert.Contains("highlighting", notice!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_line_in_a_large_document_keeps_the_size_sentence_too()
    {
        var notice = DocumentSizePolicy.Default.Evaluate(5 * Mib, hasLongLines: true).Notice;

        Assert.NotNull(notice);
        Assert.StartsWith(DocumentSizePolicy.Default.Evaluate(5 * Mib).Notice!, notice!, StringComparison.Ordinal);
        Assert.Contains("10,000", notice!, StringComparison.Ordinal);
    }

    [Fact]
    public void Reassess_follows_the_size_for_what_the_editor_switches_on()
    {
        // A scratch tab that has been pasted into: the tier moves with the text, which is the
        // whole point, because the tab was born empty and in the full tier.
        var capabilities = DocumentSizePolicy.Default.Reassess(50 * Mib, hasLongLines: false, journaling: true);

        Assert.Equal(DocumentTier.PlainText, capabilities.Tier);
        Assert.False(capabilities.SyntaxHighlighting);
        Assert.False(capabilities.Folding);
        Assert.False(capabilities.DetectOnEdit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reassess_carries_journaling_through_whatever_the_size(bool journaling)
    {
        // Withdrawing it from a tab that grew would leave a stale shadow copy behind, and
        // granting it to one that shrank buys nothing. Either way it is not the size's call.
        foreach (var size in new[] { 0L, 5 * Mib, 50 * Mib, 250 * Mib })
        {
            Assert.Equal(journaling, DocumentSizePolicy.Default.Reassess(size, hasLongLines: false, journaling).Journaling);
        }
    }

    [Fact]
    public void A_journaled_plain_text_document_is_not_told_its_auto_save_is_off()
    {
        // The plain-text sentence used to say "auto-save off" unconditionally, which is
        // false of a scratch tab: it is journaled whatever its size.
        var notice = DocumentSizePolicy.Default.Reassess(50 * Mib, hasLongLines: false, journaling: true).Notice;

        Assert.NotNull(notice);
        Assert.DoesNotContain("auto-save", notice!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_open_document_past_the_ceiling_is_reassessed_rather_than_refused()
    {
        // A buffer restored after the ceiling was lowered is already on screen; refusing it
        // is not an option any more, and it must not keep features the ceiling exists to
        // withhold.
        var capabilities = DocumentSizePolicy.Default.Reassess(250 * Mib, hasLongLines: false, journaling: true);

        Assert.Equal(DocumentTier.Rejected, capabilities.Tier);
        Assert.False(capabilities.SyntaxHighlighting);
        Assert.False(capabilities.Folding);
        Assert.False(capabilities.DetectOnEdit);
        Assert.True(capabilities.Journaling);
        Assert.False(string.IsNullOrWhiteSpace(capabilities.Notice));
    }

    [Fact]
    public void Reassess_rejects_negative_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DocumentSizePolicy.Default.Reassess(-1, hasLongLines: false, journaling: true));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(512, "512 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024, "1 MB")]
    [InlineData(52_428_800, "50 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    public void Describe_formats_sizes_for_humans(long bytes, string expected)
    {
        Assert.Equal(expected, DocumentSizePolicy.Describe(bytes));
    }

    [Fact]
    public void Describe_rejects_negative_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentSizePolicy.Describe(-1));
    }
}
