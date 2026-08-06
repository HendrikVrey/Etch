namespace Etch.Core.Text;

/// <summary>A region of the buffer that can be collapsed.</summary>
/// <param name="StartOffset">Offset of the opening bracket.</param>
/// <param name="EndOffset">Offset one past the closing bracket.</param>
/// <param name="Label">What to draw in place of the collapsed text.</param>
public readonly record struct FoldRegion(int StartOffset, int EndOffset, string Label);

/// <summary>
/// Finds foldable regions by matching brackets, ignoring those inside strings and comments.
/// </summary>
/// <remarks>
/// <para>
/// AvalonEdit ships exactly one folding strategy and it is for XML, so everything else —
/// JSON, C#, JavaScript, CSS, the languages people actually open a scratchpad for — has
/// none. This is that strategy, written here rather than in the UI layer because the
/// entire difficulty is lexical: a <c>{</c> inside a string literal is not a block, a
/// <c>}</c> inside a comment does not close one, and a file with one stray quote in it
/// must not lose folding for everything below that line. Those are properties to assert
/// in a test, not behaviour to discover by scrolling.
/// </para>
/// <para>
/// <b>One lexical model, stated plainly.</b> Double-quoted and single-quoted strings with
/// backslash escapes, <c>//</c> to end of line, and <c>/* */</c> across lines. That is the
/// C family and JSON, which is exactly the set of languages this is offered for — XML and
/// HTML fold through AvalonEdit's own strategy, and Python and Markdown are not offered
/// folding at all because their structure is not bracketed and pretending otherwise would
/// produce folds in the wrong places.
/// </para>
/// <para>
/// <b>Known imprecision, deliberate.</b> A string is ended by a newline as well as by its
/// closing quote. That is wrong for C# verbatim strings and JavaScript template literals,
/// which really do span lines — but the alternative is worse in the case that actually
/// happens: a single unmatched quote anywhere in a file would otherwise swallow the whole
/// remainder as string content and silently delete every fold below it. Recovering at the
/// line break confines the damage to one line, which is the same bet every syntax
/// highlighter makes.
/// </para>
/// </remarks>
public static class BraceFolding
{
    /// <summary>
    /// Ceiling on how deep nesting is tracked.
    /// </summary>
    /// <remarks>
    /// Untrusted input, and the stack grows with nesting depth: a file that is a megabyte
    /// of <c>[</c> is not a plausible document but it is a trivially constructible one, and
    /// without a bound it costs a stack entry per byte. Past this depth brackets are
    /// counted but not recorded, so the scan stays linear and correct for everything
    /// shallower.
    /// </remarks>
    public const int MaxDepth = 512;

    /// <summary>
    /// Ceiling on how many regions are returned.
    /// </summary>
    /// <remarks>
    /// The same argument as <see cref="TextFinder.MaxMatches"/>: a fold margin with a
    /// hundred thousand markers in it is not usable, and building the list is what costs.
    /// </remarks>
    public const int MaxRegions = 20_000;

    /// <summary>
    /// Every foldable region in <paramref name="text"/>, ordered by start offset.
    /// </summary>
    /// <remarks>
    /// Ordered because AvalonEdit's <c>FoldingManager.UpdateFoldings</c> requires it and
    /// misbehaves quietly rather than throwing when given anything else. Matching brackets
    /// are discovered innermost-first, which is the opposite order, so the sort at the end
    /// is load-bearing and not tidiness.
    /// </remarks>
    /// <param name="text">The buffer to scan.</param>
    public static IReadOnlyList<FoldRegion> Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var regions = new List<FoldRegion>();
        var open = new Stack<Opening>();

        var line = 0;
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            switch (character)
            {
                case '\n':
                    line++;
                    index++;
                    continue;

                case '"':
                case '\'':
                    index = SkipString(text, index, ref line);
                    continue;

                case '/' when index + 1 < text.Length && text[index + 1] == '/':
                    index = SkipLineComment(text, index);
                    continue;

                case '/' when index + 1 < text.Length && text[index + 1] == '*':
                    index = SkipBlockComment(text, index, ref line);
                    continue;

                case '{':
                case '[':
                    if (open.Count < MaxDepth)
                    {
                        open.Push(new Opening(character, index, line));
                    }

                    index++;
                    continue;

                case '}':
                case ']':
                    Close(character, index, line, open, regions);
                    index++;
                    continue;

                default:
                    index++;
                    continue;
            }
        }

        // Innermost-first becomes outermost-first. Sorting on the start offset alone is
        // enough, and stability is not needed rather than assumed — List<T>.Sort is
        // introsort and is not stable — because two regions cannot share a start offset:
        // that would need one bracket to open two regions.
        regions.Sort(static (first, second) => first.StartOffset.CompareTo(second.StartOffset));

        return regions;
    }

    /// <summary>Closes the innermost matching bracket, if there is one.</summary>
    /// <remarks>
    /// A closer with nothing to match is discarded rather than popping whatever happens to
    /// be on top. Popping would let a single stray <c>}</c> in a comment-like position
    /// close an outer block and produce a fold spanning the wrong half of the file — a
    /// visibly broken margin, where discarding merely produces one fewer fold.
    /// </remarks>
    private static void Close(char closer, int index, int line, Stack<Opening> open, List<FoldRegion> regions)
    {
        if (open.Count == 0 || open.Peek().Character != Opener(closer))
        {
            return;
        }

        var opening = open.Pop();

        // Single-line regions are skipped: collapsing them hides nothing and every one of
        // them costs a marker in the margin, which is how a fold margin becomes noise.
        if (opening.Line == line || regions.Count >= MaxRegions)
        {
            return;
        }

        regions.Add(new FoldRegion(opening.Offset, index + 1, opening.Character == '{' ? "{...}" : "[...]"));
    }

    private static char Opener(char closer) => closer == '}' ? '{' : '[';

    /// <summary>
    /// Steps over a quoted string, returning the offset just past it.
    /// </summary>
    /// <remarks>
    /// A backslash escapes the next character whatever it is, which is what stops
    /// <c>"\\"</c> — a string holding one backslash — from reading as an unterminated
    /// string that swallows the rest of the line. The newline case is the recovery
    /// described on the type: it is consumed and counted, so the caller's line number stays
    /// correct even when a quote was never closed.
    /// </remarks>
    private static int SkipString(string text, int start, ref int line)
    {
        var quote = text[start];
        var index = start + 1;

        while (index < text.Length)
        {
            var character = text[index];

            if (character == '\\')
            {
                // Two characters, not one — but never past the end, and never over a
                // newline, or an escape at the end of a line would hide the line break from
                // the counter and every fold below would be attributed to the wrong line.
                if (index + 1 < text.Length && text[index + 1] != '\n')
                {
                    index += 2;
                    continue;
                }

                index++;
                continue;
            }

            if (character == '\n')
            {
                line++;
                return index + 1;
            }

            index++;

            if (character == quote)
            {
                return index;
            }
        }

        return index;
    }

    /// <summary>Steps to the line break ending a <c>//</c> comment, leaving it unconsumed.</summary>
    /// <remarks>
    /// The newline is left for the main loop so that exactly one place counts lines. A
    /// second counter here is how the two drift apart.
    /// </remarks>
    private static int SkipLineComment(string text, int start)
    {
        var end = text.IndexOf('\n', start);

        return end < 0 ? text.Length : end;
    }

    /// <summary>Steps over a <c>/* */</c> comment, counting the lines inside it.</summary>
    /// <remarks>
    /// An unterminated block comment runs to the end of the buffer, which is what the
    /// compiler would do with it too.
    /// </remarks>
    private static int SkipBlockComment(string text, int start, ref int line)
    {
        var index = start + 2;

        while (index < text.Length)
        {
            if (text[index] == '\n')
            {
                line++;
            }
            else if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/')
            {
                return index + 2;
            }

            index++;
        }

        return text.Length;
    }

    private readonly record struct Opening(char Character, int Offset, int Line);
}
