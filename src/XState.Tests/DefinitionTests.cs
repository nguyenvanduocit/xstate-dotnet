using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;
internal static class DefinitionTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Action Run)> cases) => cases.Add(("packages/core/test/definition.test.ts::definition > should provide invoke definitions", Invokes));
    private static void Invokes()
    {
        var machine = new StateMachine<int>(new() { Id = "invoke", Initial = "idle", Invoke = [new() { Source = ActorSource.Named("foo") }, new() { Source = ActorSource.Named("bar") }], States = States<int>(("idle", new())) }, _ => 0);
        Equal(2, machine.Root.Definition.Invoke.Count); Results["invokeCount"] = machine.Root.Definition.Invoke.Count;
    }
    private static object Transition(ITransitionDefinition transition) => new
    {
        source = "#" + transition.Source.Id, target = transition.Targets.Select(node => "#" + node.Id).ToArray(), eventType = transition.EventType,
        reenter = transition.Reenter, hasTarget = transition.HasTarget, guard = transition.Guard is not null,
        actions = transition.Actions.Select(action => action switch { ActionDefinition definition => definition.Type, MachineAction<int> original => original.Type, _ => throw new InvalidOperationException("Unknown action.") }).ToArray()
    };
    private static object Node(StateNodeDefinition<int> definition) => new
    {
        id = definition.Id, key = definition.Key, version = definition.Version, type = definition.Kind.ToString().ToLowerInvariant(),
        initial = Transition(definition.Initial), history = definition.History, order = definition.Order,
        states = definition.States.ToDictionary(pair => pair.Key, pair => Node(pair.Value), StringComparer.Ordinal),
        on = definition.On.ToDictionary(pair => pair.Key, pair => pair.Value.Select(Transition).ToArray(), StringComparer.Ordinal),
        transitions = definition.Transitions.Select(transition => Transition(transition)).ToArray(),
        entry = definition.Entry.Select(action => action ?? throw new InvalidOperationException("Null entry action.")).Select(action => new { type = action.Type, hasParams = action.HasParameters, parameters = action.Parameters is Delegate ? "function" : action.Parameters }).ToArray(),
        exit = definition.Exit.Select(action => action?.Type).ToArray(), hasMeta = definition.HasMeta, meta = definition.Meta, description = definition.Description,
        tags = definition.Tags, output = definition.Output is not null,
        invoke = definition.Invoke.Select(invoke => new { id = invoke.Id, src = invoke.Src, systemId = invoke.SystemId, input = invoke.Input is not null, done = invoke.OnDone.Count, error = invoke.OnError.Count, snapshot = invoke.OnSnapshot is not null }).ToArray()
    };
    private static void Tree()
    {
        var calls = 0; Func<int, MachineEvent, object?> dynamic = (_, _) => { calls++; return 42; };
        var action = MachineActions.Named<int>("dynamic", dynamic); var check = MachineGuards.Predicate<int>((_, _) => { calls++; return true; });
        Func<MachineActionArgs<int>, object?> input = _ => { calls++; return 42; };
        var machine = new StateMachine<int>(new()
        {
            Id = "root", Version = "v1", Initial = "idle", InitialActions = [MachineActions.Named<int>("initial")], InitialMeta = "initial meta", InitialDescription = "initial description",
            Description = "root description", Meta = null, Tags = ["root-tag"], Output = _ => { calls++; return 42; },
            Entry = [MachineActions.Named<int>("plain"), MachineActions.Named<int>("static", 42), MachineActions.Named<int>("nil", (object?)null), action],
            Invoke = [new() { Source = ActorSource.Named("foo"), Input = input, SystemId = "foo-system", OnDone = [new() { Target = [".done"] }] },
                new() { Id = "inline", Source = ActorSource.From(new CallbackLogic(_ => { calls++; return null; })), OnError = [new() { Target = [".idle"] }], OnSnapshot = [] }],
            States = States<int>(("idle", new()
            {
                Meta = new { role = "waiting" }, Tags = ["idle-tag"], Entry = [MachineActions.Effect<int>((_, _) => calls++, "enterIdle")],
                On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
                {
                    ["GO"] = [new() { Target = ["done"], Guard = check, Actions = [MachineActions.Assign<int>((context, _) => { calls++; return context + 1; })] }],
                    ["BLOCKED"] = [new()]
                },
                After = On<int>(("5", new() { Target = ["done"] }))
            }), ("history", new() { Kind = StateKind.History, History = HistoryKind.Deep }), ("done", new() { Kind = StateKind.Final, Output = _ => { calls++; return "done"; } }))
        }, _ => { calls++; return 0; });
        var definition = machine.Definition; Equal(0, calls);
        Results["treeJson"] = JsonSerializer.Deserialize<JsonElement>(MachineDefinitionJson.Serialize(definition)); Equal(0, calls);
        Equal(false, ReferenceEquals(definition, machine.Definition)); Equal(true, ReferenceEquals(definition.Initial.Source, machine.Root));
        Equal(true, ReferenceEquals(definition.Initial.Targets[0], machine.States["idle"]));
        Equal(true, ReferenceEquals(definition.States["idle"].Transitions[0].Guard, check));
        Equal(true, ReferenceEquals((definition.Entry[3] ?? throw new InvalidOperationException("Missing dynamic action.")).Parameters, dynamic)); Equal(true, ReferenceEquals(definition.Invoke[0].Input, input));
        Equal(true, ReferenceEquals(machine.States["idle"].On, definition.States["idle"].On));
        Equal(0, calls); Results["tree"] = Node(definition);
        Results["identity"] = new { fresh = !ReferenceEquals(definition, machine.Definition), source = ReferenceEquals(definition.Initial.Source, machine.Root), target = ReferenceEquals(definition.Initial.Targets[0], machine.States["idle"]), guard = ReferenceEquals(definition.States["idle"].Transitions[0].Guard, check), parameters = ReferenceEquals((definition.Entry[3] ?? throw new InvalidOperationException("Missing dynamic action.")).Parameters, dynamic), input = ReferenceEquals(definition.Invoke[0].Input, input), on = ReferenceEquals(machine.States["idle"].On, definition.States["idle"].On), calls };
        Results["initialMetadata"] = new { meta = definition.Initial.Meta, description = definition.Initial.Description };
    }
    private static void ActionKinds()
    {
        var calls = 0;
        var logic = ActorSource.From(new CallbackLogic(_ => { calls++; return null; }));
        MachineAction<int>[] actions = [MachineActions.Log<int>(), MachineActions.Log<int>(42), MachineActions.Log<int>(_ => { calls++; return 42; }),
            MachineActions.EnqueueActions<int>(_ => calls++), MachineActions.Emit<int>(new MachineEvent("notice")), MachineActions.Emit<int>(_ => { calls++; return new("notice"); }),
            MachineActions.Assign<int>((context, _) => { calls++; return context; }), MachineActions.Assign<int>(args => { calls++; return args.Context; }),
            MachineActions.SpawnChild<int>(logic), MachineActions.StopChild<int>("child"), MachineActions.StopChild<int>(_ => "child"), MachineActions.StopChild<int>(_ => (IActor?)null),
            MachineActions.Raise<int>(new MachineEvent("GO")), MachineActions.Raise<int>(_ => new("GO"), new() { Delay = MachineDelays.From<int>(10) }), MachineActions.Raise<int>((_, _) => new("GO")),
            MachineActions.SendTo<int>("child", new MachineEvent("GO")), MachineActions.SendTo<int>("child", _ => new("GO"), new() { Delay = MachineDelays.From<int>(10) }),
            MachineActions.SendTo<int>(_ => (IActor?)null, _ => new("GO")), MachineActions.SendParent<int>(new MachineEvent("GO")),
            MachineActions.ForwardTo<int>("child"), MachineActions.ForwardTo<int>(_ => (IActor?)null), MachineActions.Cancel<int>("timer"), MachineActions.Cancel<int>(_ => "timer"),
            MachineActions.Named<int>("plain"), MachineActions.Named<int>("nil", (object?)null), MachineActions.Named<int>("dynamic", (_, _) => { calls++; return 1; }),
            MachineActions.Effect<int>((_, _) => calls++), MachineActions.Effect<int>((_, _) => calls++, "named"), MachineActions.Effect<int>((_, _) => calls++, "(anonymous)")];
        var definitions = actions.Select(action => action.Definition).ToArray(); Equal(0, calls);
        Equal("xstate.log,xstate.log,xstate.log,xstate.enqueueActions,xstate.emit,xstate.emit,xstate.assign,xstate.assign,xstate.spawnChild,xstate.stopChild,xstate.stopChild,xstate.stopChild,xstate.raise,xstate.raise,xstate.raise,xstate.sendTo,xstate.sendTo,xstate.sendTo,xstate.sendTo,xstate.sendTo,xstate.sendTo,xstate.cancel,xstate.cancel,plain,nil,dynamic,,named,(anonymous)", string.Join(',', definitions.Select(action => action.Type)));
        Equal(false, definitions[23].HasParameters); Equal(true, definitions[24].HasParameters); Equal<object?>(null, definitions[24].Parameters); Equal(true, definitions[25].Parameters is Delegate);
        Results["actionKinds"] = definitions.Select(action => new { type = action.Type, hasParams = action.HasParameters, parameters = action.Parameters is Delegate ? "function" : action.Parameters }).ToArray();
    }
    private static void Forbidden()
    {
        var observations = new List<object>();
        foreach (var blocked in new[] { true, false })
        {
            var calls = 0;
            var machine = new StateMachine<int>(new() { Initial = "a", On = On<int>(("EV", new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] })), States = States<int>(("a", new()
            {
                On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["EV"] = blocked ? [new()] : [] }
            })) }, _ => 0);
            var actor = new Actor<MachineSnapshot<int>>(machine).Start();
            try
            {
                var definition = machine.Definition.States["a"]; var can = actor.GetSnapshot().Can(new("EV"));
                Equal(!blocked, can); actor.Send(new("EV")); Equal(blocked ? 0 : 1, calls);
                Equal(blocked, definition.On.ContainsKey("EV")); Equal(blocked ? 1 : 0, definition.Transitions.Count);
                observations.Add(new { blocked, can, calls, on = definition.On.ContainsKey("EV"), transitions = definition.Transitions.Count });
            }
            finally { actor.Stop(); }
        }
        Results["forbidden"] = observations;
    }
    private static void JsonMetadata()
    {
        var calls = 0;
        MachineAction<int>[] actions = [MachineActions.Named<int>("plain"), MachineActions.Named<int>("nil", (object?)null),
            MachineActions.Named<int>("static", new { count = 42 }), MachineActions.Named<int>("dynamic", (_, _) => { calls++; return 1; }),
            MachineActions.Effect<int>((_, _) => calls++, "work"), MachineActions.Assign<int>((context, _) => { calls++; return context; })];
        var machine = new StateMachine<int>(new()
        {
            Id = "json", Initial = "a", InitialMeta = null, InitialActions = actions, Entry = actions, Exit = actions,
            Invoke = [new() { Source = ActorSource.Named("source"), Input = _ => { calls++; return 1; }, SystemId = "system",
                OnDone = [new() { Target = [".b"] }], OnError = [new() { Target = [".a"] }],
                OnSnapshot = [new() { Target = [".a"], Meta = null, Actions = [], Reenter = false }, new() { Actions = actions }] }],
            States = States<int>(("a", new() { On = On<int>(
                ("EV", new() { Target = ["b"], Actions = actions, Meta = null, Description = "transition", Guard = MachineGuards.Named<int>("check", (_, _) => { calls++; return 1; }) }),
                ("PLAIN", new() { Guard = MachineGuards.Named<int>("check") }),
                ("NIL", new() { Target = [], Guard = MachineGuards.Named<int>("check", (object?)null) }),
                ("STATIC", new() { Guard = MachineGuards.Named<int>("check", 42) }),
                ("FN", new() { Guard = MachineGuards.Predicate<int>((_, _) => { calls++; return true; }) })) }), ("b", new()))
        }, _ => { calls++; return 0; });
        var json = MachineDefinitionJson.Serialize(machine);
        Equal(json, MachineDefinitionJson.Serialize(machine.Root)); Equal(json, MachineDefinitionJson.Serialize(machine.Definition));
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        Equal(0, calls); Equal(false, root.TryGetProperty("meta", out _));
        Equal(JsonValueKind.Null, root.GetProperty("initial").GetProperty("meta").ValueKind);
        Equal(false, root.GetProperty("initial").TryGetProperty("reenter", out _));
        Equal("work", root.GetProperty("entry")[4].GetProperty("type").GetString());
        var transition = root.GetProperty("states").GetProperty("a").GetProperty("on").GetProperty("EV")[0];
        Equal(JsonValueKind.Null, transition.GetProperty("actions")[4].ValueKind);
        Equal(JsonValueKind.Null, transition.GetProperty("actions")[5].ValueKind);
        Equal("plain", transition.GetProperty("actions")[0].GetString());
        Equal(JsonValueKind.Null, transition.GetProperty("meta").ValueKind);
        Equal(false, transition.GetProperty("guard").TryGetProperty("params", out _));
        Equal(false, root.GetProperty("invoke")[0].TryGetProperty("onDone", out _));
        Equal(false, root.GetProperty("invoke")[0].TryGetProperty("onError", out _));
        Equal(0, root.GetProperty("invoke")[0].GetProperty("onSnapshot")[0].GetProperty("actions").GetArrayLength());
        Results["metadataJson"] = root.Clone();
    }
    private static void LiteralOutputs()
    {
        var value = new { result = 42 };
        var config = new StateConfig<int>() { Initial = "a", OutputValue = value, States = States<int>(("a", new() { Kind = StateKind.Final, OutputValue = new { something = "else" } })) };
        var machine = new MachineSetup<int>().CreateMachine(config, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try
        {
            Equal(SnapshotStatus.Done, actor.GetSnapshot().Status); Equal(true, ReferenceEquals(value, actor.GetSnapshot().Output));
            Equal(true, ReferenceEquals(value, machine.Definition.OutputValue));
            using var document = JsonDocument.Parse(MachineDefinitionJson.Serialize(machine)); Results["literalJson"] = document.RootElement.Clone();
            Equal(42, document.RootElement.GetProperty("output").GetProperty("result").GetInt32());
            Equal("else", document.RootElement.GetProperty("states").GetProperty("a").GetProperty("output").GetProperty("something").GetString());
        }
        finally { actor.Stop(); }
        object? propagated = new object();
        var nested = new StateMachine<int>(new() { Initial = "group", States = States<int>(("group", new()
        {
            Initial = "done", States = States<int>(("done", new() { Kind = StateKind.Final, OutputValue = value })),
            OnDone = [new() { Actions = [MachineActions.Effect<int>((_, ev) => propagated = ev.Payload)] }]
        })) }, _ => 0);
        var nestedActor = new Actor<MachineSnapshot<int>>(nested).Start();
        try { Equal(true, ReferenceEquals(value, propagated)); } finally { nestedActor.Stop(); }
        var explicitNull = new StateMachine<int>(new() { Kind = StateKind.Final, OutputValue = null }, _ => 0);
        var nullActor = new Actor<MachineSnapshot<int>>(explicitNull).Start();
        try
        {
            using var document = JsonDocument.Parse(MachineDefinitionJson.Serialize(explicitNull));
            Equal(JsonValueKind.Null, document.RootElement.GetProperty("output").ValueKind);
            Equal<object?>(null, nullActor.GetSnapshot().Output);
            using var snapshot = JsonDocument.Parse(SnapshotJson.Serialize(nullActor.GetSnapshot()));
            Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("output").ValueKind);
            Results["literalNullJson"] = document.RootElement.Clone();
        }
        finally { nullActor.Stop(); }
        var ignored = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { OutputValue = value })) }, _ => 0);
        Equal(false, ignored.States["a"].HasOutputValue);
        using var ignoredJson = JsonDocument.Parse(MachineDefinitionJson.Serialize(ignored)); Results["ignoredOutputJson"] = ignoredJson.RootElement.Clone();
        Equal(false, ignoredJson.RootElement.GetProperty("states").GetProperty("a").TryGetProperty("output", out _));
        Results["literalOutputIdentity"] = ReferenceEquals(value, propagated);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("machine definition tree preserves node identity and does not execute callbacks", () => { Tree(); return Task.CompletedTask; }));
        cases.Add(("action definitions retain builtin kinds and unresolved parameters without execution", () => { ActionKinds(); return Task.CompletedTask; }));
        cases.Add(("forbidden transitions differ from empty candidate arrays in definitions and ancestor selection", () => { Forbidden(); return Task.CompletedTask; }));
        cases.Add(("definition JSON preserves action representations metadata presence and invoke serialization without evaluating callbacks", () => { JsonMetadata(); return Task.CompletedTask; }));
        cases.Add(("literal machine and final state outputs preserve reference null propagation and setup copy", () => { LiteralOutputs(); return Task.CompletedTask; }));
        cases.Add(("definition differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-definitions.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
    }
}
