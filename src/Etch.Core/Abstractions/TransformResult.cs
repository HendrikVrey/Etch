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
    public static TransformResult Failed(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        return new TransformResult(Success: false, Text: null, error, ResultingFormat: null);
    }
}
