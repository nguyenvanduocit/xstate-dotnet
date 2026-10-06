using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class RealClockTests
{
    private sealed class Errors : IUnhandledErrorReporter
    {
        public ConcurrentQueue<Exception> Items { get; } = new();
        public void Report(object? failure) => Items.Enqueue(ActorErrors.ToException(failure));
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new();
        public TaskCompletionSource Posted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (queue) { queue.Enqueue((d, state)); Posted.TrySetResult(); }
        }
        public void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (queue)
                {
                    if (!queue.TryDequeue(out item))
                    {
                        Posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        return;
                    }
                }
                item.Callback(item.State);
            }
        }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("real timers preserve registration order at equal delays", TimerOrder));
        cases.Add(("posted timer callbacks can be cancelled before context dispatch", CancelPosted));
        cases.Add(("real timer callback exceptions are reported and other timers continue", CallbackError));
        cases.Add(("real clock normalizes Node timeout bounds without retaining timers", TimeoutBounds));
        cases.Add(("stop cancels a real actor timer already posted to its context", StopPosted));
        cases.Add(("actor operations and real timers do not overlap across roots", NoOverlap));
        cases.Add(("real clock dispatches on the captured synchronization context", ContextDispatch));
        cases.Add(("stopped real timer actors and payloads can be collected", CaptureRelease));
        cases.Add(("failed context posting releases pending timers and reports the error", PostFailure));
    }
    private static async Task TimerOrder()
    {
        const int count = 100;
        var errors = new Errors();
        var clock = new RealClock(errors);
        var otherClock = new RealClock(errors);
        var seen = new List<int>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ActorRuntime.Run(() =>
        {
            for (var index = 0; index < count; index++)
            {
                var captured = index;
                (index % 2 == 0 ? clock : otherClock).SetTimeout(() => { seen.Add(captured); if (seen.Count == count) completion.TrySetResult(); }, 1);
            }
        });
        await completion.Task.ConfigureAwait(false);
        Equal(string.Join(',', Enumerable.Range(0, count)), string.Join(',', seen));
        Equal(0, errors.Items.Count);
        Equal(0, clock.PendingCount);
        Equal(0, otherClock.PendingCount);
    }
    private static (RealClock Clock, QueuedContext Context) ContextClock(Errors errors)
    {
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        try { SynchronizationContext.SetSynchronizationContext(context); return (new RealClock(errors), context); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static async Task CancelPosted()
    {
        var errors = new Errors();
        var (clock, context) = ContextClock(errors);
        var calls = 0;
        var id = clock.SetTimeout(() => calls++, 1);
        await context.Posted.Task.ConfigureAwait(false);
        clock.ClearTimeout(id);
        context.Drain();
        Equal(0, calls);
        Equal(0, clock.PendingCount);
        Equal(0, errors.Items.Count);
    }
    private static async Task CallbackError()
    {
        var errors = new Errors();
        var clock = new RealClock(errors);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.SetTimeout(() => throw new InvalidOperationException("timer failure"), 1);
        clock.SetTimeout(() => completion.TrySetResult(), 20);
        await completion.Task.ConfigureAwait(false);
        Equal(1, errors.Items.Count);
        Equal("timer failure", errors.Items.Single().Message);
        Equal(0, clock.PendingCount);
    }
    private static async Task TimeoutBounds()
    {
        var errors = new Errors();
        var clock = new RealClock(errors);
        double[] delays = [double.NaN, double.PositiveInfinity, -1, 0, 2147483648, 1.9];
        var remaining = delays.Length;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var delay in delays) clock.SetTimeout(() => { if (--remaining == 0) completion.TrySetResult(); }, delay);
        await completion.Task.ConfigureAwait(false);
        Equal(0, errors.Items.Count);
        Equal(0, clock.PendingCount);
    }
    private static async Task StopPosted()
    {
        var errors = new Errors();
        var (clock, context) = ContextClock(errors);
        var calls = 0;
        var machine = new StateMachine<int>(new()
        {
            Entry = [MachineActions.Raise<int>((_, _) => new("GO"), new() { Delay = MachineDelays.From<int>(1) })],
            On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
            { ["GO"] = [new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] }] }
        }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Clock = clock, ErrorReporter = errors }).Start();
        await context.Posted.Task.ConfigureAwait(false);
        actor.Stop();
        context.Drain();
        Equal(0, calls);
        Equal(0, actor.System.GetSnapshot().ScheduledEvents.Count);
        Equal(0, clock.PendingCount);
        Equal(0, errors.Items.Count);
    }
    private static async Task ContextDispatch()
    {
        var errors = new Errors();
        var (clock, context) = ContextClock(errors);
        var callbackThread = -1;
        clock.SetTimeout(() => callbackThread = Environment.CurrentManagedThreadId, 1);
        var expectedThread = -1;
        // A native timer can wake before its Stopwatch deadline; the queue then rearms.
        // Pump each posted turn until the user callback runs, checking affinity on that turn.
        while (callbackThread == -1)
        {
            await context.Posted.Task.ConfigureAwait(false);
            Equal(-1, callbackThread);
            expectedThread = Environment.CurrentManagedThreadId;
            context.Drain();
        }
        Equal(expectedThread, callbackThread);
        Equal(0, clock.PendingCount);
        Equal(0, errors.Items.Count);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (RealClock Clock, WeakReference Actor, WeakReference Payload) StoppedCapture()
    {
        var payload = new byte[4096];
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new()
        {
            Entry = [MachineActions.Raise<int>((_, _) => new("LATER", payload), new() { Delay = MachineDelays.From<int>(100000) })]
        }, _ => 0)).Start();
        var clock = actor.Clock as RealClock ?? throw new InvalidOperationException("Missing default clock.");
        Equal(1, clock.PendingCount);
        actor.Stop();
        return (clock, new(actor), new(payload));
    }
    private static Task CaptureRelease()
    {
        var (clock, actor, payload) = StoppedCapture();
        Equal(0, clock.PendingCount);
        for (var i = 0; i < 3 && (actor.IsAlive || payload.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, actor.IsAlive);
        Equal(false, payload.IsAlive);
        GC.KeepAlive(clock);
        return Task.CompletedTask;
    }
    private sealed class RejectingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new InvalidOperationException("Dispatcher rejected callback.");
    }
    private sealed class CompletionReporter : IUnhandledErrorReporter
    {
        public TaskCompletionSource<Exception> Error { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(object? failure) => Error.TrySetResult(ActorErrors.ToException(failure));
    }
    private static async Task PostFailure()
    {
        var reporter = new CompletionReporter();
        var previous = SynchronizationContext.Current;
        RealClock clock;
        try { SynchronizationContext.SetSynchronizationContext(new RejectingContext()); clock = new RealClock(reporter); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        var calls = 0;
        clock.SetTimeout(() => calls++, 1);
        var error = await reporter.Error.Task.ConfigureAwait(false);
        Equal("Dispatcher rejected callback.", error.Message);
        Equal(0, calls);
        Equal(0, clock.PendingCount);
    }
    private static async Task NoOverlap()
    {
        var active = 0;
        var overlap = 0;
        var calls = 0;
        void Operation()
        {
            if (Interlocked.Increment(ref active) != 1) Interlocked.Increment(ref overlap);
            Thread.SpinWait(1000);
            calls++;
            Interlocked.Decrement(ref active);
        }
        var errors = new Errors();
        var logic = new TransitionLogic<int>((state, ev, _) => { if (ev.Type == "INC") { Operation(); return state + 1; } return state; }, 0);
        var first = new Actor<TransitionSnapshot<int>>(logic, options: new() { ErrorReporter = errors }).Start();
        var second = new Actor<TransitionSnapshot<int>>(logic, options: new() { ErrorReporter = errors }).Start();
        var clock = new RealClock(errors);
        var timersDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ActorRuntime.Run(() =>
        {
            for (var i = 0; i < 100; i++) clock.SetTimeout(Operation, 1);
            clock.SetTimeout(() => timersDone.TrySetResult(), 30);
        });
        await Task.WhenAll(
            Task.Run(() => { for (var i = 0; i < 100; i++) first.Send(new("INC")); }),
            Task.Run(() => { for (var i = 0; i < 100; i++) second.Send(new("INC")); }),
            timersDone.Task).ConfigureAwait(false);
        Equal(0, overlap);
        Equal(300, calls);
        Equal(100, first.GetSnapshot().Context);
        Equal(100, second.GetSnapshot().Context);
        first.Stop();
        second.Stop();
        Equal(0, errors.Items.Count);
    }
}
