using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ErrorValueTests
{
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    private static object?[] Values() => ["failure", 17, false, null, new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = 42, ["detail"] = null }, new InvalidOperationException("exception")];
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("raw promise rejection preserves identity across observers snapshot task helpers and persistence", PromiseValues));
        cases.Add(("raw thenable rejection includes null and object values", ThenableValues));
        cases.Add(("raw errors survive callback start reducer machine initialization and action boundaries", ThrowBoundaries));
        cases.Add(("raw observable errors cross the BCL observer boundary without changing the value", ObservableValues));
        cases.Add(("BCL observers wrap only non Exception errors and reporters receive raw errors", ObserverBoundaries));
        cases.Add(("waitFor explicit raw cancellation reasons reject before and after registration", CancellationReasons));
        cases.Add(("child reporters inherit system ownership and explicit overrides remain local", ReporterOwnership));
        cases.Add(("raw error differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-error-values.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask; }));
    }
    private static void Same(object? expected, object? actual) => Equal(true, ReferenceEquals(expected, actual));
    private static async Task Rejected(Task task, object? reason)
    {
        Exception? caught = null;
        try { await task.ConfigureAwait(false); } catch (Exception error) { caught = error; }
        if (caught is null) throw new InvalidOperationException("Expected a rejected Task.");
        Same(reason, ActorErrors.GetValue(caught)); Equal(true, task.IsFaulted);
        if (reason is Exception exception) Same(exception, caught);
    }
    private static async Task PromiseValues()
    {
        var observations = new List<object>();
        foreach (var reason in Values())
        {
            var received = new List<object?>(); var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(_ => Task.FromException<int>(ActorErrors.ToException(reason))));
            using var subscription = actor.Subscribe(onError: received.Add);
            var promise = ActorTasks.ToPromiseAsync(actor); var waiting = ActorTasks.WaitForAsync(actor, _ => false);
            actor.Start(); await Rejected(promise, reason).ConfigureAwait(false); await Rejected(waiting, reason).ConfigureAwait(false);
            Equal(SnapshotStatus.Error, actor.GetSnapshot().Status); Same(reason, actor.GetSnapshot().Failure); Equal(1, received.Count); Same(reason, received[0]);
            using var late = actor.Subscribe(onError: received.Add); Equal(2, received.Count); Same(reason, received[1]);
            await Rejected(ActorTasks.ToPromiseAsync(actor), reason).ConfigureAwait(false);
            var persisted = actor.GetPersistedSnapshot(); Same(actor.GetSnapshot(), persisted); var json = SnapshotJson.Serialize(persisted);
            var calls = 0; var restored = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(_ => { calls++; return Task.FromResult(1); }), options: new() { Snapshot = SnapshotJson.Parse(json) });
            object? restoredError = null; var notified = 0; using var restoredSubscription = restored.Subscribe(onError: error => { notified++; restoredError = error; }); restored.Start();
            Equal(0, calls); Equal(1, notified); Same(restored.GetSnapshot().Failure, restoredError); Equal(json, SnapshotJson.Serialize(restored.GetPersistedSnapshot()));
            observations.Add(new { json = JsonSerializer.Deserialize<JsonElement>(json), observed = received.Count, restored = notified }); actor.Stop(); restored.Stop();
        }
        Observations["promise"] = observations;
    }
    private sealed class Thenable(object? reason) : IPromiseLike<int> { public PromiseThen<int> ThenHandler => resolver => resolver.Reject(reason); }
    private static async Task ThenableValues()
    {
        foreach (var reason in Values())
        {
            var actor = new Actor<PromiseSnapshot<int>>(PromiseActors.FromPromiseLike<int>(_ => new Thenable(reason)));
            var pending = ActorTasks.ToPromiseAsync(actor); actor.Start(); await Rejected(pending, reason).ConfigureAwait(false);
            Same(reason, actor.GetSnapshot().Failure); Equal(SnapshotStatus.Error, actor.GetSnapshot().Status); actor.Stop();
        }
    }
    private static void Verify(IActor actor, object? reason, Action? trigger = null)
    {
        var seen = new List<object?>(); using var subscription = actor.Subscribe(onError: seen.Add); actor.StartActor(); trigger?.Invoke();
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status); Same(reason, actor.GetSnapshot().Failure); Equal(1, seen.Count); Same(reason, seen[0]); actor.StopActor();
    }
    private static Task ThrowBoundaries()
    {
        foreach (var reason in Values())
        {
            Verify(new Actor<CallbackSnapshot>(new CallbackLogic(_ => throw ActorErrors.ToException(reason))), reason);
            var reducer = new Actor<TransitionSnapshot<int>>(new TransitionLogic<int>((_, _, _) => throw ActorErrors.ToException(reason), 0)); Verify(reducer, reason, () => reducer.Send(new("FAIL")));
            Verify(new Actor<MachineSnapshot<int>>(new StateMachine<int>(new(), _ => throw ActorErrors.ToException(reason))), reason);
            Verify(new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>((_, _) => throw ActorErrors.ToException(reason))] }, _ => 0)), reason);
            Verify(new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(_ => throw ActorErrors.ToException(reason))), reason);
        }
        return Task.CompletedTask;
    }
    private sealed class ObservableFailure(object? reason) : IObservable<int>
    {
        private sealed class Subscription : IDisposable { public void Dispose() { } }
        public IDisposable Subscribe(IObserver<int> observer) { observer.OnError(ActorErrors.ToException(reason)); return new Subscription(); }
    }
    private static Task ObservableValues()
    {
        var snapshots = new List<JsonElement>();
        foreach (var reason in Values())
        {
            var actor = new Actor<ObservableSnapshot<int>>(new ObservableLogic<int>(_ => new ObservableFailure(reason))); Verify(actor, reason);
            snapshots.Add(JsonSerializer.Deserialize<JsonElement>(SnapshotJson.Serialize(actor.GetPersistedSnapshot())));
        }
        Observations["observable"] = snapshots; return Task.CompletedTask;
    }
    private sealed class Observer(Action<Exception> onError) : IObserver<CallbackSnapshot>
    {
        public void OnNext(CallbackSnapshot value) { }
        public void OnCompleted() { }
        public void OnError(Exception error) => onError(error);
    }
    private static Task ObserverBoundaries()
    {
        foreach (var reason in Values())
        {
            var reports = new ErrorHandlingTests.Reports(); var original = new CallbackLogic(_ => throw ActorErrors.ToException(reason));
            var actor = new Actor<CallbackSnapshot>(original, options: new() { ErrorReporter = reports }); var seen = new List<Exception>();
            using var bcl = actor.Subscribe(new Observer(seen.Add)); using var missing = actor.Subscribe(_ => { }); actor.Start();
            Equal(1, reports.Values.Count); Same(reason, reports.Values[0]); Same(reason, ActorErrors.GetValue(seen.Single()));
            if (reason is Exception) Same(reason, seen[0]);
            using var late = actor.Subscribe(_ => { }); Equal(2, reports.Values.Count); Same(reason, reports.Values[1]); actor.Stop();
            var listener = new Actor<CallbackSnapshot>(original, options: new() { ErrorReporter = reports }); using var throwing = listener.Subscribe(onError: _ => throw ActorErrors.ToException(reason));
            listener.Start(); Equal(3, reports.Values.Count); Same(reason, reports.Values[2]); listener.Stop();
        }
        return Task.CompletedTask;
    }
    private static async Task CancellationReasons()
    {
        foreach (var reason in Values())
        {
            for (var phase = 0; phase < 2; phase++)
            {
                using var cancellation = new CancellationTokenSource(); var actor = new Actor<TransitionSnapshot<int>>(new TransitionLogic<int>((context, _, _) => context, 0)).Start();
                if (phase == 0) cancellation.Cancel();
                var pending = ActorTasks.WaitForAsync(actor, _ => false, new() { CancellationToken = cancellation.Token, CancellationReason = reason });
                if (phase == 1) cancellation.Cancel();
                await Rejected(pending, reason).ConfigureAwait(false); Equal(false, pending.IsCanceled); actor.Stop();
            }
        }
    }
    private static Task ReporterOwnership()
    {
        var inherited = new ErrorHandlingTests.Reports(); var local = new ErrorHandlingTests.Reports();
        var root = new Actor<TransitionSnapshot<int>>(new TransitionLogic<int>((context, _, _) => context, 0), options: new() { ErrorReporter = inherited }).Start();
        var child = new Actor<CallbackSnapshot>(new CallbackLogic(scope => { scope.Receive(_ => scope.Emit(new("FAIL"))); return null; }), options: new() { Parent = root, ErrorReporter = local }).Start();
        var grandchild = new Actor<CallbackSnapshot>(new CallbackLogic(scope => { scope.Receive(_ => scope.Emit(new("FAIL"))); return null; }), options: new() { Parent = child }).Start();
        using var first = child.On("FAIL", _ => throw ActorErrors.ToException("local")); using var second = grandchild.On("FAIL", _ => throw ActorErrors.ToException("system"));
        // Emission callbacks exercise each reporter without stopping or relaying an actor failure.
        child.Send(new("trigger")); grandchild.Send(new("trigger"));
        Equal(1, local.Values.Count); Equal<object?>("local", local.Values[0]); Equal(1, inherited.Values.Count); Equal<object?>("system", inherited.Values[0]);
        grandchild.Send(new("xstate.stop")); child.Send(new("xstate.stop"));
        var machine = new StateMachine<int>(new() { Invoke = [new() { Source = ActorSource.From(new CallbackLogic(_ => throw ActorErrors.ToException("child"))), OnError = [new()] }] }, _ => 0);
        var parent = new Actor<MachineSnapshot<int>>(machine, options: new() { ErrorReporter = inherited });
        var invoked = parent.GetSnapshot().Children.Values.Single() ?? throw new InvalidOperationException("Child missing.");
        using var missing = invoked.Subscribe(_ => { }); parent.Start(); Equal(2, inherited.Values.Count); Equal<object?>("child", inherited.Values[1]); Equal(1, local.Values.Count); parent.Stop(); root.Stop();
        return Task.CompletedTask;
    }
}
