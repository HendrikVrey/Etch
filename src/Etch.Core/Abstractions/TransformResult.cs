namespace Etch.Core.Abstractions;

/// <summary>
/// What a transform produced, or why it could not.
/// </summary>
/// <param name="Success">Whether the transform ran.</param>
/// <param name="Text">
/// The replacement text, or null to leave the buffer alone. Always null when
/// <paramref name="Success"/> is false; null on success means the transform had something
/// to say rather than something to write — see <see cref="TransformResult.Reported"/>.
/// </param>
/// <param name="Error">
/// An actionable message for the status bar. Null on success. "Invalid input" is not
/// actionable; "Unexpected '}' at line 12" is.
/// </param>
/// <param name="ResultingFormat">
/// What the output is, when the transform knows. Lets the palette re-rank for the next
/// step without waiting for detection to run again — which is what makes chaining feel
/// immediate rather than merely possible.
/// </param>
/// <remarks>
/// A failed transform carries no text, so there is no way to accidentally write the
/// error into the buffer. That is the point of pairing them in one type rather than
/// returning a nullable string.
/// </remarks>
public readonly record struct TransformResult(
    bool Success,
    string? Text,
    string? Error,
    FormatId? ResultingFormat,
    string? Message = null)
{
    /// <summary>
    /// Where in the input the transform gave up, as a character offset, or null when it
    /// cannot say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A number rather than more prose. The message already names a line and column for
    /// the parsers that report one, and a line number in a status bar still leaves the
    /// user to go and find it — on a minified document, where the whole payload is line
    /// one, it leaves them nothing at all. An offset is what lets the editor put the
    /// caret on the problem.
    /// </para>
    /// <para>
    /// <b>Relative to the text the transform was given</b>, which is the selection when
    /// there is one, not the document. The caller applied the selection and is the only
    /// one that can undo it.
    /// </para>
    /// <para>
    /// Null is the honest answer and the common one. Most failures are about the input as
    /// a whole — "that is not valid base64" — and inventing an offset of zero for them
    /// would move the caret to the top of the document for no reason.
    /// </para>
    /// <para>
    /// Deliberately not a positional parameter. The three factories below are the whole
    /// sanctioned way to build one of these, and <see cref="Failed"/> is the only one
    /// that may set this — keeping it out of the constructor is what stops
    /// <see cref="Ok"/> from ever growing an offset that means nothing.
    /// </para>
    /// </remarks>
    public int? ErrorOffset { get; init; }

    /// <summary>A successful transform.</summary>
    /// <param name="text">The replacement text.</param>
    /// <param name="resultingFormat">What the output is, when known.</param>
    /// <param name="message">
    /// What to say instead of the default "<c>{name}</c> applied". Null for almost
    /// everything: the buffer visibly changed, and narrating it is noise.
    /// </param>
    public static TransformResult Ok(string text, FormatId? resultingFormat = null, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new TransformResult(Success: true, text, Error: null, resultingFormat, message);
    }

    /// <summary>
    /// A transform that ran, has something to say, and must not touch the buffer.
    /// </summary>
    /// <param name="message">The finding, e.g. "Valid JSON — 42 keys".</param>
    /// <remarks>
    /// "Validate" is the case this exists for. Returning <see cref="Ok"/> with the input
    /// unchanged would be the obvious alternative and is wrong twice over: the editor does
    /// not diff before replacing, so a document that was already valid would acquire an
    /// undo step and lose its caret position for nothing. A transform is allowed to answer
    /// a question rather than perform an edit.
    /// </remarks>
    public static TransformResult Reported(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return new TransformResult(Success: true, Text: null, Error: null, ResultingFormat: null, message);
    }

    /// <summary>A transform that could not run, with a message worth showing.</summary>
    /// <param name="error">What went wrong, in terms the user can act on.</param>
    /// <param name="errorOffset">
    /// Where in the input it went wrong, when the transform knows. See
    /// <see cref="ErrorOffset"/> — a negative value is treated as "cannot say" rather
    /// than trusted, because it can only have come from arithmetic that went wrong, and
    /// handing it on would throw in the editor rather than here, a layer away from the
    /// mistake.
    /// </param>
    public static TransformResult Failed(string error, int? errorOffset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        return new TransformResult(Success: false, Text: null, error, ResultingFormat: null)
        {
            ErrorOffset = errorOffset is < 0 ? null : errorOffset,
        };
    }
}
