namespace Etch.Persistence.Storage;

/// <summary>
/// Decides how long a closed buffer is kept before it is really deleted.
/// </summary>
/// <remarks>
/// <para>
/// Pure and clock-free: it is handed the current time rather than reading one, so
/// every boundary can be tested without waiting for it. The I/O that acts on these
/// decisions lives in <see cref="BufferStore"/>.
/// </para>
/// <para>
/// This policy is the reason Etch can close a tab without asking. Close is a move
/// to the trash, not a delete, so there is nothing to confirm — and that only stays
/// true for as long as the retention window is honest about when deletion happens.
/// </para>
/// </remarks>
public sealed class RetentionPolicy
{
    /// <summary>Retention used when the user has not chosen one.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    /// <summary>The shipped default: seven days.</summary>
    public static RetentionPolicy Default { get; } = new(DefaultRetention);

    /// <summary>
    /// Deletes closed buffers immediately, for users who would rather nothing
    /// lingered on disk. Documented in the README as a privacy affordance, since
    /// people do paste secrets into a scratchpad.
    /// </summary>
    public static RetentionPolicy DeleteImmediately { get; } = new(TimeSpan.Zero);

    /// <summary>Creates a policy.</summary>
    /// <param name="retention">
    /// How long a closed buffer is kept. <see cref="TimeSpan.Zero"/> deletes on close.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retention"/> is negative.</exception>
    public RetentionPolicy(TimeSpan retention)
    {
        if (retention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                retention,
                "Retention cannot be negative - a buffer cannot expire before it is closed.");
        }

        Retention = retention;
    }

    /// <summary>How long a closed buffer is kept.</summary>
    public TimeSpan Retention { get; }

    /// <summary>True when closing a tab deletes it outright rather than trashing it.</summary>
    public bool DeletesOnClose => Retention == TimeSpan.Zero;

    /// <summary>
    /// Whether a buffer trashed at <paramref name="trashedAtUtc"/> has expired by
    /// <paramref name="nowUtc"/>.
    /// </summary>
    /// <remarks>
    /// A timestamp in the future — a clock correction, a daylight-saving jump, a
    /// file copied from another machine — is treated as not expired. Erring the
    /// other way would delete recently closed tabs because the clock moved.
    /// </remarks>
    public bool IsExpired(DateTimeOffset trashedAtUtc, DateTimeOffset nowUtc)
    {
        if (trashedAtUtc > nowUtc)
        {
            return false;
        }

        return nowUtc - trashedAtUtc >= Retention;
    }

    /// <summary>When a buffer trashed at <paramref name="trashedAtUtc"/> becomes eligible for deletion.</summary>
    public DateTimeOffset ExpiresAt(DateTimeOffset trashedAtUtc) => trashedAtUtc + Retention;
}
