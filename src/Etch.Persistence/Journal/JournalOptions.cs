namespace Etch.Persistence.Journal;

/// <summary>
/// How aggressively edits are written to disk.
/// </summary>
/// <remarks>
/// Two intervals rather than one, because a single debounce has a failure mode that
/// matters: someone typing steadily never stops long enough to trigger it, so the
/// longer they work the more they stand to lose. <see cref="MaxLatency"/> is the
/// ceiling on that exposure, and <see cref="DebounceInterval"/> keeps the common
/// case (type, pause, think) cheap.
/// </remarks>
public sealed class JournalOptions
{
    /// <summary>The shipped defaults: half a second of quiet, five seconds at most.</summary>
    public static JournalOptions Default { get; } = new(
        debounceInterval: TimeSpan.FromMilliseconds(500),
        maxLatency: TimeSpan.FromSeconds(5));

    /// <summary>Creates options.</summary>
    /// <param name="debounceInterval">Quiet period after the last edit before writing.</param>
    /// <param name="maxLatency">Longest an edit may go unwritten while typing continues.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An interval is not positive, or <paramref name="maxLatency"/> is shorter than
    /// <paramref name="debounceInterval"/>, which would make the debounce
    /// unreachable and is therefore a configuration mistake worth refusing.
    /// </exception>
    public JournalOptions(TimeSpan debounceInterval, TimeSpan maxLatency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(debounceInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLatency, debounceInterval);

        DebounceInterval = debounceInterval;
        MaxLatency = maxLatency;
    }

    /// <summary>How long the text must be untouched before it is written.</summary>
    public TimeSpan DebounceInterval { get; }

    /// <summary>The longest an unwritten edit may live, however continuously the user types.</summary>
    public TimeSpan MaxLatency { get; }
}
