using Etch.Core.Documents;
using Etch.Core.Text;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Etch.App.Editor;

/// <summary>
/// Owns the editor's highlighting and folding, and the size rules that switch them off.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DocumentCapabilities"/> has carried <c>SyntaxHighlighting</c>,
/// <c>Folding</c> and <c>DetectOnEdit</c> since M1, computed on every load and then read by
/// nothing at all — a degradation model with tests and no effect. This is the place they
/// take effect, and gathering all three here rather than scattering three checks through
/// <c>MainWindow</c> is what stops a fourth capability arriving later and being honoured in
/// two places out of three.
/// </para>
/// <para>
/// <b>Nothing here runs at startup.</b> §4's checklist is explicit that highlighting
/// definitions must not be constructed at boot, and this obeys it by never touching
/// <see cref="HighlightingManager"/> until a document that wants a grammar is bound —
/// which for a launch into an empty scratch tab is never. Worth knowing when reading a
/// startup timeline: in a <c>Debug</c> build AvalonEdit parses <em>all twenty-one</em>
/// grammars the first time <c>HighlightingManager.Instance</c> is touched, because its
/// registration is wrapped in <c>#if DEBUG</c> to surface a broken grammar immediately. In
/// <c>Release</c> — what ships, and what the measurements in <c>docs/M0-measurement.md</c>
/// are taken from — registration is lazy and only the grammar actually asked for is parsed.
/// So the first file opened looks dramatically more expensive under a debugger than it is.
/// </para>
/// </remarks>
internal sealed class EditorSyntax : IDisposable
{
    /// <summary>
    /// Grammar names as AvalonEdit registers them.
    /// </summary>
    /// <remarks>
    /// By name, never by extension. <c>GetDefinitionByExtension(".md")</c> answers
    /// <c>MarkDownWithFontSize</c>, because AvalonEdit registers <c>.md</c> twice and the
    /// later registration wins — and that variant scales heading text, which in a
    /// fixed-width editor looks like a rendering fault. The names are verified against
    /// <c>Resources.RegisterBuiltInHighlightings</c> rather than guessed; a wrong one is
    /// silent, since <c>GetDefinition</c> returns null and the document simply renders
    /// plain.
    /// </remarks>
    private static readonly Dictionary<SyntaxLanguage, string> GrammarNames = new()
    {
        [SyntaxLanguage.Json] = "Json",
        [SyntaxLanguage.Xml] = "XML",
        [SyntaxLanguage.Html] = "HTML",
        [SyntaxLanguage.Css] = "CSS",
        [SyntaxLanguage.JavaScript] = "JavaScript",
        [SyntaxLanguage.CSharp] = "C#",
        [SyntaxLanguage.Cpp] = "C++",
        [SyntaxLanguage.Java] = "Java",
        [SyntaxLanguage.Python] = "Python",
        [SyntaxLanguage.PowerShell] = "PowerShell",
        [SyntaxLanguage.Markdown] = "MarkDown",
        [SyntaxLanguage.Sql] = "TSQL",
        [SyntaxLanguage.Php] = "PHP",
        [SyntaxLanguage.VisualBasic] = "VB",
        [SyntaxLanguage.Patch] = "Patch",
    };

    /// <summary>
    /// Languages <see cref="BraceFolding"/> is actually correct for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closed list, and it is exactly the set whose lexical model <c>BraceFolding</c>
    /// documents: double- and single-quoted strings with backslash escapes, <c>//</c> to
    /// end of line, <c>/* */</c> across lines. Nothing is here because it happens to
    /// contain brackets.
    /// </para>
    /// <para>
    /// <b>Three languages are deliberately absent although they use braces.</b> PowerShell
    /// comments with <c>#</c>, escapes with a backtick and has here-strings, so
    /// <c># {</c> in a comment would push a bracket that a later real <c>}</c> closes —
    /// a fold spanning the wrong region, which is the exact failure <c>Close</c> refuses to
    /// produce by other means. CSS has no <c>//</c> comment, so the <c>//</c> in
    /// <c>url(http://…)</c> would hide the rest of that line including a closing brace.
    /// PHP accepts <c>#</c> comments alongside <c>//</c> and loses the same way PowerShell
    /// does. All three still get highlighting; they get no fold margin, which is the same
    /// answer Python and Markdown get and an honest one. Folding them needs a per-language
    /// comment convention, which is a bigger change than adding a name to this list.
    /// </para>
    /// </remarks>
    private static readonly HashSet<SyntaxLanguage> BraceFolded =
    [
        SyntaxLanguage.Json,
        SyntaxLanguage.JavaScript,
        SyntaxLanguage.CSharp,
        SyntaxLanguage.Cpp,
        SyntaxLanguage.Java,
    ];

    private readonly TextEditor _editor;

    private ThemedHighlightingColorizer? _colorizer;
    private FoldingManager? _folding;

    /// <summary>The document <see cref="_folding"/> was installed against.</summary>
    /// <remarks>
    /// Held rather than re-read from the editor, because the point is to notice when the
    /// editor's document is no longer the one the manager is bound to.
    /// </remarks>
    private TextDocument? _foldingDocument;

    private SyntaxLanguage _language = SyntaxLanguage.None;
    private bool _dark;
    private bool _disposed;

    internal EditorSyntax(TextEditor editor, bool dark)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _dark = dark;
    }

    /// <summary>Whether format detection should re-run as the user types.</summary>
    /// <remarks>
    /// Read by the window's edit handler. False above the reduced-size threshold, where a
    /// detection pass per debounce is a scan the user did not ask for on a buffer big enough
    /// to notice.
    /// </remarks>
    internal bool DetectOnEdit { get; private set; } = true;

    /// <summary>The language currently applied, for the status bar and for tests.</summary>
    internal SyntaxLanguage Language => _language;

    /// <summary>Whether a fold margin is currently installed.</summary>
    internal bool IsFolding => _folding is not null;

    /// <summary>
    /// Applies the highlighting and folding a tab should have.
    /// </summary>
    /// <param name="language">The language chosen by <see cref="LanguageSelector"/>.</param>
    /// <param name="capabilities">What the document's size permits.</param>
    /// <remarks>
    /// Called on every bind and again whenever detection changes its mind about a scratch
    /// tab. Idempotent, and cheap when nothing has changed — which matters because the
    /// second of those happens on a debounce timer.
    /// </remarks>
    internal void Apply(SyntaxLanguage language, DocumentCapabilities capabilities)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DetectOnEdit = capabilities.DetectOnEdit;

        // The size rule collapses onto the language: a document too large to highlight is a
        // document with no language, so there is one path through the rest of this rather
        // than a size check beside every step.
        var effective = capabilities.SyntaxHighlighting ? language : SyntaxLanguage.None;

        if (effective != _language)
        {
            _language = effective;
            InstallHighlighting(effective);
        }

        // Only when something actually moved. This is called again on every detection
        // result for a scratch tab — a 150 ms debounce while somebody types — and
        // RefreshFoldings materialises the whole document to scan it, so refreshing
        // unconditionally would put a full copy of the buffer on the heap several times a
        // second for an answer that had not changed.
        if (InstallFolding(capabilities.Folding ? effective : SyntaxLanguage.None))
        {
            RefreshFoldings();
        }
    }

    /// <summary>Recolours for a theme change.</summary>
    /// <returns>True when the palette changed and the editor needs a repaint.</returns>
    internal bool Retheme(bool dark)
    {
        _dark = dark;

        return _colorizer?.Retheme(dark) == true;
    }

    /// <summary>
    /// Tears the fold margin down, which must happen <b>before</b> the editor's document
    /// is replaced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a tidiness call — skipping it is a crash. AvalonEdit's own documentation on
    /// <see cref="FoldingManager.Install"/> says the manager must be uninstalled before the
    /// text area is bound to a different document, and the reason is that
    /// <c>TextView.OnDocumentChanged</c> disposes the height tree, setting its root to null.
    /// Uninstalling afterwards walks <c>Clear</c> → <c>FoldingSection.IsFolded = false</c> →
    /// <c>CollapsedLineSection.Uncollapse</c> → <c>HeightTree.GetNodeByIndex</c>, which
    /// dereferences that null root. The assertion that would catch it is <c>Debug</c>-only.
    /// </para>
    /// <para>
    /// The setter only fires for a section that is actually collapsed, so the repro is:
    /// collapse a fold, then switch tabs. That makes it a crash a casual pass over the
    /// feature would never see, on the UI thread, unhandled.
    /// </para>
    /// <para>
    /// Highlighting deliberately does <em>not</em> need the same treatment.
    /// <c>HighlightingColorizer</c> subscribes to <c>TextView.DocumentChanged</c> and
    /// re-registers itself, so the colorizer survives the swap on its own.
    /// </para>
    /// </remarks>
    internal void DetachFolding()
    {
        if (_folding is not { } existing)
        {
            return;
        }

        FoldingManager.Uninstall(existing);

        _folding = null;
        _foldingDocument = null;
    }

    /// <summary>
    /// Recomputes fold regions from the current text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan runs on the thread pool over an immutable snapshot, which is the affordance
    /// AvalonEdit's document exists to provide and the same shape <c>DetectionService</c>
    /// uses. It matters more here than there: folding is offered up to two mebibytes, and
    /// materialising that buffer is a four-megabyte allocation on the large object heap.
    /// Doing it on the dispatcher would be a visible stall in the middle of typing.
    /// </para>
    /// <para>
    /// The result is discarded unless the document, the manager and the <em>version</em> are
    /// all still the ones scanned. Version as well as identity, because two scans can
    /// overlap and nothing makes them finish in order — and fold offsets applied to a
    /// document that has moved on address the wrong text, which shows up as a fold margin
    /// pointing at nothing in particular rather than as an error.
    /// </para>
    /// <para>
    /// XML stays synchronous. <c>XmlFoldingStrategy</c> takes the manager and the live
    /// document together and there is no snapshot-shaped overload of it, so moving it off
    /// the dispatcher would mean touching a <c>TextDocument</c> from another thread — which
    /// AvalonEdit forbids outright. It reads through <c>document.CreateReader()</c> rather
    /// than materialising the text, so the allocation this method exists to move is not one
    /// it makes.
    /// </para>
    /// </remarks>
    internal void RefreshFoldings()
    {
        if (_folding is not { } manager || _foldingDocument is not { } document)
        {
            return;
        }

        // The manager is bound to _foldingDocument; scanning what the editor happens to be
        // showing would coerce offsets from one document onto another. Every current path
        // keeps the two equal — this is here so that a future one cannot quietly not.
        if (!ReferenceEquals(_editor.Document, document))
        {
            return;
        }

        if (_language is SyntaxLanguage.Xml or SyntaxLanguage.Html)
        {
            new XmlFoldingStrategy().UpdateFoldings(manager, document);
            return;
        }

        // Taken on the UI thread, which owns the document; everything after this point works
        // on the immutable snapshot and is safe anywhere.
        var snapshot = document.CreateSnapshot();

        _ = Task.Run(() => BraceFolding.Scan(snapshot.Text)).ContinueWith(
            task =>
            {
                if (task.IsFaulted || _disposed)
                {
                    return;
                }

                if (!ReferenceEquals(_folding, manager) || !ReferenceEquals(_foldingDocument, document))
                {
                    return;
                }

                if (snapshot.Version is not { } scanned
                    || !scanned.BelongsToSameDocumentAs(document.Version)
                    || scanned.CompareAge(document.Version) != 0)
                {
                    return;
                }

                manager.UpdateFoldings(
                    task.Result.Select(static region =>
                        new NewFolding(region.StartOffset, region.EndOffset) { Name = region.Label }),
                    firstErrorOffset: -1);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        InstallHighlighting(SyntaxLanguage.None);
        DetachFolding();
    }

    private void InstallHighlighting(SyntaxLanguage language)
    {
        if (_colorizer is not null)
        {
            _editor.TextArea.TextView.LineTransformers.Remove(_colorizer);
            _colorizer = null;
        }

        if (Grammar(language) is not { } definition)
        {
            return;
        }

        _colorizer = new ThemedHighlightingColorizer(definition, _dark);

        // Index 0, matching what TextEditor.OnSyntaxHighlightingChanged does with the
        // colorizer it would have installed. Etch has no other line transformer, so the
        // position is not currently load-bearing — it is here so that adding one later
        // inherits the ordering AvalonEdit itself chose rather than an accident.
        //
        // TextEditor.SyntaxHighlighting stays null throughout: setting it would install a
        // second colorizer, in the grammar's own light-theme colours, on top of this one.
        _editor.TextArea.TextView.LineTransformers.Insert(0, _colorizer);
    }

    /// <summary>
    /// Puts the fold margin where it belongs for this language and this document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The document is part of the condition, not just the language.</b>
    /// <c>FoldingManager.Install</c> binds to <c>textArea.Document</c> as it is at that
    /// moment — <c>FoldingManagerInstallation</c> passes it to the base constructor — and
    /// Etch has one editor whose <c>Document</c> is swapped on every tab switch. A manager
    /// left installed across that swap goes on holding folds belonging to the previous
    /// buffer, and the offsets in them address a document it is no longer looking at. Two
    /// JSON tabs are exactly the case where the language does not change and everything
    /// else does.
    /// </para>
    /// <para>
    /// <b>A consequence worth stating: collapsed folds do not survive a tab switch.</b>
    /// Rebuilding the manager discards which sections were folded, so returning to a tab
    /// finds it fully expanded. That is a real cost of one editor serving many documents,
    /// and the alternative — a manager per tab — trades it for an object graph per open
    /// file, which is the memory the single-editor design exists to avoid. Restoring the
    /// collapsed set would mean persisting it beside the caret and scroll position in
    /// <c>BufferTab</c>, which is a session-schema change and not attempted here.
    /// </para>
    /// </remarks>
    /// <returns>True when the installation changed, so the caller knows to recompute.</returns>
    private bool InstallFolding(SyntaxLanguage language)
    {
        var document = _editor.Document;

        var wanted = document is not null
            && (language == SyntaxLanguage.Xml
                || language == SyntaxLanguage.Html
                || BraceFolded.Contains(language));

        if (wanted && _folding is not null && ReferenceEquals(_foldingDocument, document))
        {
            return false;
        }

        if (!wanted && _folding is null)
        {
            return false;
        }

        DetachFolding();

        if (!wanted)
        {
            return true;
        }

        _folding = FoldingManager.Install(_editor.TextArea);
        _foldingDocument = document;

        return true;
    }

    /// <summary>
    /// The grammar for a language, or null when there is none to apply.
    /// </summary>
    /// <remarks>
    /// A missing grammar is not an error worth reporting. It means this build of AvalonEdit
    /// does not carry that definition, and the honest response is an unhighlighted document
    /// rather than a message about a library the user did not choose.
    /// </remarks>
    private static IHighlightingDefinition? Grammar(SyntaxLanguage language) =>
        GrammarNames.TryGetValue(language, out var name)
            ? HighlightingManager.Instance.GetDefinition(name)
            : null;
}
