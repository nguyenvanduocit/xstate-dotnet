using System.Diagnostics;
using System.Runtime.CompilerServices;
namespace XState;

/// <summary>Real timers ordered by deadline, serialized with actor turns, and dispatched to a captured context when present.</summary>
public sealed class RealClock : IClock
{
    private sealed class TimeoutEntry(RealClock owner, long id, Action callback, long due, long sequence)
    {
        internal RealClock Owner { get; } = owner;
        internal long Id { get; } = id;
        internal long Due { get; } = due;
        internal long Sequence { get; } = sequence;
        internal Action? Callback { get; set; } = callback;
    }
    // All clocks on the same execution context use the same deadline queue, including independent roots.
    // The queue owns a native timer only while there is pending work.
    private sealed class TimerQueue(SynchronizationContext? context)
    {
        private readonly ActorMicrotasks microtasks = ActorMicrotasks.For(context);
        private readonly SortedSet<TimeoutEntry> entries = new(Comparer<TimeoutEntry>.Create(static (left, right) =>
        {
            var due = left.Due.CompareTo(right.Due);
            return due != 0 ? due : left.Sequence.CompareTo(right.Sequence);
        }));
        private Timer? timer;
        private bool dispatchPending;
        internal void Add(TimeoutEntry entry) { entries.Add(entry); ScheduleNext(); }
        internal void Cancel(TimeoutEntry entry) { Take(entry); ScheduleNext(); }
        private Action? Take(TimeoutEntry entry)
        {
            entries.Remove(entry);
            entry.Owner.timeouts?.Remove(entry.Id);
            var callback = entry.Callback;
            entry.Callback = null;
            return callback;
        }
        private void ScheduleNext()
        {
            if (entries.Min is not { } first)
            {
                timer?.Dispose();
                timer = null;
                return;
            }
            if (dispatchPending) return;
            var wait = Math.Clamp(Math.Ceiling((first.Due - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency), 0, int.MaxValue);
            timer ??= new Timer(static state =>
                (state as TimerQueue ?? throw new InvalidOperationException("Timer queue missing.")).Due(), this, Timeout.Infinite, Timeout.Infinite);
            timer.Change((int)wait, Timeout.Infinite);
        }
        private void Due()
        {
            lock (ActorRuntime.Gate)
            {
                if (dispatchPending || entries.Count == 0) return;
                dispatchPending = true;
                timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            if (context is null) { Drain(); return; }
            try
            {
                context.Post(static state =>
                    (state as TimerQueue ?? throw new InvalidOperationException("Timer dispatch queue missing.")).Drain(), this);
            }
            catch (Exception error)
            {
                IUnhandledErrorReporter[] reporters;
                lock (ActorRuntime.Gate)
                {
                    reporters = entries.Select(entry => entry.Owner.reporter).Distinct().ToArray();
                    foreach (var entry in entries.ToArray()) Take(entry);
                    dispatchPending = false;
                    ScheduleNext();
                }
                foreach (var reporter in reporters) reporter.Report(ActorErrors.GetValue(error));
            }
        }
        private void Drain()
        {
            lock (ActorRuntime.Gate)
            {
                try
                {
                    microtasks.Drain();
                    while (entries.Min is { } entry && entry.Due <= Stopwatch.GetTimestamp())
                    {
                        var callback = Take(entry);
                        try { callback?.Invoke(); }
                        catch (Exception error) { entry.Owner.reporter.Report(ActorErrors.GetValue(error)); }
                        microtasks.Drain();
                    }
                }
                finally
                {
                    dispatchPending = false;
                    ScheduleNext();
                }
            }
        }
    }
    private static readonly TimerQueue BackgroundQueue = new(null);
    private static readonly ConditionalWeakTable<SynchronizationContext, TimerQueue> ContextQueues = new();
    private static long sequence;
    private readonly TimerQueue queue;
    private readonly IUnhandledErrorReporter reporter;
    private Dictionary<long, TimeoutEntry>? timeouts;
    private long nextId;
    public RealClock(IUnhandledErrorReporter? errorReporter = null)
    {
        var context = SynchronizationContext.Current;
        queue = context is null || context.GetType() == typeof(SynchronizationContext) ? BackgroundQueue :
            ContextQueues.GetValue(context, static key => new(key));
        reporter = errorReporter ?? new UnhandledErrorReporter();
    }
    public double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public int PendingCount { get { lock (ActorRuntime.Gate) return timeouts?.Count ?? 0; } }
    public long SetTimeout(Action callback, double timeout)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (ActorRuntime.Gate)
        {
            var milliseconds = double.IsFinite(timeout) && timeout >= 1 && timeout <= int.MaxValue ? (int)timeout : 1;
            var due = Stopwatch.GetTimestamp() + (long)(milliseconds * (double)Stopwatch.Frequency / 1000);
            var entry = new TimeoutEntry(this, nextId++, callback, due, sequence++);
            (timeouts ??= []).Add(entry.Id, entry);
            try { queue.Add(entry); }
            catch { queue.Cancel(entry); throw; }
            return entry.Id;
        }
    }
    public void ClearTimeout(long id)
    {
        lock (ActorRuntime.Gate)
            if (timeouts?.TryGetValue(id, out var entry) == true) queue.Cancel(entry);
    }
}
