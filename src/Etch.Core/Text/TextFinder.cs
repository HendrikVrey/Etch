using System.Text.RegularExpressions;

namespace Etch.Core.Text;

/// <summary>How a search pattern is interpreted.</summary>
/// <param name="MatchCase">Whether the search is case-sensitive.</param>
/// <param name="WholeWord">Whether a match must be bounded by non-word characters.</param>
/// <param name="UseRegex">Whether the pattern is a regular expression rather than literal text.</param>
public readonly record struct SearchOptions(bool MatchCase = false, bool WholeWord = false, bool UseRegex = false);

/// <summary>One occurrence of a pattern in a buffer.</summary>
/// <param name="Offset">Character offset of the match.</param>
/// <param name="Length">Length of the match, which can be zero for a regex that matches empty.</param>
public readonly record struct SearchMatch(int Offset, int Length)
{
    /// <summary>The offset one past the end of the match.</summary>
    public int EndOffset => Offset + Length;
}

/// <summary>The outcome of compiling a user-supplied pattern.</summary>
/// <param name="Finder">The compiled search, or null when the pattern was rejected.</param>
/// <param name="Error">Why the pattern was rejected, or null when it was accepted.</param>
public readonly record struct SearchCompilation(TextFinder? Finder, string? Error);

/// <summary>
/// Finds and replaces text in a buffer.
/// </summary>
/// <remarks>
/// <para>
/// Written rather than borrowed. AvalonEdit ships a search panel, but it finds only —
/// it has no replace at all — and its default template is styled for its own host
/// rather than for a Fluent window, so half of find-and-replace would have had to be
/// built here regardless and the two halves would have looked and behaved differently.
/// </para>
/// <para>
/// Pure and in <c>Etch.Core</c>, which is the point: every awkward case — overlapping
/// matches, an empty regex match, a replacement that contains the pattern, a
/// zero-length pattern — is a unit test rather than something to discover by clicking
/// around.
/// </para>
/// <para>
/// The regular expression is built with a match timeout. A user-supplied pattern is
/// untrusted input even when the user is the one who typed it: a handful of innocent
/// looking patterns backtrack catastrophically, and an editor that freezes with no
/// way out over a typo in a search box is an editor that loses work.
/// </para>
/// </remarks>
public sealed class TextFinder
{
    /// <summary>
    /// Ceiling on how long a single match attempt may run.
    /// </summary>
    /// <remarks>
    /// Long enough that no reasonable pattern hits it, short enough that a pathological
    /// one surfaces as an error message rather than as a hung window.
    /// </remarks>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Ceiling on a whole scan, however many individual matches it makes.</summary>
    public static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Ceiling on how many matches a single scan will collect.
    /// </summary>
    /// <remarks>
    /// <see cref="MatchTimeout"/> bounds one match attempt, not a scan: a pattern that
    /// matches a hundred thousand times just under the limit never throws and runs for
    /// days. And a pattern like <c>a*</c> matches at every position, so an unbounded
    /// result list over a ten-megabyte document is hundreds of megabytes against a
    /// 120 MB working-set budget. Both are the same fix.
    /// </remarks>
    public const int MaxMatches = 100_000;

    private readonly Regex _regex;
    private readonly SearchOptions _options;

    private TextFinder(Regex regex, SearchOptions options)
    {
        _regex = regex;
        _options = options;
    }

    /// <summary>How this search interprets its pattern.</summary>
    public SearchOptions Options => _options;

    /// <summary>
    /// Compiles <paramref name="pattern"/>, reporting a bad regular expression rather
    /// than throwing it.
    /// </summary>
    /// <remarks>
    /// Patterns arrive one keystroke at a time, so most of the intermediate ones are
    /// syntactically invalid by definition. An exception per keystroke is the wrong
    /// shape for that; a message the caller can show beside the box is the right one.
    /// </remarks>
    public static SearchCompilation Compile(string? pattern, SearchOptions options)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return new SearchCompilation(null, null);
        }

        var expression = options.UseRegex ? pattern : Regex.Escape(pattern);

        if (options.WholeWord)
        {
            // \b is a zero-width assertion, so this neither consumes characters nor
            // shifts the reported offsets. Wrapped in a group because an alternation in
            // the user's pattern would otherwise bind only its first and last branches.
            expression = $@"\b(?:{expression})\b";
        }

        // Multiline so that ^ and $ mean "line", which is what someone typing a pattern
        // into a text editor means by them. Note the consequence on a CRLF buffer: `.`
        // matches \r, so `^(.*)$` captures a trailing carriage return and a replacement
        // built from it converts that line to LF. Documented rather than worked around —
        // silently rewriting the user's pattern would be worse.
        var flags = RegexOptions.Multiline | RegexOptions.CultureInvariant;

        if (!options.MatchCase)
        {
            flags |= RegexOptions.IgnoreCase;
        }

        try
        {
            return new SearchCompilation(new TextFinder(new Regex(expression, flags, MatchTimeout), options), null);
        }
        catch (ArgumentException ex)
        {
            return new SearchCompilation(null, ex.Message);
        }
    }

    /// <summary>
    /// Every match in <paramref name="text"/>, in order, up to <see cref="MaxMatches"/>.
    /// </summary>
    /// <param name="text">The buffer to scan.</param>
    /// <param name="truncated">
    /// True when the cap was reached and the list is a prefix of the real answer. The
    /// caller must act on this: a replace-all that silently did only the first hundred
    /// thousand of them would be worse than one that refused.
    /// </param>
    /// <exception cref="RegexMatchTimeoutException">The pattern took too long.</exception>
    public IReadOnlyList<SearchMatch> FindAll(string text, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(text);

        var results = new List<SearchMatch>();

        foreach (var match in EnumerateFrom(text, 0))
        {
            if (results.Count == MaxMatches)
            {
                truncated = true;
                return results;
            }

            results.Add(match);
        }

        truncated = false;
        return results;
    }

    /// <inheritdoc cref="FindAll(string, out bool)" />
    public IReadOnlyList<SearchMatch> FindAll(string text) => FindAll(text, out _);

    /// <summary>
    /// The first match at or after <paramref name="startOffset"/>, wrapping to the top.
    /// </summary>
    /// <remarks>
    /// Wrapping is what makes repeated Enter feel like a search rather than like a
    /// scan that quietly stops. Returns null only when there are no matches anywhere.
    /// </remarks>
    /// <exception cref="RegexMatchTimeoutException">The pattern took too long.</exception>
    public SearchMatch? FindNext(string text, int startOffset)
    {
        ArgumentNullException.ThrowIfNull(text);

        // One deadline across both passes, not one each. These run on the UI thread, and
        // two independent budgets would let a single Enter press freeze the window for
        // twice as long as the budget claims.
        var deadline = NewDeadline();
        var from = Math.Clamp(startOffset, 0, text.Length);

        foreach (var match in EnumerateFrom(text, from, deadline))
        {
            return match;
        }

        foreach (var match in EnumerateFrom(text, 0, deadline))
        {
            return match;
        }

        return null;
    }

    /// <summary>
    /// The last match ending at or before <paramref name="startOffset"/>, wrapping to
    /// the bottom.
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">The pattern took too long.</exception>
    public SearchMatch? FindPrevious(string text, int startOffset)
    {
        ArgumentNullException.ThrowIfNull(text);

        var deadline = NewDeadline();
        var limit = Math.Clamp(startOffset, 0, text.Length);

        SearchMatch? best = null;
        SearchMatch? last = null;

        // One pass, not two. The earlier shape walked the document again from the top
        // whenever nothing was found before the caret, which is the common case for the
        // first Shift+Enter — doubling the cost of the very press most likely to be slow.
        foreach (var match in EnumerateFrom(text, 0, deadline))
        {
            last = match;

            if (match.EndOffset <= limit)
            {
                best = match;
            }
        }

        return best ?? last;
    }

    /// <summary>
    /// The text a match should be replaced with.
    /// </summary>
    /// <param name="text">The buffer the match came from.</param>
    /// <param name="match">A match previously returned for this buffer.</param>
    /// <param name="replacement">
    /// The replacement. For a regular-expression search this honours substitutions such
    /// as <c>$1</c>; for a literal search it is used verbatim, because someone replacing
    /// a literal <c>$</c> has not opted into substitution syntax and would be astonished
    /// to find it applied.
    /// </param>
    /// <remarks>
    /// The re-match deliberately uses the two-argument
    /// <see cref="Regex.Match(string, int)"/> and checks the result, not the
    /// three-argument overload that takes a length. Since .NET 7 the three-argument form
    /// runs against a *slice*, so a lookbehind cannot see before the match, a lookahead
    /// cannot see past it, and <c>\B</c> inverts at the window edge — every such pattern
    /// would fail to re-match and this method would fall through to returning the
    /// unexpanded replacement, writing a literal <c>$1</c> into the user's buffer with
    /// no error anywhere.
    /// </remarks>
    /// <exception cref="RegexMatchTimeoutException">The pattern took too long.</exception>
    public string Expand(string text, SearchMatch match, string replacement)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(replacement);

        if (!_options.UseRegex)
        {
            return replacement;
        }

        var found = _regex.Match(text, match.Offset);

        return found.Success && found.Index == match.Offset && found.Length == match.Length
            ? found.Result(replacement)
            : replacement;
    }

    /// <summary>
    /// Walks matches from an offset, guaranteeing forward progress.
    /// </summary>
    /// <remarks>
    /// A regular expression can match the empty string — <c>a*</c> against
    /// <c>"bbb"</c> matches at every position — and a loop that advances by the match
    /// length would then never advance at all. Stepping one character past an
    /// empty match is what turns that from a hang into the behaviour everyone expects.
    /// </remarks>
    /// <summary>A wall-clock deadline for one whole find operation.</summary>
    /// <remarks>
    /// A whole-scan budget as well as the per-match one on the regular expression. That
    /// one bounds a single attempt; a pattern that matches a hundred thousand times just
    /// under it never throws and runs for days.
    /// </remarks>
    private static long NewDeadline() => Environment.TickCount64 + (long)ScanBudget.TotalMilliseconds;

    private IEnumerable<SearchMatch> EnumerateFrom(string text, int startOffset) =>
        EnumerateFrom(text, startOffset, NewDeadline());

    private IEnumerable<SearchMatch> EnumerateFrom(string text, int startOffset, long deadline)
    {
        var offset = startOffset;

        while (offset <= text.Length)
        {
            if (Environment.TickCount64 > deadline)
            {
                // Not the input: the exception would otherwise hold the entire document
                // alive for as long as anything held the exception.
                throw new RegexMatchTimeoutException(string.Empty, _regex.ToString(), ScanBudget);
            }

            var match = _regex.Match(text, offset);

            if (!match.Success)
            {
                yield break;
            }

            yield return new SearchMatch(match.Index, match.Length);

            offset = match.Length > 0 ? match.Index + match.Length : match.Index + 1;
        }
    }
}
