using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;
internal static class MachineSetupTests
{
    private static void Metadata()
    {
        var schemas = new object(); var originalSchemas = new object(); var nested = new StateConfig<int>();
        var config = new StateConfig<int> { Id = "root", Version = "1.2", Schemas = originalSchemas, Initial = "a", States = States(("a", nested)), Meta = null };
        var setup = new MachineSetup<int>(schemas);
        var machine = setup.CreateMachine(config, _ => 0); var provided = machine.Provide();
        Equal(false, ReferenceEquals(config, machine.Config)); Equal(true, ReferenceEquals(originalSchemas, config.Schemas));
        Equal(true, ReferenceEquals(schemas, machine.Schemas)); Equal(true, ReferenceEquals(schemas, machine.Config.Schemas));
        Equal(true, ReferenceEquals(config.States, machine.Config.States)); Equal(true, ReferenceEquals(nested, machine.States["a"].Config));
        Equal(true, ReferenceEquals(machine.Config, provided.Config)); Equal("1.2", provided.Version); Equal(true, ReferenceEquals(schemas, provided.Schemas));
        Equal(true, ReferenceEquals(config, setup.CreateStateConfig(config)));
        Action<MachineActionArgs<int>> action = _ => { }; Equal(true, ReferenceEquals(action, setup.CreateAction(action)));
        var effect = MachineActions.Effect(action); Equal(true, ReferenceEquals(effect, setup.CreateAction(effect)));
        var actor = new Actor<MachineSnapshot<int>>(machine); Equal(true, actor.GetSnapshot().GetMeta().ContainsKey("root")); actor.Stop();
        Equal<object?>(null, new MachineSetup<int>().CreateMachine(config, _ => 0).Schemas);
        MachineConstructionTests.Results["metadata"] = new { copied = !ReferenceEquals(config, machine.Config), original = ReferenceEquals(originalSchemas, config.Schemas), nested = ReferenceEquals(nested, machine.States["a"].Config), version = provided.Version, schema = ReferenceEquals(schemas, provided.Schemas), identity = ReferenceEquals(config, setup.CreateStateConfig(config)), action = ReferenceEquals(action, setup.CreateAction(action)), explicitNullMeta = true, defaultOverrides = true };
    }
    private static void Registry(bool useSetup)
    {
        var trace = new List<string>(); var clock = new SimulatedClock();
        var actions = new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["work"] = MachineActions.Effect<int>((_, _) => trace.Add("original")) };
        var guards = new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal) { ["allowed"] = MachineGuards.Predicate<int>((_, _) => false) };
        var delays = new Dictionary<string, MachineDelay<int>>(StringComparer.Ordinal) { ["wait"] = MachineDelays.From<int>(10) };
        ActorSource Source(string name) => ActorSource.From(new CallbackLogic(_ => { trace.Add(name); return null; }));
        var actors = new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["worker"] = Source("old") };
        var config = new StateConfig<int> { On = On<int>(
            ("START", new() { Actions = [MachineActions.SpawnChild<int>(ActorSource.Named("worker"), "one")] }),
            ("GO", new() { Guard = MachineGuards.Named<int>("allowed"), Actions = [MachineActions.Named<int>("work"), MachineActions.Raise<int>(new MachineEvent("TIME"), new() { Delay = MachineDelays.Named<int>("wait") }), MachineActions.SpawnChild<int>(ActorSource.Named("worker"), "two")] }),
            ("TIME", new() { Actions = [MachineActions.Effect<int>((_, _) => trace.Add("time"))] })) };
        var machine = useSetup ? new MachineSetup<int>(actors: actors, actions: actions, guards: guards, delays: delays).CreateMachine(config, _ => 0)
            : new StateMachine<int>(config, _ => 0, actors: actors, actions: actions, guards: guards, delays: delays);
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Clock = clock }).Start();
        try
        {
            actor.Send(new("START")); actor.Send(new("GO")); Equal("old", string.Join(',', trace));
            actions["work"] = MachineActions.Effect<int>((_, _) => trace.Add("late")); guards["allowed"] = MachineGuards.Predicate<int>((_, _) => true);
            delays["wait"] = MachineDelays.From<int>(5); actors["worker"] = Source("new");
            actor.Send(new("GO")); clock.Increment(4); Equal("old,late,new", string.Join(',', trace)); clock.Increment(1); Equal("old,late,new,time", string.Join(',', trace));
            MachineConstructionTests.Results["registry:" + useSetup] = trace.ToArray();
        }
        finally { actor.Stop(); }
    }
    private static void Extend()
    {
        var trace = new List<string>(); var schemas = new object(); var actorSources = new Dictionary<string, ActorSource>(StringComparer.Ordinal);
        var baseActions = new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal)
        {
            ["work"] = MachineActions.Effect<int>((_, _) => trace.Add("base")), ["finish"] = MachineActions.Effect<int>((_, _) => trace.Add("finish")),
            ["checked"] = MachineActions.Effect<int>((_, _) => trace.Add("checked"))
        };
        var setup = new MachineSetup<int>(schemas, actorSources, baseActions,
            new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal) { ["allow"] = MachineGuards.Predicate<int>((_, _) => false) },
            new Dictionary<string, MachineDelay<int>>(StringComparer.Ordinal) { ["wait"] = MachineDelays.From<int>(10) });
        var extended = setup.Extend(actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["work"] = MachineActions.Effect<int>((_, _) => trace.Add("extended")) },
            guards: new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal) { ["allow"] = MachineGuards.Predicate<int>((_, _) => true) },
            delays: new Dictionary<string, MachineDelay<int>>(StringComparer.Ordinal) { ["wait"] = MachineDelays.From<int>(5) });
        // Extend clones actions/guards/delays but carries the actor registry and schemas by reference.
        baseActions["work"] = MachineActions.Effect<int>((_, _) => trace.Add("base-late"));
        actorSources["worker"] = ActorSource.From(new CallbackLogic(_ => { trace.Add("start"); return () => trace.Add("stop"); }));
        var config = new StateConfig<int> { Initial = "active", States = States<int>(("active", new()
        {
            Entry = [MachineActions.Named<int>("work")], Invoke = [new() { Source = ActorSource.Named("worker") }],
            On = On<int>(("CHECK", new() { Guard = MachineGuards.Named<int>("allow"), Actions = [MachineActions.Named<int>("checked")] })),
            After = On<int>(("wait", new() { Target = ["done"] }))
        }), ("done", new() { Kind = StateKind.Final, Entry = [MachineActions.Named<int>("finish")] })) };
        var states = new List<string>();
        foreach (var selected in new[] { setup, extended })
        {
            var clock = new SimulatedClock(); var machine = selected.CreateMachine(config, _ => 0); Equal(true, ReferenceEquals(schemas, machine.Schemas));
            var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Clock = clock }).Start();
            try { actor.Send(new("CHECK")); clock.Increment(5); states.Add(actor.GetSnapshot().Value.AtomicValue ?? "missing"); clock.Increment(5); Equal(SnapshotStatus.Done, actor.GetSnapshot().Status); }
            finally { actor.Stop(); }
        }
        Equal("active,done", string.Join(',', states)); Equal("start,base-late,finish,stop,start,extended,checked,finish,stop", string.Join(',', trace));
        MachineConstructionTests.Results["extend"] = new { states, trace };
    }
    private static void Aliases()
    {
        var setup = new MachineSetup<int>(); var trace = new List<string>(); var clock = new SimulatedClock();
        var source = ActorSource.From(new CallbackLogic(scope => { scope.Receive(ev => trace.Add(ev.Type)); return () => trace.Add("stop"); }));
        var child = setup.CreateStateConfig(new() { Entry = [setup.SpawnChild(source, "worker")], On = On<int>(
            ("GO", new() { Actions = [setup.Assign((context, _) => context + 1), setup.EnqueueActions(args => args.Enqueue.Add(setup.Emit(new MachineEvent("notice")))),
                setup.Log(args => args.Context, "count"), setup.SendTo("worker", new MachineEvent("PING")), setup.Raise(new MachineEvent("RAISED")),
                setup.Raise(new MachineEvent("LATE"), new() { Id = "later", Delay = MachineDelays.From<int>(10) }), setup.Cancel("later")] }),
            ("RAISED", new() { Actions = [setup.StopChild("worker")] }), ("LATE", new() { Actions = [MachineActions.Effect<int>((_, _) => trace.Add("unexpected"))] })) });
        var actor = new Actor<MachineSnapshot<int>>(setup.CreateMachine(child, _ => 0), options: new() { Clock = clock, Logger = args => trace.Add(string.Join(':', args)) });
        using var emission = actor.On("notice", _ => trace.Add("notice"));
        try { actor.Start().Send(new("GO")); clock.Increment(10); Equal(1, actor.GetSnapshot().Context); Equal("count:1,notice,PING,stop", string.Join(',', trace)); Equal(0, actor.GetSnapshot().Children.Count); Equal(0, clock.PendingCount); MachineConstructionTests.Results["aliases"] = trace; }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string id, Action run) => cases.Add((id, () => { run(); return Task.CompletedTask; }));
        Case("setup copies root configuration preserves metadata and identity helpers", Metadata);
        Case("machine implementation registries remain live references", () => Registry(false));
        Case("setup implementation registries remain live references", () => Registry(true));
        Case("setup extend merges implementations and preserves actor schema references", Extend);
        Case("setup bound actions preserve ordering scheduling emission and cleanup", Aliases);
    }
}
