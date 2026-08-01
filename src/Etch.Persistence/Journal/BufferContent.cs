namespace Etch.Persistence.Journal;

/// <summary>
/// A buffer's text, obtained when the journal is ready to write it rather than at
/// the moment the edit happened.
/// </summary>
/// <remarks>
/// <para>
/// This indirection exists for one reason, and it is a performance budget rather
/// than a matter of taste. The editor raises a change event on every keystroke, and
/// the obvious implementation hands the journal <c>document.Text</c> — which walks
/// the whole rope and allocates the entire buffer as a string, on the UI thread,
/// between the key going down and the frame going out. At a megabyte that is a
/// two-megabyte allocation straight onto the large object heap, per character
/// typed, against a 16 ms keystroke-to-frame budget.
/// </para>
/// <para>
/// A deferred content object lets the caller capture something O(1) instead — an
/// immutable snapshot of the document — and pay for materialising it once, on the
/// journal's own thread, after the debounce has already collapsed a burst of typing
/// into a single write. That is precisely the affordance the plan's threading model
/// is built around.
/// </para>
/// <para>
/// <see cref="ReadText"/> is called on the journal's background thread. An
/// implementation must therefore be safe to invoke from a thread that does not own
/// the editor, which is why the intended input is a snapshot and not the live
/// document.
/// </para>
/// </remarks>
public abstract class BufferContent
{
    private protected BufferContent()
    {
    }

    /// <summary>
    /// Produces the text to write.
    /// </summary>
    /// <remarks>
    /// Potentially expensive, and deliberately a method rather than a property so
    /// that it reads as expensive at every call site. The journal calls it exactly
    /// once per write attempt and keeps the result in a local.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The source produced no text.</exception>
    public abstract string ReadText();

    /// <summary>Content that is already a string.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static BufferContent FromText(string text) => new Literal(text);

    /// <summary>
    /// Content materialised on demand, from a snapshot the caller has already taken.
    /// </summary>
    /// <param name="materialise">
    /// Called on the journal's thread. Must be safe to call from a thread that does
    /// not own the editor, and must return the same text however often it is called —
    /// so it has to close over an immutable snapshot, never over the live document.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="materialise"/> is null.</exception>
    public static BufferContent FromSnapshot(Func<string?> materialise) => new Deferred(materialise);

    private sealed class Literal : BufferContent
    {
        private readonly string _text;

        public Literal(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            _text = text;
        }

        public override string ReadText() => _text;
    }

    private sealed class Deferred : BufferContent
    {
        private readonly Func<string?> _materialise;

        public Deferred(Func<string?> materialise)
        {
            ArgumentNullException.ThrowIfNull(materialise);
            _materialise = materialise;
        }

        /// <remarks>
        /// Deliberately not memoised. The journal materialises once and requeues the
        /// resulting string on failure, so a cache here would only serve to keep a
        /// second copy of the buffer alive for the lifetime of the pending write.
        /// </remarks>
        public override string ReadText() =>
            _materialise() ?? throw new InvalidOperationException("A buffer snapshot produced no text.");
    }
}
