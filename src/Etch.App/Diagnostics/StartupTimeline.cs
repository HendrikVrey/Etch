using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace Etch.App.Diagnostics;

/// <summary>
/// Records how long each phase of startup took, from real process creation through
/// to the window being ready for input.
/// </summary>
/// <remarks>
/// <para>
/// This exists to answer one question: can WPF get Etch on screen inside the
/// 250 ms budget? A number that starts counting at <c>Main</c> would flatter us by
/// hiding host and runtime initialisation, which on a self-contained WPF app is
/// usually the largest single slice. So the origin is
/// <see cref="Process.StartTime"/>, not managed entry.
/// </para>
/// <para>
/// Measuring costs something, and what it costs is excluded: the clock is reset
/// after the process handle has been read, so the reported total is what an
/// ordinary launch pays rather than what a measured launch pays.
/// </para>
/// <para>
/// When diagnostics are off, <see cref="Mark"/> costs one static bool read and a
/// return. Nothing is allocated, no process handle is opened, and no timer runs.
/// </para>
/// </remarks>
internal static class StartupTimeline
{
    /// <summary>The §4 budget: cold start to an interactive window.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Resolution of the Windows system clock, which bounds how precisely the
    /// pre-managed slice can be known. Reported so a near-budget result is read as
    /// the coin-flip it is rather than as a decision.
    /// </summary>
    private static readonly TimeSpan ClockQuantum = TimeSpan.FromMilliseconds(15.625);

    private static readonly Lock Gate = new();
    private static readonly List<Phase> Phases = new(16);

    private static long _origin;
    private static TimeSpan _preManaged;
    private static TimeSpan _measurementOverhead;
    private static string? _preManagedError;
    private static volatile bool _enabled;
    private static bool _completed;

    /// <summary>Whether the timeline is recording.</summary>
    public static bool Enabled => _enabled;

    /// <summary>
    /// Starts the clock. Call this as the very first statement in <c>Main</c>,
    /// before touching anything else.
    /// </summary>
    /// <param name="enabled">False disables all recording; every other member becomes a no-op.</param>
    public static void Begin(bool enabled)
    {
        // Take both clocks together so the process-start offset and the high
        // resolution origin refer to the same instant.
        var origin = Stopwatch.GetTimestamp();
        var wallClockAtOrigin = DateTime.UtcNow;

        _origin = origin;
        _enabled = enabled;

        if (!enabled)
        {
            return;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            _preManaged = wallClockAtOrigin - process.StartTime.ToUniversalTime();

            // A negative value means the two clocks disagree (it happens across
            // suspend/resume). Report it rather than pretending startup was instant.
            if (_preManaged < TimeSpan.Zero)
            {
                _preManagedError = "process start time is ahead of the current clock";
                _preManaged = TimeSpan.Zero;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            // Diagnostics must never be the reason the app fails to start.
            _preManagedError = ex.Message;
            _preManaged = TimeSpan.Zero;
        }

        // Opening a process handle is not free, and an ordinary launch never pays
        // it. Restart the clock so it lands outside the measurement instead of
        // inflating the first phase and the verdict.
        var afterOverhead = Stopwatch.GetTimestamp();
        _measurementOverhead = Stopwatch.GetElapsedTime(origin, afterOverhead);
        _origin = afterOverhead;
    }

    /// <summary>Records the end of a startup phase.</summary>
    /// <param name="name">Short phase name, shown verbatim in the report.</param>
    /// <param name="detail">Optional explanation shown alongside the timing.</param>
    /// <remarks>
    /// Never throws, even on a malformed name. Instrumentation that fails only
    /// under <c>--diag</c> would be a bug visible exclusively while measuring.
    /// </remarks>
    public static void Mark(string name, string? detail = null)
    {
        if (!_enabled)
        {
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        var safeName = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;

        lock (Gate)
        {
            if (_completed)
            {
                return;
            }

            Phases.Add(new Phase(safeName, Stopwatch.GetElapsedTime(_origin, timestamp), detail));
        }
    }

    /// <summary>
    /// Seals the timeline and renders it. Further calls to <see cref="Mark"/> are ignored.
    /// </summary>
    /// <returns>The rendered report, or null when diagnostics are disabled.</returns>
    public static StartupReport? Complete()
    {
        if (!_enabled)
        {
            return null;
        }

        Phase[] phases;
        lock (Gate)
        {
            if (_completed)
            {
                return null;
            }

            _completed = true;
            phases = Phases.ToArray();
        }

        var managedElapsed = phases.Length > 0 ? phases[^1].Elapsed : TimeSpan.Zero;
        var total = _preManaged + managedElapsed;

        return new StartupReport(total, total <= Budget, Render(phases, total));
    }

    private static string Render(Phase[] phases, TimeSpan total)
    {
        var report = new StringBuilder(1024);

        report.AppendLine("Etch - startup timeline");
        report.AppendLine(CultureInfo.InvariantCulture, $"  captured   {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"  runtime    {RuntimeInformation.FrameworkDescription}");
        report.AppendLine(CultureInfo.InvariantCulture, $"  platform   {RuntimeInformation.RuntimeIdentifier}  ({RuntimeInformation.ProcessArchitecture})");
        report.AppendLine(CultureInfo.InvariantCulture, $"  deployment {DescribeDeployment()}");
        report.AppendLine();

        report.AppendLine(CultureInfo.InvariantCulture,
            $"  {"process start → managed Main",-34}{Ms(_preManaged),12}   host + runtime init");

        if (_preManagedError is not null)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {string.Empty,-34}{string.Empty,12}   (unreliable: {_preManagedError})");
        }

        report.AppendLine(new string('-', 78));

        var previous = TimeSpan.Zero;
        foreach (var phase in phases)
        {
            var delta = phase.Elapsed - previous;
            previous = phase.Elapsed;

            var cumulative = _preManaged + phase.Elapsed;
            var detail = phase.Detail is null ? string.Empty : $"   {phase.Detail}";

            report.AppendLine(CultureInfo.InvariantCulture,
                $"  + {phase.Name,-32}{Ms(delta),12}   ({Ms(cumulative)} total){detail}");
        }

        report.AppendLine(new string('-', 78));
        report.AppendLine(CultureInfo.InvariantCulture, $"  = {"window interactive",-32}{Ms(total),12}");
        report.AppendLine();

        var headroom = Budget - total;
        var verdict = headroom >= TimeSpan.Zero
            ? $"PASS - {Ms(headroom)} under budget"
            : $"FAIL - {Ms(headroom.Negate())} over budget";

        report.AppendLine(CultureInfo.InvariantCulture, $"  budget     {Ms(Budget)}   {verdict}");
        report.AppendLine(CultureInfo.InvariantCulture, $"  working set{Environment.WorkingSet / (1024.0 * 1024.0),11:0.0} MB");
        report.AppendLine(CultureInfo.InvariantCulture, $"  allocated  {GC.GetTotalAllocatedBytes(precise: false) / (1024.0 * 1024.0),11:0.0} MB");
        report.AppendLine(CultureInfo.InvariantCulture, $"  gc         {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} (gen0/1/2)");

        // JIT time is the clearest signal of whether ReadyToRun is doing its job.
        // A large compiled-method count on an R2R build means the pre-compiled
        // code is being rejected — usually a RID or framework-version mismatch.
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  jit        {Ms(JitInfo.GetCompilationTime())} across {JitInfo.GetCompiledMethodCount()} methods");

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  note       'process start → managed Main' derives from the system clock, which ticks");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"             every {ClockQuantum.TotalMilliseconds:0.###} ms. Treat the total as ±{ClockQuantum.TotalMilliseconds:0} ms; a result within");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"             ~20 ms of budget is not a decision. Measurement overhead of {Ms(_measurementOverhead)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"             is excluded - an ordinary launch does not pay it.");

        return report.ToString();
    }

    private static string DescribeDeployment()
    {
        // In a single-file bundle the entry assembly reports an empty location.
        var location = Assembly.GetEntryAssembly()?.Location;

        return string.IsNullOrEmpty(location) ? "single-file bundle" : "loose assemblies";
    }

    private static string Ms(TimeSpan value) =>
        value.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture) + " ms";

    private readonly record struct Phase(string Name, TimeSpan Elapsed, string? Detail);
}

/// <summary>The outcome of a startup measurement.</summary>
/// <param name="Total">Process creation through to an interactive window.</param>
/// <param name="WithinBudget">Whether <paramref name="Total"/> met <see cref="StartupTimeline.Budget"/>.</param>
/// <param name="Text">The rendered, human-readable timeline.</param>
internal readonly record struct StartupReport(TimeSpan Total, bool WithinBudget, string Text);
