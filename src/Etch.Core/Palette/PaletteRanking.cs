using Etch.Core.Abstractions;
using Etch.Core.Transforms;

namespace Etch.Core.Palette;

/// <summary>One row of the palette.</summary>
/// <param name="Transform">The transform this row runs.</param>
/// <param name="IsSuggested">
/// True when the transform applies to what the buffer was detected as. Shown as a
/// marker so the ordering is explicable rather than mysterious.
/// </param>
/// <param name="Score">The ranking score. Higher is better; only meaningful within one ranking.</param>
public readonly record struct PaletteEntry(ITransform Transform, bool IsSuggested, int Score);

/// <summary>
/// Decides what the palette shows and in what order.
/// </summary>
/// <remarks>
/// <para>
/// The plan's rule, in order: what applies to the detected format, then what was used
/// recently, then how well the query matches. The point is stated in section 9 and is
/// worth restating here because every part of this file serves it: <b>the right
/// answer should be the first row before anything is typed.</b>
/// </para>
/// <para>
/// A transform that does not apply to the detected format is still listed. Detection
/// is a guess and the user is not: someone who wants to base64-encode a JSON document
/// is not wrong, they are just doing something the buffer could not have predicted.
/// </para>
/// </remarks>
public static class PaletteRanking
{
    /// <summary>Outranks everything. Applicability is the first sort key, not a nudge.</summary>
    private const int SuggestedBonus = 10_000;

    /// <summary>Recency outranks fuzzy quality but never applicability.</summary>
    private const int RecencyBonus = 1_000;

    /// <summary>How many recently-used transforms carry a bonus.</summary>
    public const int RecencyDepth = 8;

    /// <summary>
    /// Ranks the registry against a query and a detection.
    /// </summary>
    /// <param name="query">What was typed. Empty lists everything.</param>
    /// <param name="detection">What the buffer was detected as.</param>
    /// <param name="recentIds">
    /// Transform ids most recently used, newest first. Null or empty is normal: it is
    /// what a fresh session looks like.
    /// </param>
    public static IReadOnlyList<PaletteEntry> Rank(
        string? query,
        in DetectionResult detection,
        IReadOnlyList<string>? recentIds = null)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        var entries = new List<PaletteEntry>(TransformRegistry.All.Count);

        foreach (var transform in TransformRegistry.All)
        {
            if (!FuzzyMatch.TryScoreAny(transform.Name, transform.Aliases, trimmed, out var score))
            {
                continue;
            }

            var suggested = transform.IsAvailable(detection);

            if (suggested)
            {
                score += SuggestedBonus;
            }

            score += RecencyScore(transform.Id, recentIds);

            entries.Add(new PaletteEntry(transform, suggested, score));
        }

        // Score, then declared precedence, then name.
        //
        // Precedence sits in the middle because an exact score tie is the one case where
        // the order is otherwise an accident of the alphabet, and on a fresh session that
        // tie is what Ctrl+Enter resolves. Name stays as the last word so the list is
        // stable between openings: a palette whose rows move when nothing changed is one
        // people stop trusting to muscle memory, which is most of what a palette is for.
        entries.Sort(static (left, right) =>
        {
            if (right.Score != left.Score)
            {
                return right.Score.CompareTo(left.Score);
            }

            var precedence = left.Transform.Precedence.CompareTo(right.Transform.Precedence);

            return precedence != 0
                ? precedence
                : string.CompareOrdinal(left.Transform.Name, right.Transform.Name);
        });

        return entries;
    }

    /// <summary>
    /// The transform <c>Ctrl+Enter</c> should run, or null when there is nothing obvious.
    /// </summary>
    /// <remarks>
    /// Only ever something that applies to the detected format. This key does its job
    /// without showing the user what it is about to do, so it has to be the obvious
    /// action or no action: running the highest-scoring transform in a list nobody
    /// looked at would be a keystroke that reformats a buffer at random.
    /// </remarks>
    public static ITransform? Suggested(in DetectionResult detection, IReadOnlyList<string>? recentIds = null)
    {
        var top = SuggestedTop(detection, recentIds, count: 1);

        return top.Count > 0 ? top[0] : null;
    }

    /// <summary>
    /// The transforms that apply to the detected format, best first, at most
    /// <paramref name="count"/> of them. Empty when there is nothing obvious.
    /// </summary>
    /// <param name="detection">What the buffer was detected as.</param>
    /// <param name="recentIds">Transform ids most recently used, newest first.</param>
    /// <param name="count">How many to return. Zero or fewer returns empty.</param>
    /// <remarks>
    /// <para>
    /// The generalisation of <see cref="Suggested"/>, and the reason it exists is that
    /// "which transforms are ready" now has three consumers: <c>Ctrl+Enter</c>, the green
    /// markers in the palette, and the editor's right-click menu. A rule written in more
    /// than one place will disagree, and the copy under test is not necessarily the copy
    /// that ships, so the rule lives here and the menu asks rather than re-deriving it by
    /// filtering <see cref="Rank"/> at the call site.
    /// </para>
    /// <para>
    /// The scan stops at the first row that is not suggested rather than filtering the
    /// whole ranking. With an empty query every fuzzy score is zero, so
    /// <see cref="SuggestedBonus"/> (10,000) cannot be overtaken by the largest possible
    /// recency bonus (8,000) and the suggested entries are exactly the leading run: the
    /// two readings agree today. Stopping is still the one to write down, because it is
    /// what keeps <see cref="Suggested"/> returning null in the case its own contract
    /// names: the top-ranked entry is not itself suggested.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ITransform> SuggestedTop(
        in DetectionResult detection,
        IReadOnlyList<string>? recentIds = null,
        int count = 4)
    {
        if (count <= 0 || !detection.IsRecognised)
        {
            return [];
        }

        var ranked = Rank(query: null, detection, recentIds);
        var top = new List<ITransform>(Math.Min(count, ranked.Count));

        foreach (var entry in ranked)
        {
            if (!entry.IsSuggested)
            {
                break;
            }

            top.Add(entry.Transform);

            if (top.Count == count)
            {
                break;
            }
        }

        return top;
    }

    /// <summary>
    /// Adds <paramref name="id"/> to the front of a recency list, without duplicates.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the application layer because it is the other half of
    /// the ranking rule, and splitting a rule across two projects is how the two halves
    /// come to disagree.
    /// </remarks>
    public static IReadOnlyList<string> Remember(IReadOnlyList<string>? recentIds, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var updated = new List<string>(RecencyDepth) { id };

        if (recentIds is not null)
        {
            foreach (var existing in recentIds)
            {
                if (updated.Count == RecencyDepth)
                {
                    break;
                }

                if (!string.Equals(existing, id, StringComparison.Ordinal))
                {
                    updated.Add(existing);
                }
            }
        }

        return updated;
    }

    /// <summary>Scores a transform by how recently it was used.</summary>
    private static int RecencyScore(string id, IReadOnlyList<string>? recentIds)
    {
        if (recentIds is null)
        {
            return 0;
        }

        var depth = Math.Min(recentIds.Count, RecencyDepth);

        for (var i = 0; i < depth; i++)
        {
            if (string.Equals(recentIds[i], id, StringComparison.Ordinal))
            {
                // Linear decay, so the most recent is worth eight times the oldest
                // remembered one rather than all of them being equally "recent".
                return RecencyBonus * (depth - i);
            }
        }

        return 0;
    }
}
