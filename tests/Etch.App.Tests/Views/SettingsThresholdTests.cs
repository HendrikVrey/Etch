using Etch.App.Views;
using Etch.Persistence.Model;
using Xunit;

namespace Etch.App.Tests.Views;

/// <summary>
/// <see cref="SettingsThresholds"/> — where three strings a user is halfway through
/// typing become the arguments to <c>DocumentSizePolicy</c>'s constructor, which throws
/// on a set that does not ascend.
/// </summary>
/// <remarks>
/// Every decimal literal here is built through <see cref="SettingsThresholds.Format"/>
/// rather than written out, because the panel parses with the current culture — a German
/// user types "0,0625" and expects it to work — and because a formatter and a parser that
/// disagree is exactly the defect <see cref="The_panel_can_round_trip_its_own_floor"/>
/// exists to catch. Whole numbers need no such care.
/// </remarks>
public class SettingsThresholdTests
{
    private const string TwoMegabytes = "2";
    private const string TenMegabytes = "10";
    private const string HundredMegabytes = "100";

    [Fact]
    public void The_shipped_defaults_parse_back_to_themselves()
    {
        Assert.True(SettingsThresholds.TryParse(
            TwoMegabytes,
            TenMegabytes,
            HundredMegabytes,
            out var reduced,
            out var plainText,
            out var ceiling));

        Assert.Equal(EtchSettings.Default.ReducedThresholdBytes, reduced);
        Assert.Equal(EtchSettings.Default.PlainTextThresholdBytes, plainText);
        Assert.Equal(EtchSettings.Default.HardCeilingBytes, ceiling);
    }

    /// <summary>Sizes the panel might have to display, including the awkward one.</summary>
    /// <remarks>
    /// 100 KiB is here on purpose: it is 0.09765625 MB, so four decimal places cannot
    /// express it. It is the case that proved <see cref="SettingsThresholds.Format"/> and
    /// <see cref="SettingsThresholds.TryParse"/> are not exact inverses, and every size
    /// below sits comfortably under half the ceiling so it can take the "reduced" slot.
    /// </remarks>
    public static TheoryData<long> Sizes => new()
    {
        EtchSettings.MinThresholdBytes,
        100L * 1024,
        EtchSettings.Default.ReducedThresholdBytes,
        EtchSettings.Default.PlainTextThresholdBytes,
        EtchSettings.Default.HardCeilingBytes,
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Anything_the_panel_displays_the_panel_accepts(long size)
    {
        // The bug this replaced a stricter version of: Format used to round to two
        // decimals, so the smallest legal threshold — 64 KiB, or 0.0625 MB exactly — was
        // displayed as "0.06" and parsed back as 62,914 bytes, below the floor. The panel
        // then rejected the value it had written into the box one field earlier.
        //
        // Acceptance, not equality, and the weakening is deliberate. 100 KiB is
        // 0.09765625 MB and cannot survive four decimal places; asserting the byte count
        // came back unchanged would be asserting something the panel does not promise and
        // could only deliver by showing twenty decimals in a text box.
        var text = SettingsThresholds.Format(size);

        Assert.True(
            SettingsThresholds.TryParse(
                text,
                SettingsThresholds.Format(EtchSettings.MaxThresholdBytes / 2),
                SettingsThresholds.Format(EtchSettings.MaxThresholdBytes),
                out var reduced,
                out _,
                out _),
            $"{text} MB was formatted by the panel and then refused by it.");

        // Still legal afterwards, even where it is not identical — which is the part that
        // would actually hurt, because an illegal value reaches a constructor that throws.
        Assert.InRange(reduced, EtchSettings.MinThresholdBytes, EtchSettings.MaxThresholdBytes);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Formatting_is_idempotent_so_a_threshold_cannot_drift(long size)
    {
        // The property that replaces exact round-tripping, and the one that actually
        // protects the user. A value may lose precision once, the first time it passes
        // through the box — but it must not keep moving every time the panel is reopened,
        // or a threshold would walk a little further away on each visit.
        var once = SettingsThresholds.Format(size);

        Assert.True(SettingsThresholds.TryParse(
            once,
            SettingsThresholds.Format(EtchSettings.MaxThresholdBytes / 2),
            SettingsThresholds.Format(EtchSettings.MaxThresholdBytes),
            out var parsed,
            out _,
            out _));

        Assert.Equal(once, SettingsThresholds.Format(parsed));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void An_untouched_box_is_recognised_as_untouched(long size)
    {
        // What stops the drift the two tests above tolerate. The panel re-reads the size
        // boxes only when one has actually been typed in — otherwise editing the retention
        // field beside them would silently rewrite 102,400 bytes as 102,445.
        Assert.True(SettingsThresholds.IsUnchanged(SettingsThresholds.Format(size), size));
    }

    [Fact]
    public void An_edited_box_is_recognised_as_edited()
    {
        // The other direction, so that "unchanged" cannot be satisfied by a method that
        // always says yes — which would freeze the size boxes completely while looking
        // like nothing was wrong at all.
        var size = EtchSettings.Default.ReducedThresholdBytes;
        var text = SettingsThresholds.Format(size);

        Assert.False(SettingsThresholds.IsUnchanged(text + "1", size));
        Assert.False(SettingsThresholds.IsUnchanged(string.Empty, size));
        Assert.False(SettingsThresholds.IsUnchanged(" " + text, size));
    }

    [Theory]
    [InlineData("10", "2", "100")]
    [InlineData("2", "100", "10")]
    [InlineData("2", "2", "100")]
    [InlineData("2", "10", "10")]
    public void A_set_that_does_not_ascend_is_refused_whole(string reduced, string plainText, string ceiling)
    {
        // All or nothing. Accepting one field at a time would mean every intermediate
        // keystroke produced a policy whose constructor throws.
        Assert.False(SettingsThresholds.TryParse(reduced, plainText, ceiling, out _, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("2mb")]
    [InlineData("1e9")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("∞")]
    [InlineData("-")]
    public void Text_that_is_not_a_number_is_refused(string reduced)
    {
        // "1e9" is in the list on purpose: exponent notation parses happily under
        // NumberStyles.Float and is almost certainly a typo in a box labelled MB.
        Assert.False(SettingsThresholds.TryParse(reduced, TenMegabytes, HundredMegabytes, out _, out _, out _));
    }

    [Fact]
    public void A_negative_size_is_refused()
    {
        // The sign is allowed through the parse deliberately, so that this reads as out
        // of range rather than as unparseable — but it must still be refused, because a
        // negative reduced threshold ascends perfectly well towards a positive one.
        Assert.False(SettingsThresholds.TryParse("-2", TenMegabytes, HundredMegabytes, out _, out _, out _));
    }

    [Fact]
    public void A_size_beyond_the_ceiling_is_refused_without_overflowing()
    {
        // The trap this guards. A double past long's range converts, in an unchecked
        // context, to an unspecified value — long.MinValue in practice — which would then
        // sail through the ascending check as a plausible-looking negative threshold.
        //
        // Equal to zero rather than merely non-negative: TryParse zeroes its outputs on
        // entry, so "not negative" would pass with the overflow guard deleted.
        Assert.False(SettingsThresholds.TryParse(
            TwoMegabytes,
            TenMegabytes,
            "99999999999999999999",
            out _,
            out _,
            out var ceiling));

        Assert.Equal(0, ceiling);
    }

    [Fact]
    public void A_size_below_the_floor_is_refused()
    {
        // 1 KB ascends towards 10 MB perfectly well and would still make Etch turn
        // folding off for an ordinary source file, which reads as broken rather than
        // careful.
        Assert.False(SettingsThresholds.TryParse(
            SettingsThresholds.Format(1024),
            TenMegabytes,
            HundredMegabytes,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void The_floor_and_the_ceiling_are_themselves_accepted()
    {
        // Boundaries are inclusive on both ends. A range whose stated limits are rejected
        // is a range whose message is a lie — and the panel's message names both of these
        // numbers.
        Assert.True(SettingsThresholds.TryParse(
            SettingsThresholds.Format(EtchSettings.MinThresholdBytes),
            TenMegabytes,
            SettingsThresholds.Format(EtchSettings.MaxThresholdBytes),
            out var reduced,
            out _,
            out var ceiling));

        Assert.Equal(EtchSettings.MinThresholdBytes, reduced);
        Assert.Equal(EtchSettings.MaxThresholdBytes, ceiling);
    }

    [Fact]
    public void One_byte_past_the_ceiling_is_refused()
    {
        // The other side of the boundary, so that "inclusive" is asserted rather than
        // assumed. 4096 MB is legal; 4097 is not.
        Assert.False(SettingsThresholds.TryParse(
            TwoMegabytes,
            TenMegabytes,
            SettingsThresholds.Format(EtchSettings.MaxThresholdBytes) + "1",
            out _,
            out _,
            out _));
    }

    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        // Pasted values arrive with it, and refusing them would look like a bug in the
        // paste rather than a rule about the field.
        Assert.True(SettingsThresholds.TryParse(" 2 ", " 10 ", " 100 ", out var reduced, out _, out _));

        Assert.Equal(EtchSettings.Default.ReducedThresholdBytes, reduced);
    }

    [Fact]
    public void A_parsed_set_always_builds_a_policy()
    {
        // The contract the panel depends on: anything TryParse accepts is handed to
        // DocumentSizePolicy's constructor, which throws rather than clamping.
        Assert.True(SettingsThresholds.TryParse(
            SettingsThresholds.Format(EtchSettings.MinThresholdBytes),
            TenMegabytes,
            SettingsThresholds.Format(EtchSettings.MaxThresholdBytes),
            out var reduced,
            out var plainText,
            out var ceiling));

        var policy = new Etch.Core.Documents.DocumentSizePolicy(reduced, plainText, ceiling);

        Assert.Equal(reduced, policy.ReducedThreshold);
        Assert.Equal(ceiling, policy.HardCeiling);
    }
}
