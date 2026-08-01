using Etch.Core.Abstractions;

namespace Etch.Core.Transforms.Data;

/// <summary>
/// Re-indents a JSON document.
/// </summary>
/// <remarks>
/// The transform the whole product is an argument for: paste a minified payload, press
/// <c>Ctrl+Enter</c>, read it. Note that it drops comments, because the parser is
/// configured to skip them — the alternative is refusing to format the config files
/// people most want formatted.
/// </remarks>
internal sealed class FormatJson : ITransform
{
    /// <inheritdoc />
    public string Id => "json.format";

    /// <inheritdoc />
    public string Name => "Format JSON";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Data;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["pretty", "prettify", "beautify", "indent", "expand"];

    /// <inheritdoc />
    /// <remarks>
    /// Claims the suggested slot for JSON outright. It held it before this property
    /// existed, but only because "Format JSON" sorts ahead of "Minify JSON" — the right
    /// answer for the wrong reason, and one a differently-named transform would have taken
    /// away silently.
    /// </remarks>
    public int Precedence => -100;

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => detection.Is(FormatId.Json);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default) =>
        JsonRewriter.Rewrite(
            input.Text,
            input.Options,
            indented: true,
            static (element, writer) => element.WriteTo(writer));
}

/// <summary>Strips every optional byte from a JSON document.</summary>
internal sealed class MinifyJson : ITransform
{
    /// <inheritdoc />
    public string Id => "json.minify";

    /// <inheritdoc />
    public string Name => "Minify JSON";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Data;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["compact", "collapse", "compress", "one line"];

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => detection.Is(FormatId.Json);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default) =>
        JsonRewriter.Rewrite(
            input.Text,
            input.Options,
            indented: false,
            static (element, writer) => element.WriteTo(writer));
}

/// <summary>
/// Rewrites a JSON document with every object's keys in ordinal order.
/// </summary>
/// <remarks>
/// Almost always done so that two documents can be diffed against each other, which is
/// why the output is indented: a sorted single line is not something anyone compares.
/// </remarks>
internal sealed class SortJsonKeys : ITransform
{
    /// <inheritdoc />
    public string Id => "json.sortKeys";

    /// <inheritdoc />
    public string Name => "Sort JSON keys";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Data;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["order keys", "alphabetise", "alphabetize", "canonicalise"];

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => detection.Is(FormatId.Json);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default) =>
        JsonRewriter.Rewrite(
            input.Text,
            input.Options,
            indented: true,
            static (element, writer) => JsonRewriter.WriteSorted(element, writer));
}
