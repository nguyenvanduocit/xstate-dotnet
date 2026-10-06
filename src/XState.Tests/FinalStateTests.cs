using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class FinalStateTests
{
    private const string Prefix = "packages/core/test/final.test.ts::final states > ";
    private sealed record Empty;
    private sealed record CountContext(int Count);
    private sealed record SecretContext(string? RevealedSecret);
    private sealed record Secret(string Value);
    private sealed record SelfOutput(IActor SelfRef);
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] entries) => entries.ToDictionary(e => e.Key, e => e.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(params (string Key, TransitionConfig<T> Transition)[] entries) => entries.ToDictionary(e => e.Key, e => (IReadOnlyList<TransitionConfig<T>>)[e.Transition], StringComparer.Ordinal);
    private static Actor<MachineSnapshot<int>> Start(StateConfig<int> config, object? input = null) => new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0), input).Start();
    private static StateConfig<int> Final() => new() { Kind = StateKind.Final };
    private static StateConfig<int> Compound(string name, StateConfig<int> child) => new() { Initial = name, States = States((name, child)) };
    private static StateConfig<int> Parallel(string name, StateConfig<int> child) => new() { Kind = StateKind.Parallel, States = States((name, child)) };
    private static StateConfig<int> Region(string ev) => new() { Initial = "a", States = States(("a", new StateConfig<int> { On = On<int>((ev, new() { Target = ["b"] })) }), ("b", Final())) };
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string name, Action run) => cases.Add((Prefix + name, run));
        Case("output of a machine with a root state being final should be called with a \"xstate.done.state.ROOT_ID\" event", () => RootEvent(0));
        Case("should emit the \"xstate.done.state.*\" event when all nested states are in their final states", ParallelEvent);
        Case("should execute final child state actions first", ChildActions);
        Case("should only call data expression once when entering root's final state", OutputOnce);
        Case("output mapper should receive self", OutputSelf);
        Case("state output should be able to use context updated by the entry action of the reached final state", AssignedOutput);
        Case("root output should be called with a \"xstate.done.state.*\" event of the parallel root when a direct final child of that parallel root is reached", () => RootEvent(1));
        Case("root output should be called with a \"xstate.done.state.*\" event of the parallel root when a final child of its compound child is reached", () => RootEvent(2));
        Case("root output should be called with a \"xstate.done.state.*\" event of the parallel root when a final descendant is reached 2 parallel levels deep", () => RootEvent(3));
        Case("onDone of an outer parallel state should be called with its own \"xstate.done.state.*\" event when its direct parallel child completes", OuterDoneEvent);
        Case("onDone should not be called when the machine reaches its final state", SuppressedDone);
        Case("machine should not complete when a parallel child of a compound state completes", () =>
        {
            var actor = Start(Compound("a", Parallel("b", Compound("c", Final())))); Equal(SnapshotStatus.Active, actor.GetSnapshot().Status); actor.Stop();
        });
        Case("root output should only be called once when multiple parallel regions complete at once", () => MultipleCompletions(true));
        Case("onDone of a parallel state should only be called once when multiple parallel regions complete at once", () => MultipleCompletions(false));
        Case("should not resolve output of a final state if its parent is a parallel state", IgnoreParallelOutput);
        Case("should only call exit actions once when a child machine reaches its final state and sends an event to its parent that ends up stopping that child", () => ChildCancellation(false, true));
        Case("should deliver final outgoing events (from final entry action) to the parent before delivering the `xstate.done.actor.*` event", () => ChildCancellation(false, false));
        Case("should deliver final outgoing events (from root exit action) to the parent before delivering the `xstate.done.actor.*` event", () => ChildCancellation(true, false));
        Case("should be possible to complete with a null output (directly on root)", () => NullOutput(false));
        Case("should be possible to complete with a null output (resolving with final state's output)", () => NullOutput(true));
    }
    public static void RegisterAsync(List<(string Id, Func<Task> Run)> cases) => cases.Add((Prefix + "should call output expressions on nested final nodes", NestedOutput));
    private static void RootEvent(int depth)
    {
        var events = new List<MachineEvent>();
        StateConfig<int> config = depth switch
        {
            0 => new() { Kind = StateKind.Final, Output = Capture },
            1 => new() { Kind = StateKind.Parallel, States = States(("a", Final())), Output = Capture },
            2 => new() { Kind = StateKind.Parallel, States = States(("a", Compound("b", Final()))), Output = Capture },
            3 => new() { Kind = StateKind.Parallel, States = States(("a", Parallel("b", Compound("c", Final())))), Output = Capture },
            _ => throw new ArgumentOutOfRangeException(nameof(depth))
        };
        object? Capture(MachineOutputArgs<int> args) { events.Add(args.Event); return null; }
        var actor = Start(config, depth == 0 ? 42 : null);
        Equal(1, events.Count); Equal("xstate.done.state.(machine)", events[0].Type); Equal<object?>(null, events[0].Payload);
        Observations["root" + depth] = events.Select(e => e.Type).ToArray(); actor.Stop();
    }
    private static void ParallelEvent()
    {
        var calls = new List<string>();
        var actor = Start(new() { Id = "m", Initial = "foo", States = States<int>(("foo", new() { Kind = StateKind.Parallel,
            States = States(("first", Region("NEXT_1")), ("second", Region("NEXT_2"))),
            OnDone = [new() { Target = ["bar"], Actions = [MachineActions.Effect<int>((_, ev) => calls.Add(ev.Type))] }] }), ("bar", new())) });
        actor.Send(new("NEXT_1")); actor.Send(new("NEXT_2")); Equal(true, actor.GetSnapshot().Matches("bar")); Equal(true, calls.Contains("xstate.done.state.m.foo", StringComparer.Ordinal)); actor.Stop();
    }
    private static void ChildActions()
    {
        var actual = new List<string>();
        var actor = Start(new() { Initial = "foo", States = States<int>(("foo", new() { Initial = "bar", OnDone = [new() { Actions = [MachineActions.Effect<int>((_, _) => actual.Add("fooAction"))] }],
            States = States<int>(("bar", new() { Initial = "baz", OnDone = [new() { Target = ["barFinal"] }], States = States<int>(("baz", new() { Kind = StateKind.Final, Entry = [MachineActions.Effect<int>((_, _) => actual.Add("bazAction"))] })) }),
                ("barFinal", new() { Kind = StateKind.Final, Entry = [MachineActions.Effect<int>((_, _) => actual.Add("barAction"))] })) })) });
        Equal("bazAction,barAction,fooAction", string.Join(',', actual)); Observations["childActions"] = actual; actor.Stop();
    }
    private static async Task NestedOutput()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = new StateMachine<SecretContext>(new() { Initial = "secret", States = States<SecretContext>(
            ("secret", new() { Initial = "wait", States = States<SecretContext>(("wait", new() { On = On<SecretContext>(("REQUEST_SECRET", new() { Target = ["reveal"] })) }), ("reveal", new() { Kind = StateKind.Final, Output = _ => new Secret("the secret") })),
                OnDone = [new() { Target = ["success"], Actions = [MachineActions.Assign<SecretContext>((_, ev) => new(((Secret)(ev.Payload ?? throw new InvalidOperationException("Secret output missing."))).Value))] }] }),
            ("success", new() { Kind = StateKind.Final })) }, _ => new(null));
        var actor = new Actor<MachineSnapshot<SecretContext>>(machine);
        using var subscription = actor.Subscribe(onComplete: () =>
        {
            try { Equal(new SecretContext("the secret"), actor.GetSnapshot().Context); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        });
        actor.Start(); actor.Send(new("REQUEST_SECRET")); await completion.Task.ConfigureAwait(false); actor.Stop();
    }
    private static StateConfig<int> Completing(Func<MachineOutputArgs<int>, object?> output, Func<MachineOutputArgs<int>, object?>? childOutput = null) => new()
    {
        Initial = "start", States = States<int>(("start", new() { On = On<int>(("FINISH", new() { Target = ["end"] }), ("NEXT", new() { Target = ["end"] })) }),
            ("end", new() { Kind = StateKind.Final, Output = childOutput })), Output = output
    };
    private static void OutputOnce()
    {
        var calls = 0; var actor = Start(Completing(_ => { calls++; return null; })); actor.Send(new("FINISH", 1)); Equal(1, calls); actor.Stop();
    }
    private static void OutputSelf()
    {
        var actor = Start(new() { Initial = "done", States = States(("done", Final())), Output = args => new SelfOutput(args.Self) });
        var output = (SelfOutput)(actor.GetSnapshot().Output ?? throw new InvalidOperationException("Self output missing."));
        Action<MachineEvent> send = output.SelfRef.Send; Equal(true, send is not null); Equal(true, ReferenceEquals(actor, output.SelfRef)); actor.Stop();
    }
    private static void AssignedOutput()
    {
        var actual = new List<object?>();
        var machine = new StateMachine<CountContext>(new() { Initial = "a", States = States<CountContext>(("a", new() { Initial = "a1", States = States<CountContext>(
            ("a1", new() { On = On<CountContext>(("NEXT", new() { Target = ["a2"] })) }),
            ("a2", new() { Kind = StateKind.Final, Entry = [MachineActions.Assign<CountContext>((_, _) => new(1))], Output = args => args.Context.Count })),
            OnDone = [new() { Actions = [MachineActions.Effect<CountContext>((_, ev) => actual.Add(ev.Payload))] }] })) }, _ => new(0));
        var actor = new Actor<MachineSnapshot<CountContext>>(machine).Start(); actor.Send(new("NEXT")); Equal(true, actual.Contains(1)); Observations["assignedOutput"] = actual; actor.Stop();
    }
    private static void OuterDoneEvent()
    {
        var events = new List<MachineEvent>();
        var actor = Start(Compound("a", new() { Kind = StateKind.Parallel, States = States(("b", Parallel("c", Compound("d", Final())))), OnDone = [new() { Actions = [MachineActions.Effect<int>((_, ev) => events.Add(ev))] }] }));
        Equal(1, events.Count); Equal("xstate.done.state.(machine).a", events[0].Type); Equal<object?>(null, events[0].Payload); Observations["outerDone"] = events.Select(e => e.Type).ToArray(); actor.Stop();
    }
    private static void SuppressedDone()
    {
        var calls = 0; TransitionConfig<int>[] done = [new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] }];
        var actor = Start(new() { Kind = StateKind.Parallel, OnDone = done, States = States<int>(("a", new() { Kind = StateKind.Parallel, OnDone = done,
            States = States<int>(("b", new() { Initial = "c", OnDone = done, States = States(("c", Final())) })) })) });
        Equal(0, calls); Observations["suppressedDone"] = calls; actor.Stop();
    }
    private static void MultipleCompletions(bool root)
    {
        var calls = 0;
        var config = root ? new StateConfig<int> { Kind = StateKind.Parallel, States = States(("a", Final()), ("b", Final())), Output = _ => { calls++; return null; } }
            : Compound("a", new() { Kind = StateKind.Parallel, States = States(("b", Final()), ("c", Final())), OnDone = [new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] }] });
        var actor = Start(config); Equal(1, calls); Observations[root ? "multipleRoot" : "multipleDone"] = calls; actor.Stop();
    }
    private static void IgnoreParallelOutput()
    {
        var calls = 0;
        var actor = Start(Compound("A", new() { Kind = StateKind.Parallel, States = States<int>(("B", new() { Kind = StateKind.Final, Output = _ => { calls++; return null; } }), ("C", Compound("C1", new()))) }));
        Equal(0, calls); Observations["ignoredOutput"] = calls; actor.Stop();
    }
    private static void ChildCancellation(bool fromExit, bool spyExit)
    {
        var exits = 0;
        var outgoing = MachineActions.SendParent<int>(_ => new("CHILD_CANCELED"));
        var child = new StateMachine<int>(new() { Initial = "start", Exit = spyExit ? [MachineActions.Effect<int>((_, _) => exits++)] : fromExit ? [outgoing] : [],
            States = States<int>(("start", new() { On = On<int>(("CANCEL", new() { Target = ["canceled"] })) }), ("canceled", new() { Kind = StateKind.Final, Entry = fromExit ? [] : [outgoing] })) }, _ => 0);
        var actor = Start(new() { Initial = "start", States = States<int>(("start", new() { Invoke = [new() { Id = "child", Source = ActorSource.From(child), OnDone = [new() { Target = ["completed"] }] }],
            On = On<int>(("CHILD_CANCELED", new() { Target = ["canceled"] })) }), ("canceled", new()), ("completed", new())) });
        (actor.GetSnapshot().Children["child"] ?? throw new InvalidOperationException("Child missing.")).Send(new("CANCEL"));
        if (spyExit) Equal(1, exits); else Equal(true, actor.GetSnapshot().Matches("canceled"));
        Observations[spyExit ? "exitCount" : fromExit ? "outgoingExit" : "outgoingEntry"] = spyExit ? exits : actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Expected atomic state."); actor.Stop();
    }
    private static void NullOutput(bool nested)
    {
        var actor = Start(Completing(nested ? args => args.Event.Payload : _ => null, nested ? _ => null : null)); actor.Send(new("NEXT")); Equal<object?>(null, actor.GetSnapshot().Output); actor.Stop();
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("final state null output persists and restores without becoming absent", () => { OutputPersistence(); return Task.CompletedTask; }));
        cases.Add(("final output evaluation uses entry context before exit and preserves upstream call count", () => { EvaluationOrder(); return Task.CompletedTask; }));
        cases.Add(("output mapper errors preserve exception identity during initialization and transition", () => { OutputErrors(); return Task.CompletedTask; }));
        cases.Add(("explicit null resolved output survives status copies and restoration", () => { ResolvedOutput(); return Task.CompletedTask; }));
        cases.Add(("final state differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-final-state.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask; }));
    }
    private static void OutputPersistence()
    {
        var observations = new List<object>();
        foreach (var configured in new[] { false, true })
        {
            var machine = new StateMachine<Empty>(new() { Initial = "start", States = States<Empty>(("start", new() { On = On<Empty>(("NEXT", new() { Target = ["end"] })) }), ("end", new() { Kind = StateKind.Final })), Output = configured ? _ => null : null }, _ => new());
            var actor = new Actor<MachineSnapshot<Empty>>(machine).Start(); actor.Send(new("NEXT"));
            Equal(configured, actor.GetSnapshot().HasOutput);
            var json = SnapshotJson.Serialize(actor.GetPersistedSnapshot()); var liveJson = SnapshotJson.Serialize(actor.GetSnapshot());
            foreach (var text in new[] { json, liveJson })
            {
                using var document = JsonDocument.Parse(text); Equal(configured, document.RootElement.TryGetProperty("output", out var output)); if (configured) Equal(JsonValueKind.Null, output.ValueKind);
            }
            var restored = new Actor<MachineSnapshot<Empty>>(machine, options: new() { Snapshot = SnapshotJson.Parse(json) }).Start();
            var memory = new Actor<MachineSnapshot<Empty>>(machine, options: new() { Snapshot = actor.GetPersistedSnapshot() }).Start();
            Equal<object?>(null, restored.GetSnapshot().Output); Equal<object?>(null, memory.GetSnapshot().Output);
            Equal(configured, restored.GetSnapshot().HasOutput); Equal(configured, memory.GetSnapshot().HasOutput);
            observations.Add(new { configured, live = JsonSerializer.Deserialize<JsonElement>(liveJson), persisted = JsonSerializer.Deserialize<JsonElement>(json), restored = JsonSerializer.Deserialize<JsonElement>(SnapshotJson.Serialize(restored.GetSnapshot())) });
            actor.Stop(); restored.Stop(); memory.Stop();
        }
        Observations["persistence"] = observations;
    }
    private static void EvaluationOrder()
    {
        var trace = new List<string>(); var selfRefs = new List<IActor>();
        var machine = new StateMachine<int>(new() { Initial = "start", Output = args =>
        {
            selfRefs.Add(args.Self); trace.Add($"root:{args.Context}:{args.Event.Type}:{args.Event.Payload}"); return args.Event.Payload;
        }, Exit = [MachineActions.Assign<int>((_, _) => 2)], States = States<int>(
            ("start", new() { On = On<int>(("FINISH", new() { Target = ["end"] })) }),
            ("end", new() { Kind = StateKind.Final, Entry = [MachineActions.Assign<int>((_, _) => 1)], Output = args =>
            {
                selfRefs.Add(args.Self); trace.Add($"child:{args.Context}:{args.Event.Type}"); return args.Context;
            } })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("FINISH"));
        Equal("child:1:FINISH,child:1:FINISH,root:1:xstate.done.state.(machine).end:1", string.Join(',', trace));
        Equal(true, selfRefs.All(self => ReferenceEquals(self, actor))); Equal(2, actor.GetSnapshot().Context); Equal<object?>(1, actor.GetSnapshot().Output);
        Observations["evaluation"] = new { trace, context = actor.GetSnapshot().Context, output = actor.GetSnapshot().Output }; actor.Stop();
    }
    private static void OutputErrors()
    {
        var results = new List<object>();
        foreach (var initial in new[] { false, true })
        {
            var error = new InvalidOperationException("output failure"); var errors = new List<Exception>();
            var machine = new StateMachine<int>(initial ? new() { Kind = StateKind.Final, Output = _ => throw error } : Completing(_ => throw error), _ => 0);
            var actor = new Actor<MachineSnapshot<int>>(machine); using var subscription = actor.Subscribe(onError: failure => errors.Add(ActorTaskTests.RequireException(failure))); actor.Start(); if (!initial) actor.Send(new("FINISH"));
            Equal(SnapshotStatus.Error, actor.GetSnapshot().Status); Equal(true, ReferenceEquals(error, actor.GetSnapshot().Failure)); Equal(1, errors.Count); Equal(true, ReferenceEquals(error, errors[0]));
            results.Add(new { initial, errors = errors.Select(e => e.Message).ToArray(), status = "error" }); actor.Stop();
        }
        Observations["errors"] = results;
    }
    private static void ResolvedOutput()
    {
        var machine = new StateMachine<int>(new(), _ => 0);
        foreach (var present in new[] { false, true })
        {
            var resolved = machine.ResolveState(StateValue.Parse("{}"), 0, output: null, hasOutput: present);
            Equal(present, resolved.HasOutput);
            var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = resolved }).Start(); actor.Stop();
            Equal(present, actor.GetSnapshot().HasOutput); Equal(present, machine.GetPersistedSnapshot(actor.GetSnapshot()).HasOutput);
        }
    }

}

