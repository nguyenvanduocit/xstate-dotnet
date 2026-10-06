using System.Runtime.CompilerServices;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class PromiseLogicTests
{
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Func<Task> run) => cases.Add(("packages/core/test/actorLogic.test.ts::promise logic (fromPromise) > " + title, run));
        Case("should interpret a promise", Interpret);
        Case("should resolve", () => Resolve(false));
        Case("should resolve (observer .next)", () => Resolve(true));
        Case("should complete (observer .complete)", Complete);
        Case("should not execute when reading initial state", Initial);
        Case("should persist an unresolved promise", PersistPending);
        Case("should persist a resolved promise", () => PersistDone(false));
        Case("should not invoke a resolved promise again", () => PersistDone(true));
        Case("should have access to the system", () => Scope(false));
        Case("should have reference to self", () => Scope(true));
        Case("should abort when stopping", Abort);
        Case("should not abort when stopped if promise is resolved/rejected", NotAbortDone);
        Case("should not reuse the same signal for different actors with same logic", () => ParallelSignals(false));
        Case("should not reuse the same signal for different actors with same logic and id", () => ParallelSignals(true));
        Case("should not reuse the same signal for the same actor when restarted", Restart);
        cases.Add(("packages/core/test/input.test.ts::input > should create a promise with input", Input));
        cases.Add(("packages/core/test/emit.test.ts::event emitter > events can be emitted from promise logic", Emit));
        cases.Add(("packages/core/test/actor.test.ts::spawning promises > should be able to spawn a promise", () => Spawn(false)));
        cases.Add(("packages/core/test/actor.test.ts::spawning promises > should be able to spawn a referenced promise", () => Spawn(true)));
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("completed tasks publish active before done and run before timers", MicrotaskOrder));
        cases.Add(("promise rejection retains the original Exception and clears input", Reject));
        cases.Add(("promise creator throws synchronously without aborting its signal", CreatorError));
        cases.Add(("stopped promises ignore late success and late failure", LateCompletion));
        cases.Add(("stopped promise actor and logic are collectable while Task stays pending", PendingCapture));
        cases.Add(("promise completion cancels signal registrations without abort", RegistrationRelease));
        cases.Add(("promise stop before start never calls creator", StopBeforeStart));
        cases.Add(("pure promise snapshots never start a task", Pure));
        cases.Add(("promise cancellation keeps the token usable until work ends", CancellationLifetime));
        cases.Add(("rejected Task snapshots restore without invoking the creator again", RestoreRejected));
    }
    private sealed record Count(int Value);
    private sealed record EmittedMessage(string Msg);
    private static Task<T> Later<T>(T result, double delay)
    {
        var completion = new TaskCompletionSource<T>();
        new RealClock().SetTimeout(() => completion.TrySetResult(result), delay);
        return completion.Task;
    }
    private static Task<int> Sleep(double delay) => Later(0, delay);
    private static async Task Interpret()
    {
        var actor = new Actor<PromiseSnapshot<string>>(new PromiseLogic<string>(_ => Later("hello", 10))).Start();
        var snapshot = await ActorTasks.WaitForAsync(actor, value => Equals(value.Output, "hello")).ConfigureAwait(false);
        Equal<object?>("hello", snapshot.Output);
    }
    private sealed class Observer(TaskCompletionSource completion) : IObserver<PromiseSnapshot<int>>
    {
        public void OnNext(PromiseSnapshot<int> value) { if (Equals(value.Output, 42)) completion.TrySetResult(); }
        public void OnCompleted() { }
        public void OnError(Exception error) => completion.TrySetException(error);
    }
    private static async Task Resolve(bool observer)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(_ => Task.FromResult(42)));
        if (observer) actor.Subscribe(new Observer(completion));
        else actor.Subscribe(value => { if (Equals(value.Output, 42)) completion.TrySetResult(); }, error => completion.TrySetException(ActorErrors.ToException(error)));
        actor.Start();
        await completion.Task.ConfigureAwait(false);
    }
    private static async Task Complete()
    {
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(_ => Task.FromResult(42))).Start();
        var snapshot = await ActorTasks.WaitForAsync(actor, value => Equals(value.Output, 42)).ConfigureAwait(false);
        Equal<object?>(42, snapshot.Output);
    }
    private static Task Initial()
    {
        var called = false;
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(_ => { called = true; return Task.FromResult(42); }));
        actor.GetSnapshot();
        Equal(false, called);
        actor.Stop();
        return Task.CompletedTask;
    }
    private static async Task PersistPending()
    {
        var logic = new PromiseLogic<int>(_ => Later(42, 10));
        var actor = new Actor<PromiseSnapshot<int>>(logic);
        object? persisted = null;
        ActorRuntime.Run(() => { actor.Start(); persisted = actor.GetPersistedSnapshot(); actor.Stop(); });
        var restored = new Actor<PromiseSnapshot<int>>(logic, options: new() { Snapshot = persisted }).Start();
        await Sleep(20).ConfigureAwait(false);
        Equal<object?>(42, restored.GetSnapshot().Output);
    }
    private static async Task PersistDone(bool counter)
    {
        var created = 0;
        var logic = new PromiseLogic<int>(_ => { created++; return Task.FromResult(counter ? created : 42); });
        var actor = new Actor<PromiseSnapshot<int>>(logic).Start();
        await Sleep(5).ConfigureAwait(false);
        var persisted = actor.GetPersistedSnapshot() as PromiseSnapshot<int> ?? throw new InvalidOperationException("Wrong persisted type.");
        Equal(SnapshotStatus.Done, persisted.Status);
        Equal<object?>(counter ? 1 : 42, persisted.Output);
        Equal<object?>(null, persisted.Input);
        Equal<object?>(null, persisted.Failure);
        Equal(true, persisted.HasOutput);
        Equal(1, created);
        var restored = new Actor<PromiseSnapshot<int>>(logic, options: new() { Snapshot = persisted }).Start();
        Equal<object?>(counter ? 1 : 42, restored.GetSnapshot().Output);
        Equal(1, created);
    }
    private static async Task Scope(bool self)
    {
        var calls = 0;
        var logic = new PromiseLogic<int>(args =>
        {
            if (self) { Action<MachineEvent> send = args.Self.Send; Equal(true, send.Target is IActor); }
            else Equal(true, args.System is not null);
            calls++;
            return Task.FromResult(42);
        });
        var actor = new Actor<PromiseSnapshot<int>>(logic).Start();
        Equal(1, calls);
        await ActorTasks.ToPromiseAsync(actor).ConfigureAwait(false);
    }
    private static async Task Abort()
    {
        var called = 0;
        var pending = new TaskCompletionSource<int>();
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(args => { args.Signal.Register(() => called++); return pending.Task; })).Start();
        actor.Stop();
        var unrelated = new TaskCompletionSource<int>();
        unrelated.SetResult(42);
        await unrelated.Task.ConfigureAwait(false);
        Equal(1, called);
        pending.SetResult(0);
    }
    private static async Task NotAbortDone()
    {
        var resolved = new TaskCompletionSource<int>();
        var rejected = new TaskCompletionSource<int>();
        var resolvedCalls = 0;
        var rejectedCalls = 0;
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(args => { args.Signal.Register(() => resolvedCalls++); return resolved.Task; })).Start();
        resolved.SetResult(42);
        await ActorTasks.WaitForAsync(actor, value => value.Status == SnapshotStatus.Done).ConfigureAwait(false);
        actor.Stop();
        Equal(0, resolvedCalls);
        var second = new Actor<PromiseSnapshot<object?>>(new PromiseLogic<object?>(async args =>
        {
            args.Signal.Register(() => rejectedCalls++);
            try { await rejected.Task.ConfigureAwait(false); }
            catch (InvalidOperationException) { return null; }
            return null;
        })).Start();
        rejected.SetException(new InvalidOperationException("50"));
        try { await rejected.Task.ConfigureAwait(false); } catch (InvalidOperationException error) { Equal("50", error.Message); }
        await ActorTasks.WaitForAsync(second, value => value.Status == SnapshotStatus.Done).ConfigureAwait(false);
        second.Stop();
        Equal(0, rejectedCalls);
    }
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Key, TransitionConfig<int> Value)[] entries) =>
        entries.ToDictionary(x => x.Key, x => (IReadOnlyList<TransitionConfig<int>>)new[] { x.Value }, StringComparer.Ordinal);
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Value)[] entries) =>
        entries.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    private static async Task ParallelSignals(bool sameId)
    {
        var pending = new List<TaskCompletionSource<int>>();
        var calls = new List<int>();
        var logic = new PromiseLogic<int>(args =>
        {
            var index = calls.Count;
            calls.Add(0);
            var completion = new TaskCompletionSource<int>();
            pending.Add(completion);
            args.Signal.Register(() => calls[index]++);
            return completion.Task;
        });
        var machine = new StateMachine<int>(new()
        {
            Kind = StateKind.Parallel, States = States(("p1", new()
            {
                Initial = "running", States = States(("running", new()
                {
                    Invoke = [new() { Source = ActorSource.From(logic), Id = sameId ? "p" : "p1" }],
                    On = On(("CANCEL_1", new() { Target = ["canceled"] }))
                }), ("canceled", new()))
            }), ("p2", new()
            {
                Initial = "running", States = States(("running", new()
                {
                    Invoke = [new() { Source = ActorSource.From(logic), Id = sameId ? "p" : "p2", OnDone = [new() { Target = ["done"] }] }]
                }), ("done", new()))
            }))
        }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        Equal(2, pending.Count);
        ActorRuntime.Run(() => { actor.Send(new("CANCEL_1")); pending[0].SetResult(42); pending[1].SetResult(42); });
        await Task.WhenAll(ActorTasks.WaitForAsync(actor, value => value.Matches("p1.canceled")),
            ActorTasks.WaitForAsync(actor, value => value.Matches("p2.done"))).ConfigureAwait(false);
        Equal(1, calls[0]);
        Equal(0, calls[1]);
        actor.Stop();
    }
    private static async Task Restart()
    {
        var pending = new List<TaskCompletionSource<int>>();
        var calls = new List<int>();
        var logic = new PromiseLogic<int>(args =>
        {
            var index = calls.Count;
            calls.Add(0);
            var completion = new TaskCompletionSource<int>();
            pending.Add(completion);
            args.Signal.Register(() => calls[index]++);
            return completion.Task;
        });
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new()
        {
            Initial = "running", States = States(("running", new()
            {
                Invoke = [new() { Source = ActorSource.From(logic), Id = "p", OnDone = [new() { Target = ["done"] }] }],
                On = On(("cancel", new() { Target = ["canceled"] }))
            }), ("done", new() { On = On(("restart", new() { Target = ["running"] })) }),
                ("canceled", new() { On = On(("restart", new() { Target = ["running"] })) }))
        }, _ => 0)).Start();
        await ActorTasks.WaitForAsync(actor, value => value.Matches("running")).ConfigureAwait(false);
        pending[0].SetResult(42);
        await ActorTasks.WaitForAsync(actor, value => value.Matches("done")).ConfigureAwait(false);
        Equal(0, calls[0]);
        actor.Send(new("restart"));
        await ActorTasks.WaitForAsync(actor, value => value.Matches("running")).ConfigureAwait(false);
        actor.Send(new("cancel"));
        await ActorTasks.WaitForAsync(actor, value => value.Matches("canceled")).ConfigureAwait(false);
        pending[1].SetResult(42);
        await pending[1].Task.ConfigureAwait(false);
        Equal(1, calls[1]);
        actor.Stop();
    }
    private static async Task Input()
    {
        var actor = new Actor<PromiseSnapshot<Count>>(new PromiseLogic<Count>(args => Task.FromResult(args.Input as Count ?? throw new InvalidOperationException("Input missing."))), new Count(42)).Start();
        await ActorRuntime.YieldAsync().ConfigureAwait(false);
        Equal<object?>(new Count(42), actor.GetSnapshot().Output);
    }
    private static async Task Emit()
    {
        var seen = new List<MachineEvent>();
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(args => { args.Emit(new("emitted", new EmittedMessage("hello"))); return Task.FromResult(0); }));
        using var subscription = actor.On("emitted", seen.Add);
        actor.Start();
        Equal(1, seen.Count);
        Equal("emitted", seen[0].Type);
        Equal<object?>(new EmittedMessage("hello"), seen[0].Payload);
        await ActorTasks.ToPromiseAsync(actor).ConfigureAwait(false);
    }
    private static async Task Spawn(bool named)
    {
        var source = ActorSource.From(new PromiseLogic<string>(_ => Task.FromResult("response")));
        var machine = new StateMachine<IActor?>(new()
        {
            Id = "promise", Initial = "idle", States = new Dictionary<string, StateConfig<IActor?>>(StringComparer.Ordinal)
            {
                ["idle"] = new()
                {
                    Entry = [MachineActions.Assign<IActor?>(args => args.Spawn(named ? ActorSource.Named("somePromise") : source, "my-promise"))],
                    On = new Dictionary<string, IReadOnlyList<TransitionConfig<IActor?>>>(StringComparer.Ordinal)
                    {
                        ["xstate.done.actor.my-promise"] = [new() { Target = ["success"], Guard = MachineGuards.Predicate<IActor?>((_, ev) => ev.Payload is ActorDoneData { Output: "response" }) }]
                    }
                },
                ["success"] = new() { Kind = StateKind.Final }
            }
        }, _ => null, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["somePromise"] = source });
        var actor = new Actor<MachineSnapshot<IActor?>>(machine);
        var completion = ActorTasks.ToPromiseAsync(actor);
        actor.Start();
        await completion.ConfigureAwait(false);
    }
    private static async Task MicrotaskOrder()
    {
        var seen = new List<string>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(_ => Task.FromResult(42)));
        actor.Subscribe(value => seen.Add(value.Status.ToString()));
        ActorRuntime.Run(() =>
        {
            actor.Start();
            Equal(SnapshotStatus.Active, actor.GetSnapshot().Status);
            seen.Add("turn");
            new RealClock().SetTimeout(() => { seen.Add("timer"); finished.TrySetResult(); }, 0);
        });
        await finished.Task.ConfigureAwait(false);
        Equal("Active,turn,Done,timer", string.Join(',', seen));
    }
    private static async Task Reject()
    {
        var failure = new InvalidOperationException("rejected");
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(_ => Task.FromException<int>(failure)), new Count(7));
        var seen = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.Subscribe(onError: error => seen.TrySetResult(ActorTaskTests.RequireException(error)));
        actor.Start();
        Equal(failure, await seen.Task.ConfigureAwait(false));
        Equal(failure, actor.GetSnapshot().Failure);
        Equal<object?>(null, actor.GetSnapshot().Input);
        Equal(false, actor.GetSnapshot().HasOutput);
    }
    private static Task CreatorError()
    {
        var aborts = 0;
        var failure = new InvalidOperationException("creator failed");
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(args => { args.Signal.Register(() => aborts++); throw failure; }), new Count(7));
        Exception? seen = null;
        actor.Subscribe(onError: error => seen = ActorTaskTests.RequireException(error));
        actor.Start();
        Equal(failure, seen);
        Equal(0, aborts);
        Equal<object?>(new Count(7), actor.GetSnapshot().Input);
        return Task.CompletedTask;
    }
    private static async Task LateCompletion()
    {
        foreach (var fail in new[] { false, true })
        {
            var task = new TaskCompletionSource<int>();
            var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(_ => task.Task)).Start();
            var emissions = 0;
            actor.Subscribe(_ => emissions++, _ => emissions++);
            actor.Stop();
            if (fail) task.SetException(new InvalidOperationException("late")); else task.SetResult(42);
            await ActorRuntime.YieldAsync().ConfigureAwait(false);
            Equal(0, emissions);
            Equal(SnapshotStatus.Stopped, actor.GetSnapshot().Status);
            Equal(false, actor.GetSnapshot().HasOutput);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Actor, WeakReference Logic) StoppedPending(Task<int> pending)
    {
        var logic = new PromiseLogic<int>(_ => pending);
        var actor = new Actor<PromiseSnapshot<int>>(logic).Start();
        actor.Stop();
        return (new(actor), new(logic));
    }
    private static Task PendingCapture()
    {
        var pending = new TaskCompletionSource<int>();
        var weak = StoppedPending(pending.Task);
        for (var i = 0; i < 3 && (weak.Actor.IsAlive || weak.Logic.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, weak.Actor.IsAlive);
        Equal(false, weak.Logic.IsAlive);
        pending.SetResult(42);
        GC.KeepAlive(pending);
        return Task.CompletedTask;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Actor<PromiseSnapshot<int>> Actor, WeakReference Payload) RegisteredCapture()
    {
        WeakReference? weak = null;
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(args =>
        {
            var payload = new byte[4096];
            weak = new(payload);
            args.Signal.Register(() => GC.KeepAlive(payload));
            return Task.FromResult(42);
        })).Start();
        return (actor, weak ?? throw new InvalidOperationException("Creator did not run."));
    }
    private static async Task RegistrationRelease()
    {
        var (actor, weak) = RegisteredCapture();
        await ActorTasks.ToPromiseAsync(actor).ConfigureAwait(false);
        for (var i = 0; i < 3 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, weak.IsAlive);
        GC.KeepAlive(actor);
    }
    private static Task StopBeforeStart()
    {
        var calls = 0;
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(_ => { calls++; return Task.FromResult(42); }));
        actor.Stop();
        Equal(0, calls);
        return Task.CompletedTask;
    }
    private static async Task CancellationLifetime()
    {
        var pending = new TaskCompletionSource<int>();
        CancellationToken signal = default;
        var actor = new Actor<PromiseSnapshot<int>>(new PromiseLogic<int>(args => { signal = args.Signal; return pending.Task; })).Start();
        actor.Stop();
        Equal(true, signal.IsCancellationRequested);
        Equal(true, signal.WaitHandle.WaitOne(0));
        pending.SetResult(42);
        await ActorRuntime.YieldAsync().ConfigureAwait(false);
        Equal(SnapshotStatus.Stopped, actor.GetSnapshot().Status);
    }
    private static async Task RestoreRejected()
    {
        var failure = new InvalidOperationException("rejected");
        var created = 0;
        var logic = new PromiseLogic<int>(_ => { created++; return Task.FromException<int>(failure); });
        var actor = new Actor<PromiseSnapshot<int>>(logic);
        var completion = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.Subscribe(onError: error => completion.TrySetResult(ActorTaskTests.RequireException(error)));
        actor.Start();
        Equal(failure, await completion.Task.ConfigureAwait(false));
        var persisted = actor.GetPersistedSnapshot();
        var restored = new Actor<PromiseSnapshot<int>>(logic, options: new() { Snapshot = persisted });
        Exception? seen = null;
        restored.Subscribe(onError: error => seen = ActorTaskTests.RequireException(error));
        restored.Start();
        Equal(failure, seen);
        Equal(1, created);
    }
    private static Task Pure()
    {
        var calls = 0;
        var result = ActorTransitions.Initial(new PromiseLogic<int>(_ => { calls++; return Task.FromResult(42); }), new Count(1));
        Equal(0, calls);
        Equal(0, result.Actions.Count);
        Equal<object?>(new Count(1), result.Snapshot.Input);
        Equal(SnapshotStatus.Active, result.Snapshot.Status);
        return Task.CompletedTask;
    }
}

