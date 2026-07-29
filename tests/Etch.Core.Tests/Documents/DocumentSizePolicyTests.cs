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
