using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Etch.App.Editor;
using Etch.Core.Documents;
using Etch.Core.Text;
using Etch.Persistence.Model;
using Etch.Persistence.Storage;
using ICSharpCode.AvalonEdit.Document;

namespace Etch.App.Tabs;

/// <summary>
/// One tab: its identity, its metadata, and, once hydrated, its text.
/// </summary>
/// <remarks>
/// <para>
/// A tab exists before its text does. Startup restores twenty tabs' worth of
/// metadata and reads exactly one buffer file, because rendering the active tab is
/// what the user is waiting for and the other nineteen can arrive while they read
/// it. <see cref="Document"/> is therefore null until <see cref="Hydrate"/> runs, and
/// every consumer has to cope with that rather than assume it away.
/// </para>
/// <para>
/// Owned by the UI thread. The one thing that crosses threads is the snapshot handed
/// to the journal, which is immutable by construction.
/// </para>
/// <para>
/// Public rather than internal because the tab strip's <c>DataTemplate</c> names it in
/// XAML and <c>MainWindow</c> exposes the collection for binding. Its mutating members
/// stay internal, so the workspace remains the only thing that can change a tab's
/// state.
/// </para>
/// </remarks>
public sealed class BufferTab : INotifyPropertyChanged
{
    private string _title;
    private bool _isActive;
    private bool _isRenaming;
    private bool _isPinned;
    private bool _isEphemeral;
    private TextDocument? _document;
    private LongLineWatch? _longLines;

    private BufferTab(BufferRecord record)
    {
        Id = record.Id;
        Kind = record.Kind;
        FilePath = record.FilePath;
        _title = record.Title;
        _isPinned = record.IsPinned;
        CaretOffset = record.CaretOffset;
        FirstVisibleLine = record.FirstVisibleLine;
        FormatOverride = record.FormatOverride;
        LastModifiedUtc = record.LastModifiedUtc;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Identifies the buffer, and names its file under Etch's data directory.</summary>
    public BufferId Id { get; }

    /// <summary>Whether Etch owns this buffer or the user does.</summary>
    public BufferKind Kind { get; private set; }

    /// <summary>The user's file, for a <see cref="BufferKind.File"/> tab.</summary>
    public string? FilePath { get; private set; }

    /// <summary>
    /// Which file this tab is actually over, independent of how its path is spelled.
    /// </summary>
    /// <remarks>
    /// Not restored from the session index, and deliberately so: an identity is a
    /// statement about the file that is on disk right now, and the one recorded last
    /// week may since have been deleted and recreated. It is established when the file
    /// is opened and left unknown until then, so a restored tab de-duplicates on its
    /// path: a weaker guarantee, honestly held, rather than a stale strong one.
    /// </remarks>
    internal FileIdentity Identity { get; private set; }

    /// <summary>
    /// What the file looked like when Etch last read or wrote it, or null if unknown.
    /// </summary>
    /// <remarks>
    /// Null after a session restore for the same reason <see cref="Identity"/> is
    /// unknown, and the save path treats null as "cannot tell" rather than as
    /// "unchanged".
    /// </remarks>
    internal FileWitness? Witness { get; private set; }

    /// <summary>The tab caption.</summary>
    public string Title
    {
        get => _title;
        set
        {
            var trimmed = value?.Trim();

            // A blank title would fail BufferRecord's invariant at the next session
            // save and take the whole index down with it, so an empty rename is
            // simply not a rename.
            if (string.IsNullOrEmpty(trimmed) || string.Equals(_title, trimmed, StringComparison.Ordinal))
            {
                return;
            }

            _title = trimmed;
            Raise();
        }
    }

    /// <summary>
    /// Whether this is the tab in front.
    /// </summary>
    /// <remarks>
    /// Held on the tab rather than derived in the view, because the tab strip is an
    /// items control and an item has no cheap way to compare itself against a property
    /// of the window. Owned by <see cref="Workspace"/>, which is the only thing allowed
    /// to have an opinion about which tab is active.
    /// </remarks>
    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            Raise();
        }
    }

    /// <summary>
    /// Whether the tab's caption is currently being edited in place.
    /// </summary>
    /// <remarks>
    /// State on the tab rather than a dialog, because the plan bans modals and a rename
    /// prompt is exactly the kind of small interruption that adds up to an application
    /// which feels heavy. The tab strip's template swaps the caption for a text box
    /// while this is set.
    /// </remarks>
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value)
            {
                return;
            }

            _isRenaming = value;
            Raise();
        }
    }

    /// <summary>
    /// Whether the tab is pinned to the front of the strip.
    /// </summary>
    /// <remarks>
    /// Set through <see cref="Workspace.SetPinned"/> rather than here, for the same reason
    /// <see cref="IsEphemeral"/> is: pinning has to move the tab into its new group, and a
    /// property setter that silently reorders the collection it lives in is a setter that
    /// will eventually be called from somewhere that did not expect it to.
    /// </remarks>
    public bool IsPinned
    {
        get => _isPinned;
        internal set
        {
            if (_isPinned == value)
            {
                return;
            }

            _isPinned = value;
            Raise();
        }
    }

    /// <summary>
    /// Whether this tab's text is forbidden from ever reaching the disk.
    /// </summary>
    /// <remarks>
    /// Set through <see cref="Workspace.SetEphemeral"/> rather than here, because
    /// turning it on has to suppress the journal and delete anything already written,
    /// and a property setter that quietly performs I/O is how that ends up being
    /// called from somewhere it should not be.
    /// </remarks>
    public bool IsEphemeral
    {
        get => _isEphemeral;
        internal set
        {
            if (_isEphemeral == value)
            {
                return;
            }

            _isEphemeral = value;
            Raise();
            Raise(nameof(IsJournaled));
        }
    }

    /// <summary>The text, or null until the tab has been hydrated.</summary>
    public TextDocument? Document
    {
        get => _document;
        private set
        {
            _document = value;
            Raise();
            Raise(nameof(IsHydrated));
        }
    }

    /// <summary>Whether the text has been read from disk yet.</summary>
    public bool IsHydrated => _document is not null;

    /// <summary>Caret position, restored on hydration and maintained by the editor thereafter.</summary>
    public int CaretOffset { get; set; }

    /// <summary>Scroll position as a one-based line number.</summary>
    public int FirstVisibleLine { get; set; }

    /// <summary>A manually pinned format, or null to trust detection. Unused until M2.</summary>
    public string? FormatOverride { get; set; }

    /// <summary>When the text last changed.</summary>
    public DateTimeOffset LastModifiedUtc { get; set; }

    /// <summary>What the editor may switch on for a document this size.</summary>
    /// <remarks>
    /// Re-derived as the text changes (see <see cref="Workspace"/>), from the policy the tab
    /// was opened under. A scratch tab is born empty, and when this was set once and never
    /// again, everything later pasted into it was treated as though it were still empty.
    /// </remarks>
    public DocumentCapabilities Capabilities { get; private set; } =
        DocumentSizePolicy.Default.Evaluate(0);

    /// <summary>The thresholds in force when this tab was opened.</summary>
    /// <remarks>
    /// Kept rather than read from the workspace each time, which is the promise
    /// <see cref="Workspace.ApplySettings"/> makes: a changed threshold applies to the next
    /// document opened, and a tab somebody is typing into does not change under them.
    /// </remarks>
    internal DocumentSizePolicy SizePolicy { get; private set; } = DocumentSizePolicy.Default;

    /// <summary>
    /// Whether a line is longer than <see cref="DocumentSizePolicy.LongLineLength"/>.
    /// </summary>
    internal bool HasLongLines => _longLines?.Any == true;

    /// <summary>
    /// Roughly how many bytes the text would take on disk, for the size policy.
    /// </summary>
    /// <remarks>
    /// Characters times the width of one in this tab's encoding: exact for ASCII in UTF-8,
    /// which is what large pasted data almost always is, and a lower bound otherwise.
    /// Encoding the whole buffer to measure it would be the very cost the policy exists to
    /// avoid, and this is read on every keystroke.
    /// </remarks>
    internal long EstimatedBytes => EstimateBytes(_document?.TextLength ?? 0);

    /// <summary>The estimate <see cref="EstimatedBytes"/> makes, for a length the text is not yet.</summary>
    internal long EstimateBytes(long characters) => characters * Encoding.CodePage switch
    {
        1200 or 1201 => 2,
        12000 or 12001 => 4,
        _ => 1,
    };

    /// <summary>
    /// Whether this tab may grow to <paramref name="characters"/> characters.
    /// </summary>
    /// <param name="characters">Its length afterwards.</param>
    /// <param name="refusal">Why not, in a sentence for the status bar.</param>
    /// <remarks>
    /// <para>
    /// The ceiling a file is refused at when it is opened, applied to text arriving by every
    /// other route: a paste, a transform's result, a replace-all. Only opening was checked,
    /// so a paste of any size went straight in, and a scratch tab a few hundred megabytes
    /// long is several copies of that in memory at once (the document, its undo history, the
    /// journal's snapshot as it is written) on a machine that may not have them to give.
    /// </para>
    /// <para>
    /// A journaled tab is also held to what <see cref="BufferStore"/> reads back at startup.
    /// Past that the text is restored cut short, safely but not whole, so a tab is not
    /// allowed to get there in the first place.
    /// </para>
    /// </remarks>
    internal bool Fits(long characters, [NotNullWhen(false)] out string? refusal)
    {
        var bytes = EstimateBytes(characters);
        var ceiling = SizePolicy.HardCeiling;

        if (bytes > ceiling)
        {
            refusal = $"That would make {Title} {DocumentSizePolicy.Describe(bytes)}, over the {DocumentSizePolicy.Describe(ceiling)} editor limit.";
            return false;
        }

        if (IsJournaled && characters > BufferStore.MaxBufferChars)
        {
            refusal = string.Create(
                CultureInfo.CurrentCulture,
                $"That would make {Title} longer than the {BufferStore.MaxBufferChars:N0} characters Etch can bring back after a restart.");
            return false;
        }

        refusal = null;
        return true;
    }

    /// <summary>The encoding to write back with, for a file tab.</summary>
    /// <remarks>
    /// Captured at load and preserved on save. Rewriting someone's UTF-16 configuration
    /// file as UTF-8 because Etch prefers it is a silent, tool-breaking change.
    /// </remarks>
    public Encoding Encoding { get; private set; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The newline convention seen when the text was loaded.</summary>
    public LineEndingStyle LineEnding { get; private set; } = LineEndingStyle.Crlf;

    /// <summary>
    /// True when the text was too large to read in full.
    /// </summary>
    /// <remarks>
    /// A truncated buffer must never be journaled: writing it back is what would turn
    /// a partial read into permanent loss of the tail.
    /// </remarks>
    public bool WasTruncated { get; private set; }

    /// <summary>True when the text on screen came from the retained previous generation.</summary>
    public bool RecoveredFromBackup { get; private set; }

    /// <summary>Whether edits to this tab are written to Etch's data directory.</summary>
    public bool IsJournaled => Capabilities.Journaling && !WasTruncated && !IsEphemeral;

    /// <summary>Creates a brand-new, empty scratch tab.</summary>
    /// <param name="title">The caption.</param>
    /// <param name="now">When it was made.</param>
    /// <param name="policy">The size thresholds in force, which the tab keeps.</param>
    public static BufferTab NewScratch(string title, DateTimeOffset now, DocumentSizePolicy policy)
    {
        var tab = new BufferTab(BufferRecord.NewScratch(title, now));

        // A new tab has no text to read, so it is born hydrated. Leaving it otherwise
        // would make Ctrl+N asynchronous for no reason and put a read of a file that
        // does not exist on the fastest path in the product.
        tab.Hydrate(string.Empty, policy, sizeInBytes: 0, journaling: true, wasTruncated: false, recoveredFromBackup: false);

        return tab;
    }

    /// <summary>Rebuilds a tab from a restored session record.</summary>
    public static BufferTab FromRecord(BufferRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new BufferTab(record);
    }

    /// <summary>
    /// Installs the text, making the tab usable.
    /// </summary>
    /// <param name="text">The contents read from disk.</param>
    /// <param name="policy">The size thresholds in force, which the tab keeps.</param>
    /// <param name="sizeInBytes">The size the text was read at, as the policy measures it.</param>
    /// <param name="journaling">
    /// Whether the text is written to Etch's own storage. Decided by whoever read it and
    /// never re-derived here: see <see cref="DocumentSizePolicy.Reassess"/>.
    /// </param>
    /// <param name="wasTruncated">Whether the read hit the size cap.</param>
    /// <param name="recoveredFromBackup">Whether the text came from the retained generation.</param>
    /// <remarks>
    /// Idempotent by refusal rather than by repetition: hydrating twice would replace
    /// a document the user may already have typed into, and would drop its undo
    /// history on the floor.
    /// </remarks>
    public void Hydrate(
        string text,
        DocumentSizePolicy policy,
        long sizeInBytes,
        bool journaling,
        bool wasTruncated,
        bool recoveredFromBackup)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(policy);

        if (IsHydrated)
        {
            return;
        }

        WasTruncated = wasTruncated;
        RecoveredFromBackup = recoveredFromBackup;
        LineEnding = LineEndings.Detect(text);

        // Constructed from a StringTextSource rather than assigned through the
        // editor's Text property: the latter routes the whole buffer through a
        // replace operation and lands it on the undo stack, so the user's first
        // Ctrl+Z would empty their restored tab.
        var document = new TextDocument(new StringTextSource(text));

        // Before the capabilities, which depend on it, and before the document is handed to
        // anything that could draw it: a restored tab holding one 3 MB line was otherwise
        // highlighted on the first frame of every launch, and never drew that frame.
        _longLines = new LongLineWatch(document, DocumentSizePolicy.LongLineLength);

        SizePolicy = policy;
        Capabilities = policy.Reassess(sizeInBytes, _longLines.Any, journaling);

        // A restored caret from a hand-edited or stale index can point past the end.
        CaretOffset = Math.Clamp(CaretOffset, 0, document.TextLength);
        FirstVisibleLine = Math.Clamp(FirstVisibleLine, 1, document.LineCount);

        Document = document;
        Raise(nameof(IsJournaled));
    }

    /// <summary>Records the encoding and line endings of a file this tab was opened from.</summary>
    public void AdoptFileMetadata(Encoding encoding, LineEndingStyle lineEnding)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        Encoding = encoding;
        LineEnding = lineEnding;
    }

    /// <summary>
    /// Takes on capabilities re-derived from what the tab holds now.
    /// </summary>
    /// <remarks>
    /// Journaling is refused a change here, deliberately and loudly, because the one caller
    /// is only ever meant to move the view features. A tab that stopped journaling after it
    /// had been would leave its shadow copy behind as it was, and restoring from that copy
    /// is how a stale text ends up saved over a newer file.
    /// </remarks>
    /// <exception cref="ArgumentException">The capabilities change whether the tab is journaled.</exception>
    internal void AdoptCapabilities(DocumentCapabilities capabilities)
    {
        if (capabilities.Journaling != Capabilities.Journaling)
        {
            throw new ArgumentException("Journaling is decided when a tab is opened and is not changed afterwards.", nameof(capabilities));
        }

        Capabilities = capabilities;
    }

    /// <summary>
    /// Records which file this tab is over and what it looked like at that moment.
    /// </summary>
    /// <remarks>
    /// Called after a load and again after every successful write-through. The second
    /// call is not optional: leave the witness describing the file as it was before
    /// Etch's own save and the very next <c>Ctrl+S</c> reports Etch's own write as
    /// somebody else's change, which trains the user to dismiss the one warning that
    /// matters.
    /// </remarks>
    internal void AdoptFileState(FileIdentity identity, FileWitness witness)
    {
        Identity = identity;
        Witness = witness;
    }

    /// <summary>
    /// Forgets the file state, for when the tab stops being over the file it was.
    /// </summary>
    internal void ForgetFileState()
    {
        Identity = default;
        Witness = null;
        OverwriteArmedFor = null;
    }

    /// <summary>
    /// The on-disk state the user has been warned about and chosen to overwrite anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carries the witness rather than a bare flag, and that is the whole point. A
    /// boolean "the user said yes" outlives the situation it was answered for: the file
    /// changes a second time, the flag is still set, and the save the user consented to
    /// is not the save that happens. Holding the state they consented to means a further
    /// change re-arms the warning by simply not matching.
    /// </para>
    /// <para>
    /// The same shape as the <c>Ctrl+K</c> prefix fix: a pending state must not outlive
    /// the thing that explains it.
    /// </para>
    /// </remarks>
    internal DiskState? OverwriteArmedFor { get; private set; }

    /// <summary>Records that the user has been warned about <paramref name="state"/>.</summary>
    internal void ArmOverwrite(DiskState? state) => OverwriteArmedFor = state;

    /// <summary>Turns a scratch tab into one backed by a file the user chose.</summary>
    /// <remarks>
    /// The path is validated by <see cref="BufferRecord"/> at the next session save,
    /// which is too late to be useful, so it is validated here as well, by round
    /// tripping it through the same rules, before anything is written to it.
    /// </remarks>
    /// <exception cref="ArgumentException">The path is not a legitimate save target.</exception>
    public void PromoteToFile(string filePath, Encoding encoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(encoding);

        // Constructing a record is the validation: it is the same code that guards the
        // session index, so the two cannot drift apart.
        _ = new BufferRecord(
            Id,
            BufferKind.File,
            Path.GetFileName(filePath) is { Length: > 0 } name ? name : filePath,
            filePath,
            CaretOffset,
            FirstVisibleLine,
            IsPinned,
            FormatOverride,
            LastModifiedUtc);

        Kind = BufferKind.File;
        FilePath = filePath;
        Encoding = encoding;

        // Whatever this tab was over before, it is not over it now. Carrying the old
        // identity forward would let a Save As onto an existing file inherit a witness
        // describing a different file, and the overwrite guard would then wave through
        // the one case it exists to catch.
        ForgetFileState();

        Title = Path.GetFileName(filePath) is { Length: > 0 } fileName ? fileName : filePath;

        Raise(nameof(Kind));
        Raise(nameof(FilePath));
    }

    /// <summary>
    /// Takes an immutable, thread-safe view of the text for the journal to write.
    /// </summary>
    /// <remarks>
    /// <see cref="TextDocument.CreateSnapshot()"/> is O(1) (the rope is shared, not
    /// copied) which is what allows this to be called on every keystroke without
    /// touching the frame budget. Materialising the string happens later, on the
    /// journal's thread, after the debounce has collapsed a burst of typing into one
    /// write.
    /// </remarks>
    public Func<string>? TakeSnapshot()
    {
        if (_document is not { } document)
        {
            return null;
        }

        var snapshot = document.CreateSnapshot();

        // A closure, not `snapshot.Text.ToString`, that reads the property here and
        // hands back a delegate over an already-materialised string, which is the
        // exact cost this method exists to defer.
        return () => snapshot.Text;
    }

    /// <summary>Projects the tab back into the record the session index stores.</summary>
    public BufferRecord ToRecord() =>
        new(
            Id,
            Kind,
            Title,
            FilePath,
            CaretOffset,
            FirstVisibleLine,
            IsPinned,
            FormatOverride,
            LastModifiedUtc);

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
