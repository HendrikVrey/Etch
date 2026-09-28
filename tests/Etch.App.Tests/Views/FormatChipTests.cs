using Etch.App.Views;
using Etch.Core.Abstractions;
using Xunit;

namespace Etch.App.Tests.Views;

/// <summary>
/// The status bar's format chip.
/// </summary>
/// <remarks>
/// One switch, and one way for it to go wrong: its default arm returns "Plain text", so a
/// <see cref="FormatId"/> added without a case reports a recognised buffer as unrecognised
/// and nothing fails. That is the same shape of defect as the missing <c>Ctrl+T</c> (every
/// mechanism works, and no artefact says what is missing) so it gets the same treatment,
/// a test that walks the enum.
/// </remarks>
public class FormatChipTests
{
    [Fact]
    public void Every_recognised_format_has_a_label_of_its_own()
    {
        foreach (var format in Enum.GetValues<FormatId>())
        {
            if (format == FormatId.PlainText)
            {
                continue;
            }

            var label = MainWindow.DescribeFormat(new DetectionResult(format, DetectionConfidence.Certain));

            Assert.False(
                label == "Plain text",
                $"{format} has no label in DescribeFormat and would be shown as plain text.");
        }
    }

    [Fact]
    public void An_unrecognised_buffer_is_plain_text_whatever_the_format_says()
    {
        // Confidence None means nothing fired, and the Format field is then meaningless
        // rather than merely uncertain.
        Assert.Equal(
            "Plain text",
            MainWindow.DescribeFormat(new DetectionResult(FormatId.Json, DetectionConfidence.None)));
    }

    [Fact]
    public void A_sampled_result_says_so()
    {
        // A 10 MB document is identified from its first 64 KB. A chip reading plain "JSON"
        // would be claiming the whole file had been checked when it had not.
        var chip = MainWindow.DescribeFormat(
            new DetectionResult(FormatId.Json, DetectionConfidence.Likely, WasSampled: true));

        Assert.Equal("JSON (sampled)", chip);
    }
}
