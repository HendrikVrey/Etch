namespace Etch.Core.Abstractions;

/// <summary>
/// Editor settings a transform needs in order to produce output that fits the buffer
/// it is going back into.
/// </summary>
/// <param name="IndentSize">Spaces per indent level for formatters.</param>
/// <param name="NewLine">
/// The line ending to emit. Taken from the document rather than assumed, because a
/// transform that writes <c>\n</c> into a CRLF buffer leaves a file with both — which
/// is invisible on screen and very visible in a diff.
/// </param>
/// <remarks>
/// A record rather than a bag of parameters so that adding a setting later does not
/// change every transform signature in the registry.
/// </remarks>
public sealed record TransformOptions(int IndentSize = 2, string NewLine = "\n")
{
    /// <summary>Two-space indent, LF endings.</summary>
    public static TransformOptions Default { get; } = new();

    private readonly int _indentSize = Math.Clamp(IndentSize, 1, 16);
    private readonly string _newLine = Sanitise(NewLine);

    /// <summary>Spaces per indent level. Clamped to something a formatter can use.</summary>
    public int IndentSize
    {
        get => _indentSize;
        init => _indentSize = Math.Clamp(value, 1, 16);
    }

    /// <summary>The line ending to emit.</summary>
    /// <remarks>
    /// Only the three endings a text editor can produce are accepted; anything else
    /// falls back to <c>\n</c>. This value is concatenated into output, so leaving it
    /// open would let an arbitrary string be injected between every line.
    /// </remarks>
    public string NewLine
    {
        get => _newLine;
        init => _newLine = Sanitise(value);
    }

    /// <summary>
    /// Both rules live in the accessors rather than only in the constructor.
    /// </summary>
    /// <remarks>
    /// This is a record, so <c>with</c> assigns through <c>init</c> and bypasses anything
    /// enforced only on construction. A guard that a single <c>with</c> expression can
    /// step around is not a guard.
    /// </remarks>
    private static string Sanitise(string? newLine) =>
        newLine is "\n" or "\r\n" or "\r" ? newLine : "\n";
}
