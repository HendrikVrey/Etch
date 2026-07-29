namespace Etch.Core.Text;

/// <summary>The dominant newline convention in a buffer.</summary>
public enum LineEndingStyle
{
    /// <summary>No line break was found in the sample.</summary>
    None,

    /// <summary>Windows: carriage return + line feed.</summary>
    Crlf,

    /// <summary>Unix: line feed only.</summary>
    Lf,

    /// <summary>Classic Mac: carriage return only.</summary>
    Cr,
}
