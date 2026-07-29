using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Etch.App.Startup;

/// <summary>
/// Parses Etch's command line.
/// </summary>
/// <remarks>
/// Hand-rolled on purpose. A parsing library would be a startup cost (assembly
/// load plus reflection) for a surface of a handful of flags, and startup cost is
/// the one thing this project refuses to spend carelessly.
/// <para>
/// Every path that arrives here is untrusted input. Paths are resolved to absolute
/// form and validated before they reach the file system, and nothing on this path
/// ever reaches a shell. Nothing here touches the file system either: parsing runs
/// before the window exists, and a single <c>File.Exists</c> on a UNC path would
/// block startup for the SMB timeout.
/// </para>
/// </remarks>
internal static class CommandLineParser
{
    /// <summary>
    /// The diagnostics flag, shared with <c>Program.Main</c>.
    /// </summary>
    /// <remarks>
    /// Main scans for this before parsing, because the startup clock has to start
    /// before anything else runs. Whether diagnostics are on is then owned solely by
    /// <see cref="Diagnostics.StartupTimeline.Enabled"/> — the parsed options do not
    /// carry a second copy, so the two cannot disagree.
    /// </remarks>
    public const string DiagnosticsFlag = "--diag";

    private const int DefaultSampleMebibytes = 50;
    private const int MaxSampleMebibytes = 2048;

    /// <summary>Attempts to parse the argument list.</summary>
    /// <param name="args">Raw arguments, excluding the executable name.</param>
    /// <param name="options">The parsed options on success.</param>
    /// <param name="error">An actionable message on failure.</param>
    public static bool TryParse(
        string[] args,
        [NotNullWhen(true)] out CommandLineOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = null;
        error = null;

        var showHelp = false;
        var force = false;
        string? fileToOpen = null;
        string? samplePath = null;
        var sampleMebibytes = DefaultSampleMebibytes;
        var sampleShape = SampleShape.Json;
        var sawSampleOption = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case DiagnosticsFlag:
                    // Already acted on by Main; accepted here so it is not an
                    // unknown option.
                    break;

                case "--force":
                    force = true;
                    sawSampleOption = true;
                    break;

                case "--help" or "-h" or "-?" or "/?":
                    showHelp = true;
                    break;

                case "--gen-sample":
                    if (!TryTakeValue(args, ref i, "--gen-sample", out var rawSamplePath, out error))
                    {
                        return false;
                    }

                    if (!TryResolvePath(rawSamplePath, out samplePath, out error))
                    {
                        return false;
                    }

                    break;

                case "--size":
                    sawSampleOption = true;

                    if (!TryTakeValue(args, ref i, "--size", out var rawSize, out error))
                    {
                        return false;
                    }

                    if (!int.TryParse(rawSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out sampleMebibytes)
                        || sampleMebibytes is < 1 or > MaxSampleMebibytes)
                    {
                        error = $"--size must be a whole number of MiB between 1 and {MaxSampleMebibytes}; got '{rawSize}'.";
                        return false;
                    }

                    break;

                case "--shape":
                    sawSampleOption = true;

                    if (!TryTakeValue(args, ref i, "--shape", out var rawShape, out error))
                    {
                        return false;
                    }

                    // Enum.TryParse also accepts numeric strings, so "--shape 7"
                    // would parse to an undefined member and fall through to the
                    // default branch of every switch downstream.
                    if (!Enum.TryParse(rawShape, ignoreCase: true, out sampleShape) || !Enum.IsDefined(sampleShape))
                    {
                        error = $"--shape must be 'json' or 'text'; got '{rawShape}'.";
                        return false;
                    }

                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"Unknown option '{arg}'.";
                        return false;
                    }

                    if (fileToOpen is not null)
                    {
                        error = "Only one file can be opened at a time.";
                        return false;
                    }

                    // Existence is deliberately not checked here — the open path
                    // reports a missing file with better context, and checking twice
                    // only adds a time-of-check/time-of-use gap.
                    if (!TryResolvePath(arg, out fileToOpen, out error))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (!TryResolveMode(showHelp, samplePath, sampleMebibytes, sampleShape, force, sawSampleOption, fileToOpen, out var mode, out error))
        {
            return false;
        }

        options = new CommandLineOptions(mode);
        return true;
    }

    /// <summary>
    /// Collapses the flags into exactly one launch mode, rejecting combinations
    /// that mean two different things at once.
    /// </summary>
    private static bool TryResolveMode(
        bool showHelp,
        string? samplePath,
        int sampleMebibytes,
        SampleShape sampleShape,
        bool force,
        bool sawSampleOption,
        string? fileToOpen,
        [NotNullWhen(true)] out LaunchMode? mode,
        [NotNullWhen(false)] out string? error)
    {
        mode = null;
        error = null;

        if (showHelp)
        {
            if (samplePath is not null || fileToOpen is not null)
            {
                error = "--help cannot be combined with other arguments.";
                return false;
            }

            mode = new LaunchMode.ShowHelp();
            return true;
        }

        if (samplePath is not null)
        {
            if (fileToOpen is not null)
            {
                error = "--gen-sample writes a file and exits; it cannot also open one.";
                return false;
            }

            mode = new LaunchMode.GenerateSample(samplePath, sampleMebibytes, sampleShape, force);
            return true;
        }

        if (sawSampleOption)
        {
            error = "--size, --shape and --force only apply to --gen-sample.";
            return false;
        }

        mode = new LaunchMode.Edit(fileToOpen);
        return true;
    }

    private static bool TryTakeValue(
        string[] args,
        ref int index,
        string option,
        [NotNullWhen(true)] out string? value,
        [NotNullWhen(false)] out string? error)
    {
        value = null;

        if (index + 1 >= args.Length)
        {
            error = $"{option} requires a value.";
            return false;
        }

        var candidate = args[index + 1];

        // Without this, `--gen-sample --size 100` would resolve a file literally
        // named "--size" and then fail on "100" with a baffling message.
        if (candidate.StartsWith('-'))
        {
            error = $"{option} requires a value, but was followed by '{candidate}'.";
            return false;
        }

        index++;
        value = candidate;
        error = null;
        return true;
    }

    /// <summary>
    /// Resolves a user-supplied path to absolute form, rejecting anything malformed
    /// or pointing at something that is not an ordinary file.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFullPath(string)"/> normalises away <c>..</c> traversal
    /// and relative segments, so downstream code only ever sees a fully resolved
    /// path. It throws on invalid characters and over-long paths, which is exactly
    /// the validation wanted — converted here into a clear message rather than an
    /// unhandled exception at startup.
    /// <para>
    /// It also resolves DOS device names, so <c>CON</c> and <c>\\.\PhysicalDrive0</c>
    /// would otherwise pass. Those are rejected explicitly: <c>--gen-sample</c>
    /// opens its target for writing.
    /// </para>
    /// </remarks>
    private static bool TryResolvePath(
        string candidate,
        [NotNullWhen(true)] out string? fullPath,
        [NotNullWhen(false)] out string? error)
    {
        fullPath = null;
        error = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "A path was expected but the value was empty.";
            return false;
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"'{candidate}' is not a usable path: {ex.Message}";
            return false;
        }

        if (resolved.StartsWith(@"\\.\", StringComparison.Ordinal) || resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            error = $"'{candidate}' resolves to a device path, which Etch will not open.";
            return false;
        }

        if (!Path.IsPathFullyQualified(resolved))
        {
            error = $"'{candidate}' does not resolve to a fully qualified path.";
            return false;
        }

        fullPath = resolved;
        return true;
    }
}
