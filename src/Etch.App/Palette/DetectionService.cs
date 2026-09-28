using System.Windows.Threading;
using Etch.Core.Abstractions;
using Etch.Core.Detection;
using ICSharpCode.AvalonEdit.Document;

namespace Etch.App.Palette;

/// <summary>
/// Keeps an up-to-date answer to "what is in the buffer?" without costing a frame.
/// </summary>
/// <remarks>
/// <para>
/// Three rules from the plan, all of them load-bearing:
/// </para>
/// <list type="bullet">
/// <item><b>Never per keystroke.</b> A one-shot timer collapses a burst of typing into
/// a single detection ~150 ms after it stops.</item>
/// <item><b>Never on the UI thread.</b> The scan runs on the thread pool over an
/// immutable snapshot, which is the affordance AvalonEdit's document exists to
/// provide.</item>
/// <item><b>Never a polling timer.</b> The timer is created on first use, fires once,
/// and stops itself, so an idle Etch has nothing scheduled, which is what the plan's
/// 0% idle CPU budget actually requires.</item>
/// </list>
/// <para>
/// Results are keyed to the document version they were computed from. A result that
/// arrives after the text has moved on is discarded rather than shown, because a format
/// chip describing text the user has already replaced is worse than one that is briefly
/// a step behind.
/// </para>
/// </remarks>
internal sealed class DetectionService
{
    /// <summary>
    /// Quiet period before a scan. Long enough to collapse typing, short enough that the
    /// chip has settled by the time anyone looks at it after a paste.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private DispatcherTimer? _timer;
    private TextDocument? _document;

    /// <summary>The version the current result describes, so a stale scan can be discarded.</summary>
    private ITextSourceVersion? _resultVersion;

    /// <summary>Raised on the UI thread when the answer changes.</summary>
    public event Action<DetectionResult>? Changed;

    /// <summary>
    /// Cancels any scheduled scan. Called when the window closes.
    /// </summary>
    /// <remarks>
    /// A one-shot tick queued at close would otherwise run a scan and post its
    /// continuation onto a dispatcher that is shutting down.
    /// </remarks>
    public void Stop()
    {
        _timer?.Stop();
        _document = null;
    }

    /// <summary>The most recent answer. <see cref="DetectionResult.PlainText"/> until one arrives.</summary>
    public DetectionResult Current { get; private set; } = DetectionResult.PlainText;

    /// <summary>
    /// Notes that <paramref name="document"/> may have changed and schedules a scan.
    /// </summary>
    /// <remarks>
    /// Called from every route that changes what is on screen: a keystroke, a tab
    /// switch, an applied transform. Restarting the timer rather than queueing is the
    /// debounce.
    /// </remarks>
    public void Invalidate(TextDocument? document)
    {
        _document = document;

        if (document is null)
        {
            Publish(DetectionResult.PlainText, version: null);
            _timer?.Stop();
            return;
        }

        // Created on first use, never at startup. An editor that has been open all day
        // without being typed into has no timer object at all.
        _timer ??= CreateTimer();

        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// The answer for <paramref name="document"/> right now, computing it if the cached
    /// one is stale.
    /// </summary>
    /// <remarks>
    /// For <c>Ctrl+Enter</c> and for opening the palette, where waiting out a debounce
    /// would mean acting on the wrong buffer. Synchronous, and affordable because the
    /// scan is bounded to a 64 KB sample whatever the document's size: the same work
    /// the background pass does, on a budget of 15 ms.
    /// </remarks>
    public DetectionResult DetectNow(TextDocument? document)
    {
        if (document is null)
        {
            return DetectionResult.PlainText;
        }

        if (_resultVersion is { } version && version.BelongsToSameDocumentAs(document.Version)
            && version.CompareAge(document.Version) == 0)
        {
            return Current;
        }

        var snapshot = document.CreateSnapshot();
        var result = Scan(snapshot);

        Publish(result, snapshot.Version);

        return result;
    }

    /// <summary>Runs the detection over a snapshot, reading only what it needs.</summary>
    /// <remarks>
    /// <c>GetText(0, n)</c> rather than <c>snapshot.Text</c>. The latter walks the rope
    /// and allocates a copy of the whole document, on a 10 MB buffer that is a
    /// large-object-heap allocation per debounce, to examine the first 64 KB of it.
    /// </remarks>
    private static DetectionResult Scan(ITextSource snapshot)
    {
        var take = Math.Min(snapshot.TextLength, FormatDetection.SampleLimit);

        return FormatDetection.DetectSample(snapshot.GetText(0, take), snapshot.TextLength);
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Debounce };

        timer.Tick += (_, _) =>
        {
            // One-shot. Stopping first means an exception below cannot leave a timer
            // running forever against a document nobody is looking at.
            timer.Stop();
            Run();
        };

        return timer;
    }

    private void Run()
    {
        if (_document is not { } document)
        {
            return;
        }

        // Taken on the UI thread, which owns the document; everything after this point
        // works on the immutable snapshot and is safe anywhere.
        var snapshot = document.CreateSnapshot();

        _ = Task.Run(() => Scan(snapshot)).ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    // FormatDetection already swallows a failing detector. Reaching here
                    // means something more fundamental, and a wrong format chip is not
                    // worth taking the editor down for.
                    return;
                }

                if (_document is null || !ReferenceEquals(_document, document))
                {
                    // Different tab now. The result describes a buffer that is no longer
                    // on screen.
                    return;
                }

                // And the version, not just the document. Two scans can overlap, an
                // Invalidate landing while the first is on the pool, and nothing makes
                // them finish in order, so without this the older answer can publish last
                // and leave the chip describing text that has already been replaced.
                if (snapshot.Version is not { } scanned
                    || !scanned.BelongsToSameDocumentAs(document.Version)
                    || scanned.CompareAge(document.Version) != 0)
                {
                    return;
                }

                Publish(task.Result, scanned);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Publish(DetectionResult result, ITextSourceVersion? version)
    {
        _resultVersion = version;

        if (Current == result)
        {
            return;
        }

        Current = result;
        Changed?.Invoke(result);
    }
}
