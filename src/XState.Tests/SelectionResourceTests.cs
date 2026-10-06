using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static partial class SelectionTests
{
    private sealed class Errors : IUnhandledErrorReporter { internal List<Exception> Values { get; } = []; public void Report(object? failure) => Values.Add(ActorErrors.ToException(failure)); }
    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        internal int Errors { get; private set; }
        internal int Completions { get; private set; }
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) => Errors++;
        public void OnCompleted() => Completions++;
    }
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    private static Actor<TransitionSnapshot<T>> ValueActor<T>(T value, Errors? errors = null) => new Actor<TransitionSnapshot<T>>(new TransitionLogic<T>((current, ev, _) => ev.Type == "SET" ? (T)(ev.Payload ?? throw new InvalidOperationException("Missing value")) : current, value), options: new() { ErrorReporter = errors }).Start();
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("selection is lazy and each subscription captures its own prior value", Lazy);
        Case("selection Object.is comparison preserves NaN signed zero numeric boxing and reference identity", SameValue);
        Case("selection keeps last notified value when custom equality suppresses updates", Previous);
        Case("selection propagates initial selector errors and reports later callback errors without losing subscription", Failures);
        Case("selection ignores observer terminal methods while actor reports unhandled errors", Termination);
        Case("selection preserves actor notification ordering under reentrant sends and subscription changes", Reentrant);
        Case("actor reference selection works through typed covariant and erased interfaces", Interfaces);
        Case("selection disposal and actor stop release selector callback and previous-value captures", Released);
        Case("export selection observations for upstream differential", () => File.WriteAllText("tmp/xstate-parity/csharp-selection.json", JsonSerializer.Serialize(Observations)));
    }
    private static void Lazy()
    {
        var actor = ValueActor(0); var calls = 0;
        try
        {
            var selection = actor.Select(state => { calls++; return state.Context; }); Equal(0, calls); Equal(0, selection.GetValue()); Equal(1, calls);
            var first = new List<int>(); var second = new List<int>(); using var a = selection.Subscribe(first.Add); Equal(0, first.Count); Equal(2, calls);
            actor.Send(new("SET", 1)); using var b = selection.Subscribe(second.Add); actor.Send(new("SET", 2));
            Equal(true, first.SequenceEqual([1, 2])); Equal(true, second.SequenceEqual([2])); Equal(6, calls);
            Observations["lazy"] = new { first, second, calls };
        }
        finally { actor.Stop(); }
    }
    private static string NumberLabel(double value) => double.IsNaN(value) ? "NaN" : value == 0 ? BitConverter.DoubleToInt64Bits(value) < 0 ? "-0" : "+0" : double.IsPositiveInfinity(value) ? "Infinity" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static void SameValue()
    {
        var actor = ValueActor(double.NaN);
        try
        {
            var values = new List<string>(); using var subscription = actor.Select(state => state.Context).Subscribe(value => values.Add(NumberLabel(value)));
            foreach (var value in new[] { double.NaN, 0d, -0d, -0d, 0d, double.PositiveInfinity, double.PositiveInfinity, double.NaN }) actor.Send(new("SET", value));
            Equal(true, values.SequenceEqual(["+0", "-0", "+0", "Infinity", "NaN"])); Observations["numbers"] = values;
        }
        finally { actor.Stop(); }
        var original = new Position(1, 2); var objects = ValueActor<object>(original);
        try
        {
            var values = new List<string>(); using var subscription = objects.Select(state => state.Context).Subscribe(value => values.Add(value is Position ? "position" : value is string ? "string" : "number"));
            objects.Send(new("SET", original)); var other = new Position(1, 2); objects.Send(new("SET", other)); objects.Send(new("SET", other));
            objects.Send(new("SET", "a")); objects.Send(new("SET", new string('a', 1))); objects.Send(new("SET", 1)); objects.Send(new("SET", 1d));
            Equal(true, values.SequenceEqual(["position", "string", "number"])); Observations["objects"] = values;
        }
        finally { objects.Stop(); }
    }
    private static void Previous()
    {
        var actor = ValueActor(0); var pairs = new List<int[]>(); var values = new List<int>();
        try
        {
            var selection = actor.Select(state => state.Context, (a, b) => { pairs.Add([a, b]); return b - a < 3; }); using var subscription = selection.Subscribe(values.Add);
            foreach (var value in new[] { 1, 2, 3, 4 }) actor.Send(new("SET", value));
            Equal(true, values.SequenceEqual([3])); Equal(4, selection.GetValue());
            Equal("[[0,1],[0,2],[0,3],[3,4]]", JsonSerializer.Serialize(pairs)); Observations["previous"] = new { pairs, values, current = selection.GetValue() };
        }
        finally { actor.Stop(); }
    }
    private static void Failures()
    {
        var errors = new Errors(); var actor = ValueActor(0, errors);
        try
        {
            var tracked = new TrackedActor<TransitionSnapshot<int>>(actor);
            var initial = new InvalidOperationException("initial");
            var broken = ((IActorRef<TransitionSnapshot<int>>)tracked).SelectValue<int>(_ => throw initial);
            try { broken.Subscribe(_ => { }); throw new InvalidOperationException("Expected initial selector failure"); } catch (InvalidOperationException error) when (ReferenceEquals(error, initial)) { }
            try { broken.GetValue(); throw new InvalidOperationException("Expected get failure"); } catch (InvalidOperationException error) when (ReferenceEquals(error, initial)) { }
            Equal(0, tracked.Subscriptions);
            var seen = new List<int>(); var comparisons = new List<int[]>();
            using var subscription = actor.Select(state => state.Context == 2 ? throw new InvalidOperationException("selector") : state.Context,
                (a, b) => { comparisons.Add([a, b]); if (b == 3) throw new InvalidOperationException("comparator"); return a == b; }).Subscribe(value => { seen.Add(value); if (value == 1) throw new InvalidOperationException("callback"); });
            foreach (var value in new[] { 1, 1, 2, 3, 4 }) actor.Send(new("SET", value));
            Equal(true, seen.SequenceEqual([1, 4])); Equal(true, errors.Values.Select(error => error.Message).SequenceEqual(["callback", "selector", "comparator"]));
            Equal(SnapshotStatus.Active, actor.GetSnapshot().Status);
            Observations["failures"] = new { seen, comparisons, errors = errors.Values.Select(error => error.Message).ToArray() };
        }
        finally { actor.Stop(); }
    }
    private static void Termination()
    {
        var errors = new Errors(); var fault = new InvalidOperationException("actor failed");
        var actor = new Actor<TransitionSnapshot<int>>(new TransitionLogic<int>((value, ev, _) => ev.Type == "FAIL" ? throw fault : value, 0), options: new() { ErrorReporter = errors }).Start();
        var observer = new Observer<int>(_ => { }); using var subscription = actor.Select(state => state.Context).Subscribe(observer);
        actor.Send(new("FAIL")); Equal(0, observer.Errors); Equal(0, observer.Completions); Equal(1, errors.Values.Count);
        using var late = actor.Select(state => state.Context).Subscribe(observer); Equal(2, errors.Values.Count); Equal(true, errors.Values.All(error => ReferenceEquals(error, fault)));
        var stopped = ValueActor(0); using var stopping = stopped.Select(state => state.Context).Subscribe(observer); stopped.Stop(); Equal(0, observer.Completions);
        Observations["termination"] = new { errors = observer.Errors, completions = observer.Completions, reported = errors.Values.Count };
    }
    private static void Reentrant()
    {
        var actor = ValueActor(0); var trace = new List<string>(); IDisposable? second = null; IDisposable? added = null;
        try
        {
            var selection = actor.Select(state => state.Context);
            using var first = selection.Subscribe(value =>
            {
                trace.Add("first:" + value);
                if (value != 1) return;
                second?.Dispose(); added = selection.Subscribe(next => trace.Add("added:" + next)); actor.Send(new("SET", 2));
            });
            second = selection.Subscribe(value => trace.Add("second:" + value)); actor.Send(new("SET", 1));
            Equal(true, trace.SequenceEqual(["first:1", "first:2", "added:2"])); Observations["reentrant"] = trace;
        }
        finally { added?.Dispose(); second?.Dispose(); actor.Stop(); }
    }
    private static void Interfaces()
    {
        var actor = ValueActor(42); try
        {
            IActor erased = actor;
            Equal(42, ((IActorRef<TransitionSnapshot<int>>)actor).SelectValue(state => state.Context).GetValue()); Equal(SnapshotStatus.Active, erased.SelectValue(state => state.Status).GetValue());
            Equal(SnapshotStatus.Active, ((IActorRef<IActorSnapshot>)actor).SelectValue(state => state.Status).GetValue());
        }
        finally { actor.Stop(); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Actor<TransitionSnapshot<int>> Actor, IDisposable Subscription, WeakReference Selector, WeakReference Callback, WeakReference Value) Capture()
    {
        var actor = ValueActor(0); var selectorCapture = new byte[65536]; var callbackCapture = new byte[65536]; byte[]? previous = null;
        var selection = actor.Select(_ => { GC.KeepAlive(selectorCapture); previous = new byte[65536]; return previous; });
        var subscription = selection.Subscribe(_ => GC.KeepAlive(callbackCapture));
        return (actor, subscription, new(selectorCapture), new(callbackCapture), new(previous ?? throw new InvalidOperationException("No initial selected value")));
    }
    private static void Released()
    {
        foreach (var stop in new[] { false, true })
        {
            var references = Capture(); if (stop) references.Actor.Stop(); else references.Subscription.Dispose();
            for (var i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Equal(false, references.Selector.IsAlive); Equal(false, references.Callback.IsAlive); Equal(false, references.Value.IsAlive);
            GC.KeepAlive(references.Actor); GC.KeepAlive(references.Subscription); references.Actor.Stop();
        }
    }
    public static int Benchmark(string destination)
    {
        const int samples = 20000; var rows = new List<object>();
        foreach (var count in new[] { 0, 1, 8 })
        {
            var actor = ValueActor(0); var observed = 0; var subscriptions = new List<IDisposable>();
            try
            {
                for (var i = 0; i < count; i++) subscriptions.Add(actor.Select(state => state.Context).Subscribe(_ => observed++));
                var ev = new MachineEvent("SET", 1); var zero = new MachineEvent("SET", 0);
                for (var i = 0; i < 3000; i++) actor.Send(i % 2 == 0 ? ev : zero);
                observed = 0; var timings = new long[samples]; var allocated = GC.GetAllocatedBytesForCurrentThread();
                var collections = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
                for (var i = 0; i < samples; i++) { var start = Stopwatch.GetTimestamp(); actor.Send(i % 2 == 0 ? ev : zero); timings[i] = Stopwatch.GetTimestamp() - start; }
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Equal(samples * count, observed); Array.Sort(timings);
                rows.Add(new { subscribers = count, samples, allocatedBytesPerEvent = allocated / (double)samples, p95Microseconds = timings[(int)(samples * .95)] * 1_000_000d / Stopwatch.Frequency, p99Microseconds = timings[(int)(samples * .99)] * 1_000_000d / Stopwatch.Frequency, gcCollections = collections.Select((before, generation) => GC.CollectionCount(generation) - before).ToArray() });
            }
            finally { foreach (var subscription in subscriptions) subscription.Dispose(); actor.Stop(); }
        }
        File.WriteAllText(destination, JsonSerializer.Serialize(rows)); return 0;
    }
}
