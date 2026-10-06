using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ObservableLogicTests;
namespace XStatePort.Tests;

internal static class MachinePersistenceTests
{
    private sealed record Count(int Value);
    private sealed record RefContext(IActor? Ref);
    private sealed record Deep(string Prop);
    private sealed record Input(Deep Deep);
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Type, TransitionConfig<int> Value)[] values) => values.ToDictionary(v => v.Type, v => (IReadOnlyList<TransitionConfig<int>>)new[] { v.Value }, StringComparer.Ordinal);
    private static StateMachine<int> Machine(StateConfig<int> config, IReadOnlyDictionary<string, ActorSource>? actors = null) => new(config, _ => 0, actors: actors);
    private static PersistedMachineSnapshot<T> Persist<T>(Actor<MachineSnapshot<T>> actor) => actor.GetPersistedSnapshot() as PersistedMachineSnapshot<T> ?? throw new InvalidOperationException("Expected machine snapshot data.");
    private static MachineSnapshot<int> Child(Actor<MachineSnapshot<int>> actor, string id) => actor.GetSnapshot().Children[id]?.GetSnapshot() as MachineSnapshot<int> ?? throw new InvalidOperationException("Machine child missing.");
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Sync(string file, string title, Action run) => cases.Add(("packages/core/test/" + file + ".test.ts::" + title, () => { run(); return Task.CompletedTask; }));
        void Core(string title, Action run) => Sync("actorLogic", "machine logic > " + title, run);
        cases.Add(("packages/core/test/actorLogic.test.ts::machine logic > should persist a machine", PersistTree));
        Core("should persist and restore a nested machine", Nested);
        Core("should return the initial persisted state of a non-started actor", Initial);
        Core("the initial state of a child is available before starting the parent", ChildInitial);
        Core("should not invoke an actor if it is missing in persisted state", MissingChild);
        Core("should persist a spawned actor with referenced src", NamedSpawn);
        Core("should not persist a spawned actor with inline src", RejectInline);
        Sync("actorLogic", "callback logic (fromCallback) > should persist the input of a callback", CallbackInput);
        Sync("actor", "actors > should not restart a completed observable", () => Observable(false));
        Sync("actor", "actors > should not restart a completed event observable", () => Observable(true));
        void Rehydrate(string title, Action run) => Sync("rehydration", "rehydration > " + title, run);
        Rehydrate("using state value > should be able to use `hasTag` immediately", () => StateValue(false));
        Rehydrate("using state value > should not call exit actions when machine gets stopped immediately", () => StateValue(true));
        Rehydrate("using state value > should error on incompatible state value (shallow)", () => InvalidValue(false));
        Rehydrate("using state value > should error on incompatible state value (deep)", () => InvalidValue(true));
        Rehydrate("should not replay actions when starting from a persisted state", NoReplay);
        Rehydrate("should be able to stop a rehydrated child", StopChild);
        Rehydrate("a rehydrated active child should be registered in the system", () => Registered(false, false));
        Rehydrate("a rehydrated done child should not be registered in the system", () => Registered(true, false));
        Rehydrate("a rehydrated done child should not re-notify the parent about its completion", () => Registered(true, true));
        Rehydrate("should be possible to persist a rehydrated actor that got its children rehydrated", PersistTwice);
        Rehydrate("should complete on a rehydrated final state", Final);
        cases.Add(("packages/core/test/rehydration.test.ts::rehydration > should error on a rehydrated error state", () => Error(false)));
        cases.Add(("packages/core/test/rehydration.test.ts::rehydration > shouldn't re-notify the parent about the error when rehydrating", () => Error(true)));
    }
    private static async Task PersistTree()
    {
        var childMachine = new StateMachine<Count>(new()
        {
            Initial = "start", States = new Dictionary<string, StateConfig<Count>>(StringComparer.Ordinal)
            {
                ["start"] = new() { Invoke = [new() { Id = "reducer", Source = ActorSource.From(new TransitionLogic<object?>((s, _, _) => s, initialContext: (object?)null)) }] }
            }
        }, _ => new(55));
        var machine = Machine(new()
        {
            Initial = "waiting", Invoke = [new()
            {
                Id = "a", Source = ActorSource.From(new PromiseLogic<int>(_ => Task.FromResult(42))),
                OnDone = [new() { Actions = [MachineActions.Raise<int>((_, _) => new("done"))] }]
            }, new() { Id = "b", Source = ActorSource.From(childMachine) }],
            States = States(("waiting", new() { On = On(("done", new() { Target = ["success"] })) }), ("success", new()))
        });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        await ActorTasks.WaitForAsync(actor, snapshot => snapshot.Matches("success")).ConfigureAwait(false);
        var persisted = Persist(actor);
        var promise = (PromiseSnapshot<int>)persisted.Children["a"].Snapshot;
        Equal(SnapshotStatus.Done, promise.Status);
        Equal<object?>(42, promise.Output); Equal<object?>(null, promise.Input); Equal<object?>(null, promise.Failure);
        var nested = (PersistedMachineSnapshot<Count>)persisted.Children["b"].Snapshot;
        Equal<object?>(new Count(55), nested.Context.Value);
        Equal(true, nested.Value.Matches("start"));
        Equal(SnapshotStatus.Active, ((IActorSnapshot)nested.Children["reducer"].Snapshot).Status);
        actor.Stop();
    }
    private static void Nested()
    {
        var child = Machine(new()
        {
            Initial = "a", States = States(("a", new() { On = On(("NEXT", new() { Target = ["b"] })) }),
                ("b", new() { On = On(("LAST", new() { Target = ["c"] })) }), ("c", new()))
        });
        var parent = Machine(new()
        {
            Initial = "idle", States = States(("idle", new() { On = On(("START", new() { Target = ["invoked"] })) }),
                ("invoked", new()
                {
                    Invoke = [new() { Id = "child", Source = ActorSource.From(child) }],
                    On = On(("NEXT", new() { Actions = [MachineActions.SendTo<int>("child", _ => new("NEXT"))] }),
                        ("LAST", new() { Actions = [MachineActions.SendTo<int>("child", _ => new("LAST"))] }))
                }))
        });
        var actor = new Actor<MachineSnapshot<int>>(parent).Start();
        actor.Send(new("START")); actor.Send(new("NEXT"));
        var restored = new Actor<MachineSnapshot<int>>(parent, options: new() { Snapshot = Persist(actor) }).Start();
        Equal(true, Child(restored, "child").Matches("b"));
        restored.Send(new("LAST"));
        Equal(true, Child(restored, "child").Matches("c"));
        actor.Stop(); restored.Stop();
    }
    private static void Initial()
    {
        var actor = new Actor<MachineSnapshot<int>>(Machine(new() { Initial = "idle", States = States(("idle", new())) }));
        Equal(true, Persist(actor).Value.Matches("idle"));
    }
    private static void ChildInitial()
    {
        var child = Machine(new() { Initial = "inner", States = States(("inner", new())) });
        var actor = new Actor<MachineSnapshot<int>>(Machine(new() { Invoke = [new() { Id = "child", Source = ActorSource.From(child) }] }));
        Equal(true, ((PersistedMachineSnapshot<int>)Persist(actor).Children["child"].Snapshot).Value.Matches("inner"));
    }
    private static void MissingChild()
    {
        var child = new StateMachine<string>(new(), args => (args.Input as Input ?? throw new InvalidOperationException("Required input missing.")).Deep.Prop);
        var parent = Machine(new()
        {
            Initial = "a", States = States(("a", new() { On = On(("NEXT", new() { Target = ["b"] })) }),
                ("b", new() { Invoke = [new() { Id = "child", Source = ActorSource.From(child), Input = args => args.Event.Payload }] }))
        });
        var actor = new Actor<MachineSnapshot<int>>(parent).Start();
        actor.Send(new("NEXT", new Input(new("value"))));
        var childSnapshot = actor.GetSnapshot().Children["child"]?.GetSnapshot() as MachineSnapshot<string> ?? throw new InvalidOperationException("Child missing.");
        Equal("value", childSnapshot.Context);
        var persisted = Persist(actor); persisted.Children.Remove("child");
        var restored = new Actor<MachineSnapshot<int>>(parent, options: new() { Snapshot = persisted }).Start();
        Equal(false, restored.GetSnapshot().Children.ContainsKey("child"));
        actor.Stop(); restored.Stop();
    }
    private static void NamedSpawn()
    {
        var reducer = ActorSource.From(new TransitionLogic<Count>((s, _, _) => s, new Count(42)));
        var machine = new StateMachine<RefContext>(new(), args => new(args.Spawn(ActorSource.Named("reducer"), "child")))
            .Provide(actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["reducer"] = reducer });
        var actor = new Actor<MachineSnapshot<RefContext>>(machine).Start();
        var persisted = Persist(actor);
        Equal(new Count(42), ((TransitionSnapshot<Count>)persisted.Children["child"].Snapshot).Context);
        var restored = new Actor<MachineSnapshot<RefContext>>(machine, options: new() { Snapshot = persisted }).Start();
        var snapshot = restored.GetSnapshot();
        Equal(true, ReferenceEquals(snapshot.Context.Ref, snapshot.Children["child"]));
        Equal(42, ((TransitionSnapshot<Count>)(snapshot.Context.Ref?.GetSnapshot() ?? throw new InvalidOperationException("Reference missing."))).Context.Value);
        actor.Stop(); restored.Stop();
    }
    private static void RejectInline()
    {
        var machine = new StateMachine<RefContext>(new(), args => new(args.Spawn(Machine(new()))));
        var actor = new Actor<MachineSnapshot<RefContext>>(machine).Start();
        try { actor.GetPersistedSnapshot(); throw new InvalidOperationException("Inline persistence was accepted."); }
        catch (InvalidOperationException error) { Equal("An inline child actor cannot be persisted.", error.Message); }
        actor.Stop();
    }
    private static void CallbackInput()
    {
        var seen = new List<object?>();
        var parent = Machine(new()
        {
            Initial = "a", States = States(("a", new() { On = On(("EV", new() { Target = ["b"] })) }),
                ("b", new() { Invoke = [new() { Source = ActorSource.Named("cb"), Input = args => args.Event.Payload }] }))
        }, new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["cb"] = ActorSource.From(new CallbackLogic(args => { seen.Add(args.Input); return null; })) });
        var actor = new Actor<MachineSnapshot<int>>(parent).Start(); actor.Send(new("EV", 13));
        var persisted = Persist(actor); actor.Stop(); seen.Clear();
        var restored = new Actor<MachineSnapshot<int>>(parent, options: new() { Snapshot = persisted }).Start();
        Equal(1, seen.Count); Equal<object?>(13, seen[0]); restored.Stop();
    }
    private static void Observable(bool events)
    {
        var calls = 0;
        var source = events ? ActorSource.From(new EventObservableLogic(_ => { calls++; return Of(new MachineEvent("TEST")); }))
            : ActorSource.From(new ObservableLogic<int>(_ => { calls++; return Of(42); }));
        var machine = Machine(new() { Invoke = [new() { Id = "observable", Source = source }] });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = Persist(actor) }).Start();
        Equal(1, calls); actor.Stop(); restored.Stop();
    }
    private static void StateValue(bool exits)
    {
        var calls = new List<string>();
        var machine = Machine(new()
        {
            Exit = exits ? [MachineActions.Effect<int>((_, _) => calls.Add("root"))] : [], Initial = "inactive",
            States = States(("inactive", new() { On = On(("NEXT", new() { Target = ["active"] })) }),
                ("active", new() { Tags = ["foo"], Exit = exits ? [MachineActions.Effect<int>((_, _) => calls.Add("active"))] : [] }))
        });
        var resolved = machine.ResolveState(XState.StateValue.Atomic("active"), 0);
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = resolved }).Start();
        if (exits) { actor.Stop(); Equal(0, calls.Count); }
        else { Equal(true, actor.GetSnapshot().HasTag("foo")); actor.Stop(); }
    }
    private static void InvalidValue(bool deep)
    {
        var config = new StateConfig<int> { Initial = "valid", States = States(("valid", new())) };
        var machine = Machine(deep ? new() { Initial = "parent", States = States(("parent", config)) } : config);
        try { machine.ResolveState(deep ? XState.StateValue.Parse("{\"parent\":\"invalid\"}") : XState.StateValue.Atomic("invalid"), 0); throw new InvalidOperationException("Invalid state accepted."); }
        catch (ArgumentException error) { Equal(true, error.Message.Contains("invalid", StringComparison.Ordinal)); }
    }
    private static void NoReplay()
    {
        var calls = 0;
        var machine = Machine(new() { Entry = [MachineActions.Effect<int>((_, _) => calls++)] });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); Equal(1, calls);
        var persisted = Persist(actor); actor.Stop();
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = persisted }).Start();
        Equal(1, calls); restored.Stop();
    }
    private static void StopChild() => ActorRuntime.Run(() =>
    {
        var machine = Machine(new()
        {
            Initial = "a", States = States(("a", new()
            {
                Invoke = [new() { Source = ActorSource.From(new PromiseLogic<int>(_ => Task.FromResult(11))), OnDone = [new() { Target = ["b"] }] }],
                On = On(("NEXT", new() { Target = ["c"] }))
            }), ("b", new()), ("c", new()))
        });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); var persisted = Persist(actor); actor.Stop();
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = persisted }).Start();
        restored.Send(new("NEXT")); Equal(true, restored.GetSnapshot().Matches("c")); restored.Stop();
    });
    private static void Registered(bool done, bool observe)
    {
        var events = 0;
        var source = ActorSource.From(Machine(new() { Kind = done ? StateKind.Final : StateKind.Atomic }));
        var machine = new StateMachine<int>(new() { On = observe ? On(("*", new() { Actions = [MachineActions.Effect<int>((_, _) => events++)] })) : On() },
            args => { args.Spawn(ActorSource.Named("foo"), systemId: "mySystemId"); return 0; }, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["foo"] = source });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); var persisted = Persist(actor); actor.Stop(); events = 0;
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = persisted }).Start();
        if (observe) Equal(0, events); else Equal(!done, restored.System.Get("mySystemId") is not null);
        restored.Stop();
    }
    private static void PersistTwice() => ActorRuntime.Run(() =>
    {
        var machine = Machine(new() { Invoke = [new() { Source = ActorSource.Named("foo") }] },
            new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["foo"] = ActorSource.From(new PromiseLogic<int>(_ => Task.FromResult(42))) });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = Persist(actor) }).Start();
        var children = Persist(restored).Children;
        Equal(1, children.Count); Equal("foo", children.Values.Single().Source.Name);
        actor.Stop(); restored.Stop();
    });
    private static void Final()
    {
        var machine = Machine(new() { Initial = "foo", States = States(("foo", new() { On = On(("NEXT", new() { Target = ["bar"] })) }), ("bar", new() { Kind = StateKind.Final })) });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("NEXT"));
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = Persist(actor) });
        var completed = 0; restored.Subscribe(onComplete: () => completed++); restored.Start(); Equal(1, completed);
    }
    private static async Task Error(bool handled)
    {
        var calls = 0;
        var failure = new InvalidOperationException("failure");
        var machine = Machine(new() { Invoke = [new()
        {
            Source = ActorSource.Named("failure"), OnError = handled ? [new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] }] : []
        }] }, new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["failure"] = ActorSource.From(new PromiseLogic<int>(_ => Task.FromException<int>(failure))) });
        var actor = new Actor<MachineSnapshot<int>>(machine); actor.Subscribe(onError: _ => { }); actor.Start();
        await Task.Delay(1).ConfigureAwait(false);
        await ActorRuntime.YieldAsync().ConfigureAwait(false);
        var persisted = Persist(actor); calls = 0;
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = persisted });
        var errors = 0; restored.Subscribe(onError: _ => errors++); restored.Start();
        if (handled) Equal(0, calls); else Equal(true, errors > 0);
        actor.Stop(); restored.Stop();
    }
}
