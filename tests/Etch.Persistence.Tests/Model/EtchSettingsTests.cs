using Etch.Persistence.Model;
using Xunit;

namespace Etch.Persistence.Tests.Model;

/// <summary>
/// <see cref="EtchSettings.Sanitised"/>, which is the only thing standing between a
/// hand-edited file and a policy object whose constructor throws.
/// </summary>
public class EtchSettingsTests
{
    [Fact]
    public void The_defaults_match_the_size_policy_they_configure()
    {
        // These two are written down in different assemblies and would drift silently:
        // DocumentSizePolicy.Default is what runs before the settings file is read, and
        // EtchSettings.Default is what a first launch writes. A user whose thresholds
        // changed the moment they opened the settings panel would be right to call that
        // a bug.
        var policy = Etch.Core.Documents.DocumentSizePolicy.Default;

        Assert.Equal(policy.ReducedThreshold, EtchSettings.Default.ReducedThresholdBytes);
        Assert.Equal(policy.PlainTextThreshold, EtchSettings.Default.PlainTextThresholdBytes);
        Assert.Equal(policy.HardCeiling, EtchSettings.Default.HardCeilingBytes);
    }

    [Fact]
    public void The_defaults_survive_sanitising_unchanged()
    {
        Assert.Equal(EtchSettings.Default, EtchSettings.Default.Sanitised());
    }

    [Fact]
    public void Zero_retention_is_kept_because_it_is_a_real_choice()
    {
        // The privacy affordance. Treating 0 as "unset" and replacing it with the default
        // would silently start keeping the text of every closed tab for a week from
        // somebody who asked for the opposite.
        var settings = EtchSettings.Default with { TrashRetentionDays = 0 };

        Assert.Equal(0, settings.Sanitised().TrashRetentionDays);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(EtchSettings.MaxRetentionDays + 1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void An_impossible_retention_falls_back_to_the_default(int days)
    {
        var settings = (EtchSettings.Default with { TrashRetentionDays = days }).Sanitised();

        Assert.Equal(EtchSettings.Default.TrashRetentionDays, settings.TrashRetentionDays);
    }

    [Fact]
    public void One_bad_field_does_not_cost_the_others()
    {
        // The whole reason this clamps rather than rejects. Somebody who typed a bad
        // number into one box must not lose the ligature setting they chose last week.
        var settings = (EtchSettings.Default with
        {
            TrashRetentionDays = -5,
            Ligatures = false,
        }).Sanitised();

        Assert.Equal(EtchSettings.Default.TrashRetentionDays, settings.TrashRetentionDays);
        Assert.False(settings.Ligatures);
    }

    [Theory]
    // Not ascending, in each of the three ways it can fail to be.
    [InlineData(10, 2, 100)]
    [InlineData(2, 100, 10)]
    [InlineData(100, 10, 2)]
    // Equal, which DocumentSizePolicy also rejects — its bounds are strict.
    [InlineData(2, 2, 100)]
    [InlineData(2, 10, 10)]
    public void Thresholds_that_do_not_ascend_are_replaced_as_a_set(long reduced, long plainText, long ceiling)
    {
        const long Mebibyte = 1024 * 1024;

        var settings = (EtchSettings.Default with
        {
            ReducedThresholdBytes = reduced * Mebibyte,
            PlainTextThresholdBytes = plainText * Mebibyte,
            HardCeilingBytes = ceiling * Mebibyte,
        }).Sanitised();

        // All three, not just the offending one. Repairing two of them to satisfy the
        // third would produce a policy nobody chose.
        Assert.Equal(EtchSettings.Default.ReducedThresholdBytes, settings.ReducedThresholdBytes);
        Assert.Equal(EtchSettings.Default.PlainTextThresholdBytes, settings.PlainTextThresholdBytes);
        Assert.Equal(EtchSettings.Default.HardCeilingBytes, settings.HardCeilingBytes);
    }

    [Fact]
    public void A_sanitised_set_of_thresholds_always_builds_a_policy()
    {
        // The contract this type exists to keep. DocumentSizePolicy's constructor throws
        // on a set that does not ascend, and it is constructed from these values on a
        // path with no user in front of it.
        foreach (var candidate in Hostile())
        {
            var settings = candidate.Sanitised();

            var policy = new Etch.Core.Documents.DocumentSizePolicy(
                settings.ReducedThresholdBytes,
                settings.PlainTextThresholdBytes,
                settings.HardCeilingBytes);

            Assert.True(policy.ReducedThreshold < policy.PlainTextThreshold);
            Assert.True(policy.PlainTextThreshold < policy.HardCeiling);
        }
    }

    [Fact]
    public void A_custom_but_legal_set_of_thresholds_is_kept()
    {
        // The other half of the contract, and the one that a too-eager sanitiser would
        // break: a user who wants folding off above 512 KB gets that, not the default.
        var settings = (EtchSettings.Default with
        {
            ReducedThresholdBytes = 512 * 1024,
            PlainTextThresholdBytes = 4L * 1024 * 1024,
            HardCeilingBytes = 64L * 1024 * 1024,
        }).Sanitised();

        Assert.Equal(512 * 1024, settings.ReducedThresholdBytes);
        Assert.Equal(4L * 1024 * 1024, settings.PlainTextThresholdBytes);
        Assert.Equal(64L * 1024 * 1024, settings.HardCeilingBytes);
    }

    [Fact]
    public void Sanitising_stamps_the_current_version()
    {
        // A file from an older schema is read, upgraded in memory and written back at the
        // current version. Leaving the old number would make every subsequent load look
        // like a downgrade.
        var settings = (EtchSettings.Default with { Version = 0 }).Sanitised();

        Assert.Equal(EtchSettings.CurrentVersion, settings.Version);
    }

    [Fact]
    public void A_newer_version_is_recognised_as_one()
    {
        Assert.True((EtchSettings.Default with { Version = EtchSettings.CurrentVersion + 1 }).IsFromFutureVersion);
        Assert.False(EtchSettings.Default.IsFromFutureVersion);
    }

    /// <summary>Settings a hand-edited or hostile file could plausibly produce.</summary>
    private static IEnumerable<EtchSettings> Hostile()
    {
        yield return EtchSettings.Default with { ReducedThresholdBytes = 0 };
        yield return EtchSettings.Default with { PlainTextThresholdBytes = 0 };
        yield return EtchSettings.Default with { HardCeilingBytes = 0 };
        yield return EtchSettings.Default with { ReducedThresholdBytes = -1 };
        yield return EtchSettings.Default with { HardCeilingBytes = long.MaxValue };
        yield return EtchSettings.Default with { ReducedThresholdBytes = long.MaxValue };

        yield return EtchSettings.Default with
        {
            ReducedThresholdBytes = long.MinValue,
            PlainTextThresholdBytes = long.MinValue,
            HardCeilingBytes = long.MinValue,
        };

        // Below the floor but otherwise well-formed: 1 KB, 2 KB, 3 KB ascends perfectly
        // and would still make Etch appear broken on an ordinary source file.
        yield return EtchSettings.Default with
        {
            ReducedThresholdBytes = 1024,
            PlainTextThresholdBytes = 2048,
            HardCeilingBytes = 3072,
        };

        // Above the ceiling but ascending, which is the mirror of the case above.
        yield return EtchSettings.Default with
        {
            ReducedThresholdBytes = EtchSettings.MaxThresholdBytes,
            PlainTextThresholdBytes = EtchSettings.MaxThresholdBytes + 1,
            HardCeilingBytes = EtchSettings.MaxThresholdBytes + 2,
        };
    }
}
