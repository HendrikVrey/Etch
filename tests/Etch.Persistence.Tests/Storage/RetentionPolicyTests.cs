using Etch.Persistence.Storage;
using Xunit;

namespace Etch.Persistence.Tests.Storage;

public class RetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_default_window_is_seven_days()
    {
        Assert.Equal(TimeSpan.FromDays(7), RetentionPolicy.Default.Retention);
        Assert.False(RetentionPolicy.Default.DeletesOnClose);
    }

    [Fact]
    public void A_buffer_trashed_moments_ago_has_not_expired()
    {
        Assert.False(RetentionPolicy.Default.IsExpired(Now.AddMinutes(-1), Now));
    }

    [Fact]
    public void A_buffer_expires_exactly_at_the_boundary()
    {
        var policy = RetentionPolicy.Default;

        Assert.False(policy.IsExpired(Now.AddDays(-7).AddSeconds(1), Now));
        Assert.True(policy.IsExpired(Now.AddDays(-7), Now));
        Assert.True(policy.IsExpired(Now.AddDays(-8), Now));
    }

    [Fact]
    public void Zero_retention_expires_immediately()
    {
        Assert.True(RetentionPolicy.DeleteImmediately.IsExpired(Now, Now));
        Assert.True(RetentionPolicy.DeleteImmediately.DeletesOnClose);
    }

    [Fact]
    public void A_timestamp_in_the_future_is_never_expired()
    {
        // Clock corrections, daylight-saving jumps and files copied from another
        // machine all produce these. Erring the other way would delete tabs somebody
        // closed a minute ago because the clock moved.
        Assert.False(RetentionPolicy.Default.IsExpired(Now.AddDays(1), Now));
        Assert.False(RetentionPolicy.DeleteImmediately.IsExpired(Now.AddSeconds(1), Now));
    }

    [Fact]
    public void Expiry_time_is_the_trash_time_plus_the_window()
    {
        Assert.Equal(Now.AddDays(7), RetentionPolicy.Default.ExpiresAt(Now));
    }

    [Fact]
    public void A_negative_window_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionPolicy(TimeSpan.FromDays(-1)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(365)]
    public void Custom_windows_are_honoured(int days)
    {
        var policy = new RetentionPolicy(TimeSpan.FromDays(days));

        Assert.True(policy.IsExpired(Now.AddDays(-days - 1), Now));
        Assert.False(policy.IsExpired(Now.AddDays(-days + 1), Now));
    }
}
