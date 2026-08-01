namespace Etch.Core.Palette;

/// <summary>
/// Scores how well a candidate matches what someone typed.
/// </summary>
/// <remarks>
/// <para>
/// A subsequence match with bonuses, which is what every palette worth using does:
/// <c>fj</c> finds "Format JSON", <c>b64d</c> finds "Base64 decode". The bonuses are
/// what separate a useful ordering from an arbitrary one — a match on the initials of
/// each word beats a match on adjacent letters in the middle of one.
/// </para>
/// <para>
/// Deliberately not Levenshtein. Edit distance answers "how similar are these two
/// strings", and the question here is "is what I typed an abbreviation of this",
/// which is a different question with a much cheaper answer.
/// </para>
/// </remarks>
public static class FuzzyMatch
{
    /// <summary>Every matched character is worth this much before bonuses.</summary>
    private const int MatchScore = 8;

    /// <summary>A match immediately after the previous one. Rewards typing a prefix.</summary>
    private const int ConsecutiveBonus = 12;

    /// <summary>A match at the start of a word. Rewards typing initials.</summary>
    private const int WordStartBonus = 16;

    /// <summary>Charged per character skipped before the first match.</summary>
    private const int LeadingPenalty = 2;

    /// <summary>
    /// Scores <paramref name="query"/> against <paramref name="candidate"/>.
    /// </summary>
    /// <param name="candidate">The text being searched.</param>
    /// <param name="query">What was typed. Empty matches everything, with score zero.</param>
    /// <param name="score">The score, higher being better.</param>
    /// <returns>False when the query is not a subsequence of the candidate.</returns>
    public static bool TryScore(ReadOnlySpan<char> candidate, ReadOnlySpan<char> query, out int score)
    {
        score = 0;

        if (query.IsEmpty)
        {
            return true;
        }

        if (candidate.IsEmpty)
        {
            return false;
        }

        var queryIndex = 0;
        var previousMatch = -2;

        for (var i = 0; i < candidate.Length && queryIndex < query.Length; i++)
        {
            if (char.ToUpperInvariant(candidate[i]) != char.ToUpperInvariant(query[queryIndex]))
            {
                continue;
            }

            score += MatchScore;

            if (i == previousMatch + 1)
            {
                score += ConsecutiveBonus;
            }

            if (IsWordStart(candidate, i))
            {
                score += WordStartBonus;
            }

            if (queryIndex == 0)
            {
                score -= Math.Min(i * LeadingPenalty, MatchScore);
            }

            previousMatch = i;
            queryIndex++;
        }

        if (queryIndex < query.Length)
        {
            score = 0;
            return false;
        }

        // Shorter candidates win ties: "Sort lines" should beat "Sort JSON keys" for a
        // query of "sort", because there is less of it that the query did not explain.
        score -= candidate.Length / 4;

        return true;
    }

    /// <summary>
    /// Scores against a name and its aliases, keeping the best.
    /// </summary>
    /// <remarks>
    /// Aliases score slightly lower than the name at equal quality, so that a transform
    /// matched by what it is called ranks above one matched by a synonym.
    /// </remarks>
    /// <param name="name">The display name.</param>
    /// <param name="aliases">Extra words to match against.</param>
    /// <param name="query">What was typed.</param>
    /// <param name="score">The best score found.</param>
    /// <returns>False when nothing matched.</returns>
    public static bool TryScoreAny(
        string name,
        IReadOnlyList<string> aliases,
        ReadOnlySpan<char> query,
        out int score)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(aliases);

        var matched = TryScore(name, query, out score);

        const int AliasPenalty = 6;

        foreach (var alias in aliases)
        {
            if (alias is null || !TryScore(alias, query, out var aliasScore))
            {
                continue;
            }

            aliasScore -= AliasPenalty;

            if (!matched || aliasScore > score)
            {
                score = aliasScore;
                matched = true;
            }
        }

        if (!matched)
        {
            score = 0;
        }

        return matched;
    }

    /// <summary>True at the first character, or after a separator or a case change.</summary>
    private static bool IsWordStart(ReadOnlySpan<char> text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = text[index - 1];

        return !char.IsLetterOrDigit(previous)
            || (char.IsLower(previous) && char.IsUpper(text[index]));
    }
}
