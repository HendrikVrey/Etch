namespace Etch.Core.Abstractions;

/// <summary>
/// One thing Etch can do to a piece of text, in place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Synchronous, deliberately.</b> The plan's draft contract was asynchronous, and
/// the plan is right that expensive transforms must not run on the UI thread, but
/// that is a fact about <em>where the caller invokes this</em>, not about what a
/// transform is. A transform is a pure function from text to text. Making the
/// signature asynchronous would invite an implementation to await something, and the
/// first one that awaits I/O breaks the guarantee that <c>Etch.Core</c> touches no
/// disk and no network. The application layer runs these on a background thread and
/// applies the result back as a single undo group; a long-running implementation
/// honours <paramref name="cancellationToken"/> inside its own loops.
/// </para>
/// <para>
/// Implementations must not mutate shared state. The registry hands the same instance
/// to every caller, and detection, ranking and application can all be in flight at
/// once.
/// </para>
/// </remarks>
public interface ITransform
{
    /// <summary>
    /// Stable identity, e.g. <c>json.format</c>.
    /// </summary>
    /// <remarks>
    /// Never localised and never renamed once shipped: it is what the palette's
    /// recency list and any future keybinding configuration store.
    /// </remarks>
    string Id { get; }

    /// <summary>The name shown in the palette, e.g. "Format JSON".</summary>
    string Name { get; }

    /// <summary>The grouping this appears under.</summary>
    TransformCategory Category { get; }

    /// <summary>
    /// Extra words the palette should match against.
    /// </summary>
    /// <remarks>
    /// Where someone's vocabulary goes when it differs from the name: "prettify",
    /// "beautify" and "indent" all have to find "Format JSON", or the palette only
    /// works for people who already know what the command is called.
    /// </remarks>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>
    /// Whether this transform has anything to do with an empty buffer.
    /// </summary>
    /// <remarks>
    /// True for everything that reads its input, which is everything but the generators.
    /// The application refuses to run a transform over an empty tab, an "applied" with
    /// nothing to apply it to is a confusing thing to be told, and "New GUID" is precisely
    /// the case where an empty tab is the <em>normal</em> starting point.
    /// </remarks>
    bool NeedsInput => true;

    /// <summary>
    /// Which of several applicable transforms is the obvious one. Lower wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only consulted between transforms that scored identically, in practice, between
    /// ones that all apply to the detected format and have never been used. That is exactly
    /// the case <c>Ctrl+Enter</c> decides on a fresh session, so leaving it to the final
    /// tie-break meant the most important key in the product was answered by <b>alphabetical
    /// order</b>: "Format JSON" beat "Minify JSON" because F precedes M. It happened to be
    /// right, and a transform named "Compact JSON" added later would silently have taken it
    /// over.
    /// </para>
    /// <para>
    /// Zero is the answer for almost everything. A number here is only worth setting when a
    /// transform is deliberately claiming, or deliberately conceding, the suggested slot for
    /// a format, and <c>SuggestionTests</c> pins what each format resolves to so that a
    /// change to any of these numbers has to be an intended one.
    /// </para>
    /// </remarks>
    int Precedence => 0;

    /// <summary>
    /// Whether this transform is worth offering for <paramref name="detection"/>.
    /// </summary>
    /// <remarks>
    /// Called for every registered transform each time the palette opens and each time
    /// detection re-runs, so it must be a comparison and not a computation. It decides
    /// ranking, not permission: a transform that returns false is still reachable by
    /// searching for it, because detection is a guess and the user is not.
    /// </remarks>
    bool IsAvailable(in DetectionResult detection);

    /// <summary>
    /// Applies the transform.
    /// </summary>
    /// <param name="input">The text and the editor settings to match.</param>
    /// <param name="cancellationToken">
    /// Cancels a long transform. The document has moved on; the result is discarded.
    /// </param>
    /// <returns>
    /// The replacement text, or a failure carrying a message. Implementations report
    /// bad input by returning <see cref="TransformResult.Failed"/> rather than by
    /// throwing: every buffer is untrusted input, so malformed text is the expected
    /// case rather than an exceptional one.
    /// </returns>
    TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default);
}
