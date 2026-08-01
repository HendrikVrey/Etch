using Etch.Core.Abstractions;

namespace Etch.App.Palette;

/// <summary>One row in the command palette, shaped for the list to bind to.</summary>
/// <param name="Transform">The transform this row runs.</param>
/// <param name="Name">The display name.</param>
/// <param name="Detail">The category, and the aliases that matched, for the second line.</param>
/// <param name="IsSuggested">
/// True when this applies to what the buffer was detected as. Shown as a marker, so
/// that the ordering has a visible reason rather than looking arbitrary.
/// </param>
/// <remarks>
/// <para>
/// A separate type from <c>PaletteEntry</c> so that <c>Etch.Core</c> owns the ranking
/// and knows nothing about how it is displayed. The alternative — binding the list
/// straight to the core type — would put display concerns into the project the whole
/// architecture exists to keep free of them.
/// </para>
/// <para>
/// Public rather than internal, and that is XAML's requirement rather than a choice:
/// the window exposes these through a public collection property, and a public member
/// exposing an internal type is CS0053.
/// </para>
/// </remarks>
public sealed record PaletteRow(ITransform Transform, string Name, string Detail, bool IsSuggested)
{
    /// <summary>Builds a row from a ranked entry.</summary>
    public static PaletteRow From(ITransform transform, bool isSuggested) =>
        new(transform, transform.Name, Describe(transform.Category), isSuggested);

    private static string Describe(TransformCategory category) => category switch
    {
        TransformCategory.Data => "Data",
        TransformCategory.Encoding => "Encoding",
        TransformCategory.Identity => "Identity",
        TransformCategory.Time => "Time",
        TransformCategory.Text => "Text",
        _ => string.Empty,
    };
}
