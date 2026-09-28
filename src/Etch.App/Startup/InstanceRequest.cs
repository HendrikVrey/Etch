using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Etch.App.Startup;

/// <summary>
/// What a second launch of Etch is asking the running instance to do.
/// </summary>
/// <remarks>
/// The wire format is deliberately tiny and completely explicit, because the reader
/// runs inside the process that owns the user's text and the writer is another
/// process. It is a single line:
/// <code>
/// ETCH1 ACTIVATE
/// ETCH1 OPEN &lt;absolute path&gt;
/// </code>
/// The version token exists so that a future format change is a clean rejection
/// rather than a misparse. The path is the remainder of the line and may contain
/// spaces, so there is nothing to escape and no quoting rules to get wrong.
/// </remarks>
internal abstract record InstanceRequest
{
    private const string Protocol = "ETCH1";
    private const string ActivateVerb = "ACTIVATE";
    private const string OpenVerb = "OPEN";

    /// <summary>The largest message that will be read or written, in bytes.</summary>
    /// <remarks>
    /// A Windows path tops out far below this even in its long-path form. The cap is
    /// what stops a hostile or broken client from making the running instance
    /// allocate.
    /// </remarks>
    public const int MaxMessageBytes = 8 * 1024;

    private protected InstanceRequest()
    {
    }

    /// <summary>Bring the running window to the front. No file was named.</summary>
    internal sealed record Activate : InstanceRequest;

    /// <summary>Bring the running window to the front and open a file in it.</summary>
    /// <param name="Path">An absolute path, already validated by <see cref="PathGuard"/>.</param>
    internal sealed record Open(string Path) : InstanceRequest;

    /// <summary>Serialises this request to the bytes sent over the pipe.</summary>
    /// <exception cref="InvalidOperationException">
    /// The path is too long to send. Unreachable for a path that came from
    /// <see cref="PathGuard"/>, but asserted rather than assumed, because silently
    /// sending a truncated path would open the wrong file.
    /// </exception>
    public byte[] ToBytes()
    {
        var line = this switch
        {
            Open open => $"{Protocol} {OpenVerb} {open.Path}",
            Activate => $"{Protocol} {ActivateVerb}",
            _ => throw new InvalidOperationException($"Unhandled request: {GetType().Name}"),
        };

        var bytes = Encoding.UTF8.GetBytes(line);

        return bytes.Length <= MaxMessageBytes
            ? bytes
            : throw new InvalidOperationException("The instance request is too large to send.");
    }

    /// <summary>
    /// Parses a message received over the pipe.
    /// </summary>
    /// <remarks>
    /// Every failure returns false. This is untrusted input from another process, and
    /// the only correct response to a message that is not exactly right is to ignore
    /// it: the running instance has the user's unsaved text in it and must not be
    /// taken down by a malformed byte sequence.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<byte> message, [NotNullWhen(true)] out InstanceRequest? request)
    {
        request = null;

        if (message.Length is 0 or > MaxMessageBytes)
        {
            return false;
        }

        string line;

        try
        {
            // Throwing rather than substituting U+FFFD: a path that did not survive
            // the round trip is a path that names a different file, and opening the
            // wrong file is worse than ignoring the request.
            line = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(message);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (!line.StartsWith(Protocol + " ", StringComparison.Ordinal))
        {
            return false;
        }

        var body = line[(Protocol.Length + 1)..];

        if (body.Equals(ActivateVerb, StringComparison.Ordinal))
        {
            request = new Activate();
            return true;
        }

        if (!body.StartsWith(OpenVerb + " ", StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = body[(OpenVerb.Length + 1)..];

        // Validated here rather than trusted, with the same rules the command line gets.
        // The sender is another process running as this user, so this is not a privilege
        // boundary, but it is the difference between a bug in the sender producing an
        // ignored message and it producing a device-path open.
        if (!PathGuard.TryResolve(candidate, out var path, out _))
        {
            return false;
        }

        // And it must already have been absolute. Resolution happens against the working
        // directory, and the two processes do not share one: a relative path that
        // "worked" here would silently open a different file from the one the other
        // process meant. Every legitimate sender has already resolved it.
        if (!string.Equals(path, candidate, StringComparison.Ordinal))
        {
            return false;
        }

        request = new Open(path);
        return true;
    }
}
