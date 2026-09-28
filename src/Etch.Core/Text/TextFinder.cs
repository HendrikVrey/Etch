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
/// Written rather than borrowed. AvalonEdit ships a search panel, but it finds only,
/// it has no replace at all, and its default template is styled for its own host
/// rather than for a Fluent window, so half of find-and-replace would have had to be
/// built here regardless and the two halves would have looked and behaved differently.
/// </para>
/// <para>
/// Pure and in <c>Etch.Core</c>, which is the point: every awkward case (overlapping
/// matches, an empty regex match, a replacement that contains the pattern, a
/// zero-length pattern) is a unit test rather than something to discover by clicking
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
    private readonly bool _overNormalisedText;

    /// <summary>
    /// The last buffer normalised, kept so a sequence of operations over one buffer
    /// normalises it once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on reference equality, not content: the callers hold the document text in a
    /// local for the duration of a find or a replace-all, so the same instance comes back
    /// several times and a content comparison would be as expensive as the work it saves.
    /// A miss is correct, merely slower, which is why this needs no invalidation.
    /// </para>
    /// <para>
    /// <b>One field, not two.</b> The source and the view of it have to become visible
    /// together, as a pair of fields they can be written in either order, and a reader
    /// that sees the new source beside the old view maps every offset through the wrong
    /// removal table and replaces at wrong positions with nothing raised. A single
    /// reference assignment publishes both or neither.
    /// </para>
    /// <para>
    /// The source is held <b>weakly</b>. A finder outlives the document it last ran
    /// over (it is discarded only when the find box's text or options change, so it
    /// survives closing the tab) and a strong reference here would keep both the
    /// original buffer and its normalised copy alive behind it. Two hundred megabytes
    /// retained for a 100 MB file, against the 120 MB budget cited above.
    /// </para>
    /// <para>
    /// <b>Instances are not thread-safe.</b> They are used from the UI thread.
    /// </para>
    /// </remarks>
    private sealed record CachedView(WeakReference<string> Source, CrlfView View);

    private CachedView? _cache;

    private TextFinder(Regex regex, SearchOptions options, bool overNormalisedText)
    {
        _regex = regex;
        _options = options;
        _overNormalisedText = overNormalisedText;
    }

    /// <summary>How this search interprets its pattern.</summary>
    public SearchOptions Options => _options;

    /// <summary>
    /// Whether this search runs over a CRLF-normalised view of the buffer.
    /// </summary>
    /// <remarks>
    /// False for literal searches, which need the buffer exactly as it is: the pattern
    /// has been through <see cref="Regex.Escape(string)"/>, so nothing in it can match a
    /// carriage return by accident, and someone searching literally for a line ending
    /// must still find one.
    /// </remarks>
    public bool SearchesNormalisedText => _overNormalisedText;

    /// <summary>The view of <paramref name="text"/> this search actually runs over.</summary>
    private CrlfView ViewOf(string text)
    {
        if (!_overNormalisedText)
        {
            return CrlfView.Identity(text);
        }

        if (_cache is { } cached
            && cached.Source.TryGetTarget(out var source)
            && ReferenceEquals(source, text))
        {
            return cached.View;
        }

        var view = CrlfView.Create(text);

        _cache = new CachedView(new WeakReference<string>(text), view);

        return view;
    }

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
        // into a text editor means by them.
        var flags = RegexOptions.Multiline | RegexOptions.CultureInvariant;

        if (!options.MatchCase)
        {
            flags |= RegexOptions.IgnoreCase;
        }

        try
        {
            // The search runs over a CRLF-normalised view unless the pattern is plainly
            // looking for a carriage return. See NeedsCarriageReturns for why the test is
            // a conservative one and what it costs.
            var overCrlf = options.UseRegex && !NeedsCarriageReturns(pattern);

            return new SearchCompilation(
                new TextFinder(new Regex(expression, flags, MatchTimeout), options, overCrlf),
                null);
        }
        catch (ArgumentException ex)
        {
            return new SearchCompilation(null, ex.Message);
        }
    }

    /// <summary>
    /// Whether <paramref name="pattern"/> is asking for carriage returns specifically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deciding whether to search the normalised view, and deliberately biased towards
    /// <b>not</b> normalising: a false positive here leaves the old, documented CRLF
    /// behaviour in place, while a false negative would make a search for line endings
    /// silently find nothing. Wrong in the safe direction.
    /// </para>
    /// <para>
    /// Only the spellings that name a carriage return outright are looked for. Classes
    /// that merely happen to include one (<c>\s</c>, <c>\W</c>, <c>[^a]</c>, and
    /// <c>.</c> itself) are not, and that is the point rather than an omission: those
    /// are the patterns that were behaving badly. <c>\s+$</c> against a CRLF line
    /// currently matches the trailing spaces <i>and</i> the carriage return, so using it
    /// to trim whitespace rewrites the file's line endings. Over the normalised view it
    /// trims the spaces and leaves the terminator alone, which is what was asked for.
    /// </para>
    /// </remarks>
    private static bool NeedsCarriageReturns(string pattern)
    {
        if (pattern.Contains('\r', StringComparison.Ordinal))
        {
            return true;
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '\\' || i + 1 >= pattern.Length)
            {
                continue;
            }

            switch (pattern[i + 1])
            {
                // An escaped backslash. Stepping over it is what stops a literal
                // backslash followed by the letter r from reading as the \r escape.
                case '\\':
                    i++;
                    continue;

                case 'r':
                    return true;

                // .NET upper-cases the control letter before subtracting '@', so \cm is
                // the same character as \cM.
                case 'c' when i + 2 < pattern.Length && pattern[i + 2] is 'M' or 'm':
                    return true;

                // The escape letter itself is case-sensitive, \X and \U are not escapes
                // at all, but the digits after it are not.
                case 'x' when IsHexEscape(pattern, i + 2, digits: 2):
                case 'u' when IsHexEscape(pattern, i + 2, digits: 4):
                    return true;

                default:
                    if (IsOctalCarriageReturn(pattern, i + 1))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether exactly <paramref name="digits"/> hexadecimal digits at
    /// <paramref name="start"/> spell a carriage return.
    /// </summary>
    private static bool IsHexEscape(string pattern, int start, int digits)
    {
        if (start + digits > pattern.Length)
        {
            return false;
        }

        var parsed = 0;

        for (var i = start; i < start + digits; i++)
        {
            var value = pattern[i] switch
            {
                >= '0' and <= '9' => pattern[i] - '0',
                >= 'a' and <= 'f' => pattern[i] - 'a' + 10,
                >= 'A' and <= 'F' => pattern[i] - 'A' + 10,
                _ => -1,
            };

            if (value < 0)
            {
                return false;
            }

            parsed = (parsed * 16) + value;
        }

        return parsed == '\r';
    }

    /// <summary>
    /// Whether the escape beginning at <paramref name="start"/> is an octal carriage return.
    /// </summary>
    /// <remarks>
    /// .NET reads one to three octal digits, greedily, so <c>\15</c> and <c>\015</c> are
    /// both U+000D while <c>\150</c> is a lower-case h. A leading digit may instead be a
    /// backreference when the pattern has that many groups; it is read as a carriage
    /// return either way, because over-reporting only costs the normalisation while
    /// under-reporting loses matches with nothing said.
    /// </remarks>
    private static bool IsOctalCarriageReturn(string pattern, int start)
    {
        var parsed = 0;
        var read = 0;

        while (read < 3 && start + read < pattern.Length && pattern[start + read] is >= '0' and <= '7')
        {
            parsed = (parsed * 8) + (pattern[start + read] - '0');
            read++;
        }

        return read > 0 && parsed == '\r';
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

        var view = ViewOf(text);
        var results = new List<SearchMatch>();

        foreach (var match in EnumerateFrom(view, 0, NewDeadline()))
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
        var view = ViewOf(text);

        // At-or-after, not the plain floor. Both halves of a removed \r\n share one
        // normalised offset, so the caller's "resume just past the last match" nudge
        // across a line ending maps back to where it started, and a zero-width pattern
        // like `$` then returns the same match for ever, which is an Enter key that
        // stops working rather than an obviously wrong answer.
        var from = view.ToNormalisedAtOrAfter(Math.Clamp(startOffset, 0, text.Length));

        foreach (var match in EnumerateFrom(view, from, deadline))
        {
            return match;
        }

        foreach (var match in EnumerateFrom(view, 0, deadline))
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
        var view = ViewOf(text);
        var limit = Math.Clamp(startOffset, 0, text.Length);

        SearchMatch? best = null;
        SearchMatch? last = null;

        // One pass, not two. The earlier shape walked the document again from the top
        // whenever nothing was found before the caret, which is the common case for the
        // first Shift+Enter: doubling the cost of the very press most likely to be slow.
        //
        // The matches arrive in original coordinates, so `limit` stays the caret offset
        // the caller passed rather than being mapped into the view.
        foreach (var match in EnumerateFrom(view, 0, deadline))
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
    /// cannot see past it, and <c>\B</c> inverts at the window edge, every such pattern
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

        var view = ViewOf(text);
        var found = _regex.Match(view.Text, view.ToNormalised(match.Offset));

        if (!found.Success)
        {
            return replacement;
        }

        var mapped = view.ToOriginal(new SearchMatch(found.Index, found.Length));

        if (mapped != match)
        {
            return replacement;
        }

        // The buffer and the view are the same string, so .NET's own expansion is exact.
        // Every LF-ending document and every literal search takes this path, which is the
        // whole point of keeping it: the custom expansion below only has to be right
        // about CRLF buffers, not about replacement syntax in general.
        return view.IsNormalised ? ExpandOverOriginal(text, view, found, replacement) : found.Result(replacement);
    }

    /// <summary>
    /// Expands <paramref name="replacement"/> using text taken from the original buffer
    /// rather than from the normalised view the match was found in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Match.Result"/> substitutes from the string the match was made against,
    /// which here has had its carriage returns removed. So <c>$&amp;</c> against a CRLF
    /// buffer expands to an LF-only copy of the matched text, and replacing the original
    /// span with it converts that line ending: reintroducing, through the replacement
    /// syntax, the exact defect <see cref="CrlfView"/> exists to prevent. <c>$_</c> is the
    /// same bug over the whole document.
    /// </para>
    /// <para>
    /// Only the substitutions .NET defines are honoured, and an unrecognised one is left
    /// alone with its <c>$</c>, which is what <see cref="Match.Result"/> does too.
    /// </para>
    /// </remarks>
    private static string ExpandOverOriginal(string text, CrlfView view, Match found, string replacement)
    {
        var whole = view.ToOriginal(new SearchMatch(found.Index, found.Length));

        var builder = new System.Text.StringBuilder(replacement.Length);

        for (var i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] != '$' || i + 1 >= replacement.Length)
            {
                builder.Append(replacement[i]);
                continue;
            }

            var next = replacement[i + 1];

            switch (next)
            {
                case '$':
                    builder.Append('$');
                    i++;
                    break;

                case '&':
                    builder.Append(text, whole.Offset, whole.Length);
                    i++;
                    break;

                case '`':
                    builder.Append(text, 0, whole.Offset);
                    i++;
                    break;

                case '\'':
                    builder.Append(text, whole.EndOffset, text.Length - whole.EndOffset);
                    i++;
                    break;

                case '_':
                    builder.Append(text);
                    i++;
                    break;

                case '+':
                    AppendLastCaptured(builder, text, view, found);
                    i++;
                    break;

                case '{':
                    i = AppendBracedGroup(builder, text, view, found, replacement, i);
                    break;

                default:
                    if (char.IsAsciiDigit(next))
                    {
                        i = AppendNumberedGroup(builder, text, view, found, replacement, i);
                        break;
                    }

                    builder.Append('$');
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Appends a group's text, taken from the original buffer.</summary>
    private static void AppendGroup(System.Text.StringBuilder builder, string text, CrlfView view, Group group)
    {
        if (!group.Success)
        {
            return;
        }

        var span = view.ToOriginal(new SearchMatch(group.Index, group.Length));

        builder.Append(text, span.Offset, span.Length);
    }

    /// <summary>Appends <c>$+</c>: the highest-numbered group that captured.</summary>
    private static void AppendLastCaptured(System.Text.StringBuilder builder, string text, CrlfView view, Match found)
    {
        for (var number = found.Groups.Count - 1; number >= 1; number--)
        {
            if (found.Groups[number].Success)
            {
                AppendGroup(builder, text, view, found.Groups[number]);
                return;
            }
        }
    }

    /// <summary>
    /// Appends <c>${name}</c> or <c>${number}</c>, returning the index of its last character.
    /// </summary>
    private static int AppendBracedGroup(
        System.Text.StringBuilder builder,
        string text,
        CrlfView view,
        Match found,
        string replacement,
        int dollar)
    {
        var close = replacement.IndexOf('}', dollar + 2);

        // Unterminated, so not a substitution at all. .NET emits it verbatim.
        if (close < 0)
        {
            builder.Append('$');
            return dollar;
        }

        if (!TryResolveGroup(found, replacement[(dollar + 2)..close], out var group))
        {
            builder.Append('$');
            return dollar;
        }

        AppendGroup(builder, text, view, group);

        return close;
    }

    /// <summary>
    /// Appends <c>$number</c>, returning the index of its last character.
    /// </summary>
    /// <remarks>
    /// .NET reads digits greedily and then backs off while the number names no group, so
    /// <c>$12</c> is group 12 in a pattern with twelve groups and group 1 followed by a
    /// literal 2 in a pattern with one.
    /// </remarks>
    private static int AppendNumberedGroup(
        System.Text.StringBuilder builder,
        string text,
        CrlfView view,
        Match found,
        string replacement,
        int dollar)
    {
        var end = dollar + 1;

        while (end + 1 < replacement.Length && char.IsAsciiDigit(replacement[end + 1]))
        {
            end++;
        }

        while (end > dollar)
        {
            if (TryResolveGroup(found, replacement[(dollar + 1)..(end + 1)], out var group))
            {
                AppendGroup(builder, text, view, group);
                return end;
            }

            end--;
        }

        builder.Append('$');

        return dollar;
    }

    /// <summary>
    /// Resolves a substitution token to a group of <paramref name="found"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A number is resolved by <b>position</b>, not by name. Named groups are numbered
    /// too, <c>(?&lt;line&gt;.*)</c> is group 1, and <c>Groups[1].Name</c> is
    /// <c>"line"</c>, so looking a number up by name finds nothing and <c>$1</c> comes
    /// out as the literal text <c>$1</c>. <see cref="GroupCollection.Count"/> is the
    /// bound, because <see cref="GroupCollection"/> returns an unsuccessful group for an
    /// index it does not have rather than throwing.
    /// </para>
    /// <para>
    /// A name has to be checked against the declared groups rather than by asking whether
    /// it captured: an unknown name and a real group that matched nothing both come back
    /// unsuccessful, and .NET treats only the first as literal text.
    /// </para>
    /// </remarks>
    private static bool TryResolveGroup(Match found, string token, out Group group)
    {
        if (int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            group = found.Groups[number];

            return number < found.Groups.Count;
        }

        foreach (Group candidate in found.Groups)
        {
            if (string.Equals(candidate.Name, token, StringComparison.Ordinal))
            {
                group = candidate;
                return true;
            }
        }

        group = Match.Empty.Groups[0];

        return false;
    }

    /// <summary>A wall-clock deadline for one whole find operation.</summary>
    /// <remarks>
    /// A whole-scan budget as well as the per-match one on the regular expression. That
    /// one bounds a single attempt; a pattern that matches a hundred thousand times just
    /// under it never throws and runs for days.
    /// </remarks>
    private static long NewDeadline() => Environment.TickCount64 + (long)ScanBudget.TotalMilliseconds;

    /// <summary>
    /// Walks matches from an offset, guaranteeing forward progress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A regular expression can match the empty string, <c>a*</c> against
    /// <c>"bbb"</c> matches at every position, and a loop that advances by the match
    /// length would then never advance at all. Stepping one character past an
    /// empty match is what turns that from a hang into the behaviour everyone expects.
    /// </para>
    /// <para>
    /// Takes normalised offsets and yields matches in the caller's coordinates. Doing the
    /// mapping in one place is what keeps every caller from having to remember which
    /// space it is in: the mistake that would otherwise be made once per method.
    /// </para>
    /// </remarks>
    private IEnumerable<SearchMatch> EnumerateFrom(CrlfView view, int startOffset, long deadline)
    {
        var text = view.Text;
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

            yield return view.ToOriginal(new SearchMatch(match.Index, match.Length));

            offset = match.Length > 0 ? match.Index + match.Length : match.Index + 1;
        }
    }
}
