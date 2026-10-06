using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class InterpreterLifecycleTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string group, string title, Action run) => cases.Add(("packages/core/test/interpreter.test.ts::interpreter > " + (group.Length == 0 ? "" : group + " > ") + title, () => { run(); return Task.CompletedTask; }));
        void Async(string group, string title, Func<Task> run) => cases.Add(("packages/core/test/interpreter.test.ts::interpreter > " + (group.Length == 0 ? "" : group + " > ") + title, run));
        Async("initial state", "initially spawned actors should not be spawned when reading initial state", DeferredSpawn);
        Case("initial state", "does not execute actions from a restored state", () => Restored(false));
        Case("initial state", "should not execute actions that are not part of the actual persisted state", () => Restored(true));
        Case("subscribing", "should not notify subscribers of the current state upon subscription (subscribe)", Subscribe);
        Case("activities (deprecated)", "should start activities", () => Activity("start"));
        Case("activities (deprecated)", "should stop activities", () => Activity("transition"));
        Case("activities (deprecated)", "should stop activities upon stopping the service", () => Activity("stop"));
        Case("activities (deprecated)", "should restart activities from a compound state", RestoreActivity);
        Case("", "should not throw an error if an event is sent to an uninitialized interpreter", Uninitialized);
        Async("", "should defer events sent to an uninitialized service", DeferredEvents);
        Case("", "should throw an error if initial state sent to interpreter is invalid", InvalidInitial);
        Case("", "should not update when stopped", Stopped);
        Async(".send()", "can send events with a string", () => Send("string"));
        Async(".send()", "can send events with an object", () => Send("object"));
        Async(".send()", "can send events with an object with payload", () => Send("payload"));
        Async(".send()", "should receive and process all events sent simultaneously", Simultaneous);
        Case(".start()", "should initialize the service", () => Start(false));
        Case(".start()", "should not reinitialize a started service", () => Start(true));
        Case(".start()", "should be able to be initialized at a custom state", () => Custom("state"));
        Case(".start()", "should be able to be initialized at a custom state value", () => Custom("value"));
        Case(".start()", "should be able to resolve a custom initialized state", () => Custom("compound"));
        Async(".stop()", "should cancel delayed events", CancelDelayed);
        Async(".stop()", "should not execute transitions after being stopped", StoppedTransition);
        Case(".stop()", "should not throw when sending an unserializable event to a stopped actor", Circular);
        Case(".stop()", "stopping a not-started interpreter should not crash", StopBeforeStart);
        Case(".unsubscribe()", "should remove transition listeners", Unsubscribe);
        Case("transient states", "should transition in correct order", () => Transient(false));
        Case("transient states", "should transition in correct order when there is a condition", () => Transient(true));
    }
    private static StateMachine<int> Light() => new(new() { Id = "light", Initial = "green", States = States<int>(
        ("green", new() { Entry = [MachineActions.Raise<int>(new MachineEvent("TIMER"), new() { Id = "TIMER1", Delay = MachineDelays.From<int>(10) })], On = On<int>(("TIMER", new() { Target = ["yellow"] }), ("KEEP_GOING", new() { Actions = [MachineActions.Cancel<int>("TIMER1")] })) }),
        ("yellow", new() { Entry = [MachineActions.Raise<int>(new MachineEvent("TIMER"), new() { Delay = MachineDelays.From<int>(10) })], On = On<int>(("TIMER", new() { Target = ["red"] })) }),
        ("red", new() { After = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["10"] = [new() { Target = ["green"] }] } })) }, _ => 0);
    private static async Task DeferredSpawn()
    {
        var calls = 0; var child = new PromiseLogic<int>(_ => { calls++; return new TaskCompletionSource<int>().Task; });
        var machine = new StateMachine<IActor?>(new() { Initial = "idle", States = States<IActor?>(("idle", new() { Entry = [MachineActions.Assign<IActor?>(args => args.Spawn(child))] })) }, _ => null);
        var actor = new Actor<MachineSnapshot<IActor?>>(machine);
        try { Equal(0, calls); actor.GetSnapshot(); actor.GetSnapshot(); actor.GetSnapshot(); Equal(0, calls); actor.Start(); await Task.Delay(100).ConfigureAwait(false); Equal(1, calls); Results["deferredSpawn"] = calls; } finally { actor.Stop(); }
    }
    private static void Restored(bool transient)
    {
        var called = false; var effect = MachineActions.Effect<int>((_, _) => called = true);
        var config = transient ? new StateConfig<int> { Initial = "a", States = States<int>(("a", new() { Entry = [effect], Always = [new() { Target = ["b"] }] }), ("b", new())) } : new StateConfig<int> { Initial = "green", States = States<int>(("green", new() { On = On<int>(("TIMER", new() { Target = ["yellow"], Actions = [effect] })) }), ("yellow", new() { On = On<int>(("TIMER", new() { Target = ["red"] })) }), ("red", new() { On = On<int>(("TIMER", new() { Target = ["green"] })) })) };
        var machine = new StateMachine<int>(config, _ => 0); var original = new Actor<MachineSnapshot<int>>(machine).Start();
        try
        {
            if (!transient) original.Send(new("TIMER")); called = false; if (transient) Equal("b", original.GetSnapshot().Value.AtomicValue);
            var persisted = original.GetPersistedSnapshot(); var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = persisted }).Start();
            try { Equal(false, called); Results[transient ? "restoreTransient" : "restoreAction"] = new { called, state = restored.GetSnapshot().Value.AtomicValue }; } finally { restored.Stop(); }
        }
        finally { original.Stop(); }
    }
    private static void Subscribe()
    {
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "active", States = States<int>(("active", new())) }, _ => 0)).Start(); var calls = 0;
        using var subscription = actor.Subscribe(_ => calls++); Equal(0, calls); Results["subscribe"] = calls; actor.Stop();
    }
    private static void Activity(string mode)
    {
        var starts = 0; var stops = 0; var machine = new StateMachine<int>(new() { Id = mode == "stop" ? "stopActivity" : "activity", Initial = "on", States = States<int>(("on", new() { Invoke = [new() { Source = ActorSource.Named("myActivity") }], On = On<int>(("TURN_OFF", new() { Target = ["off"] })) }), ("off", new())) }, _ => 0,
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["myActivity"] = ActorSource.From(new CallbackLogic(_ => { starts++; return () => stops++; })) });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try { if (mode == "start") Equal(1, starts); else { Equal(0, stops); if (mode == "stop") actor.Stop(); else actor.Send(new("TURN_OFF")); Equal(1, stops); } Results["activity:" + mode] = new { starts, stops }; } finally { actor.Stop(); }
    }
    private static void RestoreActivity()
    {
        var active = false; var machine = new StateMachine<int>(new() { Initial = "inactive", States = States<int>(("inactive", new() { On = On<int>(("TOGGLE", new() { Target = ["active"] })) }),
            ("active", new() { Invoke = [new() { Source = ActorSource.Named("blink") }], On = On<int>(("TOGGLE", new() { Target = ["inactive"] })), Initial = "A", States = States<int>(("A", new() { On = On<int>(("SWITCH", new() { Target = ["B"] })) }), ("B", new() { On = On<int>(("SWITCH", new() { Target = ["A"] })) })) })) }, _ => 0,
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["blink"] = ActorSource.From(new CallbackLogic(_ => { active = true; return () => active = false; })) });
        var original = new Actor<MachineSnapshot<int>>(machine).Start(); original.Send(new("TOGGLE")); original.Send(new("SWITCH")); var persisted = original.GetPersistedSnapshot(); original.Stop(); active = false;
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = persisted }).Start(); try { Equal(true, active); Results["restoreActivity"] = new { active, state = JsonSerializer.Deserialize<JsonElement>(restored.GetSnapshot().Value.ToJson()) }; } finally { restored.Stop(); }
    }
    private static void Uninitialized()
    {
        var actor = new Actor<MachineSnapshot<int>>(Light()); actor.Send(new("SOME_EVENT")); Results["uninitialized"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing."); actor.Stop();
    }
    private static async Task DeferredEvents()
    {
        var machine = new StateMachine<int>(new() { Id = "defer", Initial = "a", States = States<int>(("a", new() { On = On<int>(("NEXT_A", new() { Target = ["b"] })) }), ("b", new() { On = On<int>(("NEXT_B", new() { Target = ["c"] })) }), ("c", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); MachineSnapshot<int>? current = null; var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var seen = new List<string?>(); using var subscription = actor.Subscribe(value => { current = value; seen.Add(value.Value.AtomicValue); }, onComplete: () => completed.TrySetResult());
        try { actor.Send(new("NEXT_A")); actor.Send(new("NEXT_B")); Equal<MachineSnapshot<int>?>(null, current); actor.Start(); await completed.Task.ConfigureAwait(false); Results["deferredEvents"] = seen; } finally { actor.Stop(); }
    }
    private static void InvalidInitial()
    {
        var machine = new StateMachine<int>(new() { Id = "fetchMachine", Initial = "create", States = States<int>(("edit", new() { Initial = "idle", States = States<int>(("idle", new() { On = On<int>(("FETCH", new() { Target = ["pending"] })) }), ("pending", new())) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); var snapshot = actor.GetSnapshot(); Equal(SnapshotStatus.Error, snapshot.Status); var message = RequireException(snapshot.Failure).Message; Equal("Initial state node \"create\" not found on parent state node #fetchMachine", message); Results["invalidInitial"] = message; actor.Stop();
    }
    private static string Warning(IActor actor, string type) => $"Event \"{type}\" was sent to stopped actor \"{actor.Id} ({actor.SessionId})\". This actor has already reached its final state, and will not transition.\nEvent: {{\"type\":\"{type}\"}}";
    private static void Stopped()
    {
        var warnings = new List<string>(); var actor = new Actor<MachineSnapshot<int>>(Light(), options: new() { Clock = new SimulatedClock(), Warning = warnings.Add }).Start(); actor.Send(new("TIMER")); Equal("yellow", actor.GetSnapshot().Value.AtomicValue); actor.Stop(); actor.Send(new("TIMER")); Equal("yellow", actor.GetSnapshot().Value.AtomicValue); Equal(1, warnings.Count); Equal(Warning(actor, "TIMER"), warnings[0]); Results["stopped"] = warnings[0].Replace(actor.SessionId, "<actor>", StringComparison.Ordinal);
    }
    private static async Task Send(string mode)
    {
        var machine = new StateMachine<int>(new() { Id = "send", Initial = "inactive", States = States<int>(("inactive", new() { On = On<int>(("EVENT", new() { Target = ["active"], Guard = MachineGuards.Predicate<int>((_, ev) => ev.Payload is 42) }), ("ACTIVATE", new() { Target = ["active"] })) }), ("active", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor, () => actor.Send(mode == "payload" ? new("EVENT", 42) : new("ACTIVATE"))).ConfigureAwait(false); Results["send:" + mode] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing.");
    }
    private static async Task Simultaneous()
    {
        var machine = new StateMachine<int>(new() { Id = "toggle", Initial = "inactive", States = States<int>(("fail", new()), ("inactive", new() { On = On<int>(("INACTIVATE", new() { Target = ["fail"] }), ("ACTIVATE", new() { Target = ["active"] })) }), ("active", new() { On = On<int>(("INACTIVATE", new() { Target = ["success"] })) }), ("success", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); await Complete(actor, () => { actor.Send(new("ACTIVATE")); actor.Send(new("INACTIVATE")); }).ConfigureAwait(false); Results["simultaneous"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing.");
    }
    private static void Start(bool twice)
    {
        var contexts = 0; var entries = 0; var entry = MachineActions.Effect<int>((_, _) => entries++); var config = twice ? new StateConfig<int> { Entry = [entry] } : new StateConfig<int> { Initial = "foo", States = States<int>(("foo", new())), Entry = [entry] };
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => { contexts++; return 0; })); actor.Start(); if (twice) actor.Start();
        try { Equal(1, contexts); Equal(1, entries); if (!twice) Equal(true, actor.GetSnapshot().Matches("foo")); Results[twice ? "startTwice" : "start"] = new { contexts, entries }; } finally { actor.Stop(); }
    }
    private static void Custom(string mode)
    {
        var config = new StateConfig<int> { Id = mode == "compound" ? "start" : null, Initial = "foo", States = States<int>(("foo", mode == "compound" ? new() { Initial = "one", States = States<int>(("one", new())) } : new()), ("bar", new())) };
        var machine = new StateMachine<int>(config, _ => 0); var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = machine.ResolveState(StateValue.Atomic(mode == "compound" ? "foo" : "bar"), 0) });
        var expected = mode == "compound" ? StateValue.Parse("{\"foo\":\"one\"}") : StateValue.Atomic("bar"); var before = actor.GetSnapshot().Value.ToJson(); Equal(true, actor.GetSnapshot().Matches(expected)); actor.Start();
        try { Equal(true, actor.GetSnapshot().Matches(expected)); Results["custom:" + mode] = new { before = JsonSerializer.Deserialize<JsonElement>(before), after = JsonSerializer.Deserialize<JsonElement>(actor.GetSnapshot().Value.ToJson()) }; } finally { actor.Stop(); }
    }
    private static async Task After(RealClock clock, double delay, Action setup)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); long timer = 0;
        try { ActorRuntime.Run(() => { setup(); timer = clock.SetTimeout(() => done.TrySetResult(), delay); }); await done.Task.ConfigureAwait(false); } finally { clock.ClearTimeout(timer); }
    }
    private static async Task CancelDelayed()
    {
        var called = false; var machine = new StateMachine<int>(new() { Id = "delayed", Initial = "foo", States = States<int>(("foo", new() { After = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["50"] = [new() { Target = ["bar"], Actions = [MachineActions.Effect<int>((_, _) => called = true)] }] } }), ("bar", new())) }, _ => 0);
        var clock = new RealClock(); var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Clock = clock });
        try { await After(clock, 60, () => { actor.Start(); actor.Stop(); }).ConfigureAwait(false); Equal(false, called); Results["cancelDelayed"] = called; } finally { actor.Stop(); }
    }
    private static async Task StoppedTransition()
    {
        var called = false; var warnings = new List<string>(); var machine = new StateMachine<int>(new() { Initial = "waiting", States = States<int>(("waiting", new() { On = On<int>(("TRIGGER", new() { Target = ["active"] })) }), ("active", new() { Entry = [MachineActions.Effect<int>((_, _) => called = true)] })) }, _ => 0);
        var clock = new RealClock(); var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Clock = clock, Warning = warnings.Add });
        try { await After(clock, 10, () => { actor.Start(); actor.Stop(); actor.Send(new("TRIGGER")); }).ConfigureAwait(false); Equal(false, called); Equal(1, warnings.Count); Equal(Warning(actor, "TRIGGER"), warnings[0]); Results["stoppedTransition"] = new { called, warning = warnings[0].Replace(actor.SessionId, "<actor>", StringComparison.Ordinal) }; } finally { actor.Stop(); }
    }
    private static void Circular()
    {
        var warnings = new List<string>(); var machine = new StateMachine<int>(new() { Initial = "waiting", States = States<int>(("waiting", new() { On = On<int>(("TRIGGER", new() { Target = ["active"] })) }), ("active", new())) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Warning = warnings.Add }).Start(); actor.Stop(); var payload = new Dictionary<string, object>(StringComparer.Ordinal); var ev = new MachineEvent("TRIGGER", payload); payload["self"] = ev; actor.Send(ev); Equal(1, warnings.Count); Results["circular"] = warnings[0].Replace(actor.SessionId, "<actor>", StringComparison.Ordinal);
    }
    private static void StopBeforeStart()
    {
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new())) }, _ => 0)); actor.Stop(); Results["stopBeforeStart"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing.");
    }
    private static void Unsubscribe()
    {
        var machine = new StateMachine<int>(new() { Id = "toggle", Initial = "inactive", States = States<int>(("inactive", new() { On = On<int>(("TOGGLE", new() { Target = ["active"] })) }), ("active", new() { On = On<int>(("TOGGLE", new() { Target = ["inactive"] })) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); var count = 0; using var sub = actor.Subscribe(_ => count++); var counts = new List<int>();
        try { Equal(0, count); counts.Add(count); actor.Send(new("TOGGLE")); Equal(1, count); counts.Add(count); actor.Send(new("TOGGLE")); Equal(2, count); counts.Add(count); sub.Dispose(); actor.Send(new("TOGGLE")); Equal(2, count); counts.Add(count); Results["unsubscribe"] = counts; } finally { actor.Stop(); }
    }
    private static void Transient(bool guarded)
    {
        var machine = new StateMachine<int>(new() { Id = "transient", Initial = "idle", States = States<int>(("idle", new() { On = On<int>(("START", new() { Target = ["transient"] })) }),
            ("transient", new() { Always = guarded ? [new() { Target = ["end"], Guard = MachineGuards.Named<int>("alwaysFalse") }, new() { Target = ["next"] }] : [new() { Target = ["next"] }] }), ("next", new() { On = On<int>(("FINISH", new() { Target = ["end"] })) }), ("end", new() { Kind = StateKind.Final })) }, _ => 0, guards: new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal) { ["alwaysFalse"] = MachineGuards.Predicate<int>((_, _) => false) });
        var actor = new Actor<MachineSnapshot<int>>(machine); var states = new List<string?>(); using var sub = actor.Subscribe(value => states.Add(value.Value.AtomicValue));
        try { actor.Start(); actor.Send(new("START")); Equal(2, states.Count); Equal("idle", states[0]); Equal("next", states[1]); Results[guarded ? "transientGuard" : "transient"] = states; } finally { actor.Stop(); }
    }
    private static void InitialValidation()
    {
        var calls = 0; var machine = new StateMachine<int>(new() { Id = "invalid", Initial = "missing", States = States<int>(("valid", new())) }, _ => { calls++; return 1; });
        var actor = new Actor<MachineSnapshot<int>>(machine); var failed = actor.GetSnapshot(); Equal(0, calls); Equal(SnapshotStatus.Error, failed.Status); Equal(false, failed.HasStateValue); Equal(false, failed.HasContext); Equal("{\"status\":\"error\",\"error\":{}}", SnapshotJson.Serialize(failed)); actor.Stop();
        var nested = new StateMachine<int>(new() { Id = "nested", Initial = "idle", States = States<int>(("idle", new() { On = On<int>(("ENTER", new() { Target = ["bad"] })) }), ("bad", new() { Initial = "absent", States = States<int>(("exists", new())) })) }, _ => 0);
        var runtime = new Actor<MachineSnapshot<int>>(nested).Start(); var errors = new List<object?>(); using var subscription = runtime.Subscribe(onError: errors.Add);
        try
        {
            Equal(SnapshotStatus.Active, runtime.GetSnapshot().Status); Equal("idle", runtime.GetSnapshot().Value.AtomicValue); runtime.Send(new("ENTER")); Equal(SnapshotStatus.Error, runtime.GetSnapshot().Status); Equal(1, errors.Count); Equal("idle", runtime.GetSnapshot().Value.AtomicValue);
            Equal("Initial state node \"absent\" not found on parent state node #nested.bad", RequireException(errors[0]).Message);
            Results["validation"] = new { contextCalls = calls, hasStateValue = failed.HasStateValue, hasContext = failed.HasContext, json = JsonSerializer.Deserialize<JsonElement>(SnapshotJson.Serialize(failed)), nestedState = runtime.GetSnapshot().Value.AtomicValue, nestedError = RequireException(errors[0]).Message };
        }
        finally { runtime.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("initial target validation is deferred until traversal and precedes context initialization", () => { InitialValidation(); return Task.CompletedTask; }));
        cases.Add(("interpreter lifecycle differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-interpreter-lifecycle.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
    }
}
