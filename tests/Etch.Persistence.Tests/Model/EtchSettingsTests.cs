using Etch.Persistence.Model;
using Xunit;

namespace Etch.Persistence.Tests.Model;

/// <summary>
/// <see cref="EtchSettings.Sanitised"/>, which is the only thing standing between a
/// hand-edited file and a retention window that cannot be represented.
/// </summary>
public class EtchSettingsTests
{
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

}
