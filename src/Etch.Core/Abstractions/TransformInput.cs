namespace Etch.Core.Abstractions;

/// <summary>
/// The text a transform is being asked to operate on.
/// </summary>
/// <param name="Text">
/// The selection if there was one, otherwise the whole buffer. Transforms never see
/// the difference and never need to: the caller decides what "the text" is, and the
/// caller puts the result back where it came from.
/// </param>
/// <param name="WasSelection">
/// True when <paramref name="Text"/> is a selection. Not a routing flag — it exists so
/// that a transform which only makes sense over a whole document can say so, and so
/// that error messages can name the right thing.
/// </param>
/// <param name="Options">Editor settings the output has to match.</param>
public readonly record struct TransformInput(string Text, bool WasSelection, TransformOptions Options)
{
    /// <summary>Wraps whole-buffer text with default options.</summary>
    public static TransformInput Whole(string text) =>
        new(text, WasSelection: false, TransformOptions.Default);
}
