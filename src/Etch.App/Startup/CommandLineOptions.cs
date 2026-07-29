namespace Etch.App.Startup;

/// <summary>The content shape of a generated performance fixture.</summary>
internal enum SampleShape
{
    /// <summary>NDJSON records — realistic log-like input with long lines.</summary>
    Json,

    /// <summary>Plain prose lines — the cheapest thing the editor can be asked to hold.</summary>
    Text,
}

/// <summary>
/// What this invocation of Etch is for.
/// </summary>
/// <remarks>
/// A closed hierarchy rather than a bag of optional properties, so that
/// contradictory combinations cannot be constructed at all. The previous shape —
/// nullable fields plus an <c>IsHeadless</c> predicate — allowed
/// <c>--help --gen-sample x</c> and <c>--size 100</c> with no <c>--gen-sample</c>
/// to be represented, and both were silently mishandled downstream.
/// </remarks>
internal abstract record LaunchMode
{
    private protected LaunchMode()
    {
    }

    /// <summary>Print usage and exit.</summary>
    internal sealed record ShowHelp : LaunchMode;

    /// <summary>Write a synthetic performance fixture and exit, showing no window.</summary>
    /// <param name="Path">Absolute destination path.</param>
    /// <param name="Mebibytes">Approximate target size.</param>
    /// <param name="Shape">The kind of content to generate.</param>
    /// <param name="Force">Whether an existing file at <paramref name="Path"/> may be replaced.</param>
    internal sealed record GenerateSample(string Path, int Mebibytes, SampleShape Shape, bool Force) : LaunchMode;

    /// <summary>Open the editor, optionally on a file.</summary>
    /// <param name="FileToOpen">A validated absolute path, or null for an empty buffer.</param>
    internal sealed record Edit(string? FileToOpen) : LaunchMode;
}

/// <summary>Parsed command line for Etch.</summary>
/// <param name="Mode">What this invocation should do.</param>
/// <remarks>
/// Deliberately carries no diagnostics flag. <c>Main</c> has to act on
/// <c>--diag</c> before parsing can happen at all, so
/// <see cref="Diagnostics.StartupTimeline.Enabled"/> is the single source of truth
/// for whether the timeline is recording.
/// </remarks>
internal sealed record CommandLineOptions(LaunchMode Mode)
{
    /// <summary>Usage text, printed for <c>--help</c> and for parse failures.</summary>
    public const string Usage = """
        Etch — a fast, editor-first developer scratchpad.

        Usage:
          Etch [file]                      Open a file.
          Etch --diag                      Print the startup timeline and continue.
          Etch --gen-sample <path>         Write a synthetic file, then exit.
                 [--size <MiB>]            Target size. Default 50, max 2048.
                 [--shape json|text]       Content shape. Default json.
                 [--force]                 Replace an existing file at that path.
          Etch --help                      Show this text.

        Diagnostics are also written to %LOCALAPPDATA%\Etch\diag.
        """;
}
