using System.Text.Json;
using XState;
using XState.Graph;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static partial class TestModelTests
{
    private sealed record Empty;
    private sealed record ValueEvent(int Value);
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> Config)[] states) => states.ToDictionary(p => p.Key, p => p.Config, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(params (string Key, string Target)[] events) => events.ToDictionary(p => p.Key, p => (IReadOnlyList<TransitionConfig<T>>)[new() { Target = [p.Target] }], StringComparer.Ordinal);
    private static StateConfig<Empty> Node(params (string Key, string Target)[] events) => new() { On = On<Empty>(events) };
    private static StateMachine<Empty> Machine(params (string Key, StateConfig<Empty> Config)[] states) => new(new() { Initial = states[0].Key, States = States(states) }, _ => new());
    private static TestModel<MachineSnapshot<Empty>> Multi() => StateGraph.CreateTestModel(Machine(("a", Node(("EVENT", "b"))), ("b", Node(("EVENT", "c"))), ("c", Node(("EVENT", "d"), ("EVENT_2", "e"))), ("d", Node()), ("e", Node())));
    private static void Descriptions<T>(IReadOnlyList<TestPath<T>> paths, params string[] expected) where T : class, IActorSnapshot => Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(paths.Select(p => p.Description)));
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string file, string title, Func<Task> run) => cases.Add(("packages/core/src/graph/test/" + file + ".test.ts::" + title, run));
        void Sync(string file, string title, Action run) => Case(file, title, () => { run(); return Task.CompletedTask; });
        Case("testModel", "custom test models > tests any logic", () => Collatz(false));
        Case("testModel", "custom test models > tests states for any logic", () => Collatz(true));
        Case("states", "states > should test states by key", () => StateKeys(false));
        Case("states", "states > should test states by ID", () => StateKeys(true));
        Sync("forbiddenAttributes", "Forbidden attributes > Should not let you declare invocations on your test machine", () => Forbidden(new() { Invoke = [new() { Source = ActorSource.Named("myInvoke") }] }, "Invocations"));
        Sync("forbiddenAttributes", "Forbidden attributes > Should not let you declare after on your test machine", () => Forbidden(new() { After = new Dictionary<string, IReadOnlyList<TransitionConfig<Empty>>> { ["5000"] = [new() { Actions = [MachineActions.Effect<Empty>((_, _) => { })] }] } }, "After events"));
        Sync("forbiddenAttributes", "Forbidden attributes > Should not let you delayed actions on your machine", () => Forbidden(new() { Entry = [MachineActions.Raise<Empty>((_, _) => new("EVENT"), new() { Delay = MachineDelays.From<Empty>(1000) })] }, "Delayed actions"));
        Case("paths", "testModel.testPaths(...) > custom path generators can be provided", CustomGenerator);
        Sync("paths", "testModel.testPaths(...) > When the machine only has one path > Should only follow that path", () => Equal(1, StateGraph.CreateTestModel(Machine(("a", Node(("EVENT", "b"))), ("b", Node(("EVENT", "c"))), ("c", Node()))).GetShortestPaths().Count));
        Sync("paths", "testModel.testPaths(...) > getSimplePaths > Should dedup simple path paths", () => Equal(2, Multi().GetSimplePaths().Count));
        Sync("paths", "testModel.testPaths(...) > getSimplePaths > Should not dedup simple path paths if deduplicate: false", () => Equal(5, Multi().GetSimplePaths(new() { AllowDuplicatePaths = true }).Count));
        Sync("paths", "testModel.testPaths(...) > getSimplePaths > should support filtering disabled events", Filter);
        Sync("paths", "path.description > Should write a readable description including the target state and the path", () => Descriptions(Multi().GetShortestPaths(), "Reaches state \"d\": xstate.init → EVENT → EVENT → EVENT", "Reaches state \"e\": xstate.init → EVENT → EVENT → EVENT_2"));
        Sync("paths", "transition coverage > path generation should cover all transitions by default", () => Descriptions(StateGraph.CreateTestModel(Machine(("a", Node(("NEXT", "b"), ("END", "b"))), ("b", Node(("PREV", "a"), ("RESTART", "a"))))).GetShortestPaths(), "Reaches state \"a\": xstate.init → NEXT → PREV", "Reaches state \"a\": xstate.init → NEXT → RESTART", "Reaches state \"b\": xstate.init → END"));
        Sync("paths", "transition coverage > transition coverage should consider guarded transitions", Guarded);
        Sync("paths", "transition coverage > transition coverage should consider multiple transitions with the same target", () => Descriptions(StateGraph.CreateTestModel(Machine(("a", Node(("GO_TO_B", "b"), ("GO_TO_C", "c"))), ("b", Node(("GO_TO_A", "a"))), ("c", Node(("GO_TO_A", "a"))))).GetShortestPaths(), "Reaches state \"a\": xstate.init → GO_TO_B → GO_TO_A", "Reaches state \"a\": xstate.init → GO_TO_C → GO_TO_A"));
        Sync("paths", "getShortestPathsTo > Should find a path to a non-initial target state", () => ToState("closed"));
        Sync("paths", "getShortestPathsTo > Should find a path to an initial target state", () => ToState("open"));
        Sync("paths", "getShortestPathsFrom > should get shortest paths from array of paths", () => Extend(false));
        Sync("paths", "getShortestPathsFrom > getSimplePathsFrom > should get simple paths from array of paths", () => Extend(true));
    }
    private static async Task Collatz(bool testStates)
    {
        var logic = new TransitionLogic<double>((value, ev, _) => ev.Type == "even" ? value / 2 : value * 3 + 1, 15);
        var model = new TestModel<TransitionSnapshot<double>>(logic, new()
        {
            Events = new(state => [new(state.Context % 2 == 0 ? "even" : "odd")]),
            StateMatcher = testStates ? (state, key) => key == "even" ? state.Context % 2 == 0 : key == "odd" && state.Context % 2 == 1 : null
        });
        var paths = model.GetShortestPaths(new() { ToState = state => state.Context == 1 });
        if (!testStates) { Equal(true, paths.Count > 0); return; }
        var tested = new List<string>();
        var parameters = new TestParameters<TransitionSnapshot<double>> { States = new Dictionary<string, Func<TransitionSnapshot<double>, Task>>
        {
            ["even"] = state => { tested.Add("even"); Equal(0d, state.Context % 2); return Task.CompletedTask; },
            ["odd"] = state => { tested.Add("odd"); Equal(1d, state.Context % 2); return Task.CompletedTask; }
        } };
        foreach (var path in paths) await path.TestAsync(parameters).ConfigureAwait(false);
        Equal(true, tested.Contains("even", StringComparer.Ordinal)); Equal(true, tested.Contains("odd", StringComparer.Ordinal));
    }
    private static async Task StateKeys(bool ids)
    {
        var model = StateGraph.CreateTestModel(Machine(("a", new() { Id = ids ? "state_a" : null, On = On<Empty>(("EVENT", "b")) }),
            ("b", new() { Id = ids ? "state_b" : null, Initial = "b1", States = States<Empty>(
                ("b1", new() { Id = ids ? "state_b1" : null, On = On<Empty>(("NEXT", "b2")) }), ("b2", new() { Id = ids ? "state_b2" : null })) })));
        var tested = new List<string>();
        var keys = ids ? new[] { "#state_a", "#state_b", "#state_b1", "#state_b2" } : ["a", "b", "b.b1", "b.b2"];
        var parameters = new TestParameters<MachineSnapshot<Empty>> { States = keys.ToDictionary(key => key, _ => (Func<MachineSnapshot<Empty>, Task>)(state => { tested.Add(state.Value.ToJson()); return Task.CompletedTask; }), StringComparer.Ordinal) };
        foreach (var path in model.GetShortestPaths()) await path.TestAsync(parameters).ConfigureAwait(false);
        Equal(true, tested.SequenceEqual(["\"a\"", "{\"b\":\"b1\"}", "{\"b\":\"b1\"}", "{\"b\":\"b2\"}", "{\"b\":\"b2\"}"]));
    }
    private static void Forbidden(StateConfig<Empty> config, string category)
    {
        var machine = new StateMachine<Empty>(config, _ => new());
        try { StateGraph.CreateTestModel(machine); }
        catch (InvalidOperationException error) { Equal(category + " on test machines are not supported", error.Message); return; }
        throw new InvalidOperationException("Forbidden machine accepted.");
    }
    private static async Task CustomGenerator()
    {
        var model = StateGraph.CreateTestModel(Machine(("a", Node(("EVENT", "b"))), ("b", Node())));
        var paths = model.GetPaths((logic, options) =>
        {
            var initial = ActorTransitions.GetInitialSnapshot(logic);
            var events = options.Events?.GetEvents(initial) ?? [];
            var next = ActorTransitions.GetNextSnapshot(logic, initial, events[0]);
            return [new(next, [new(initial, events[0])], 1)];
        });
        foreach (var path in paths) await path.TestAsync(new()).ConfigureAwait(false);
    }
    private sealed record AllowedContext(bool Allowed);
    private static void Filter()
    {
        var machine = new StateMachine<AllowedContext>(new() { Id = "guarded-test-model", Initial = "start", States = States<AllowedContext>(
            ("start", new() { On = On<AllowedContext>(("NEXT", "idle")) }),
            ("idle", new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<AllowedContext>>>
            {
                ["PROCEED"] = [new() { Target = ["done"], Guard = MachineGuards.Predicate<AllowedContext>((context, _) => context.Allowed) }],
                ["ALLOW"] = [new() { Actions = [MachineActions.Assign<AllowedContext>((_, _) => new(true))] }]
            } }), ("done", new() { Kind = StateKind.Final })) }, _ => new(false));
        Descriptions(StateGraph.CreateTestModel(machine).GetSimplePaths(new() { FilterEvents = (state, ev) => state.Can(ev), ToState = state => state.Status == SnapshotStatus.Done }), "Reaches state \"done\"({\"allowed\":true}): xstate.init → NEXT → ALLOW → PROCEED");
    }
    private static void Guarded()
    {
        var machine = new StateMachine<Empty>(new() { Initial = "a", States = States<Empty>(("a", new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<Empty>>> { ["NEXT"] = [new() { Guard = MachineGuards.Named<Empty>("valid"), Target = ["b"] }, new() { Target = ["b"] }] } }), ("b", Node())) }, _ => new(), guards: new Dictionary<string, MachineGuard<Empty>> { ["valid"] = MachineGuards.Predicate<Empty>((_, ev) => ((ValueEvent)(ev.Payload ?? throw new InvalidOperationException("Event value missing."))).Value > 10) });
        Descriptions(StateGraph.CreateTestModel(machine).GetShortestPaths(new() { Events = new MachineEvent[] { new("NEXT", new ValueEvent(0)), new("NEXT", new ValueEvent(100)), new("NEXT", new ValueEvent(1000)) } }),
            "Reaches state \"b\": xstate.init → NEXT ({\"value\":0}) → NEXT ({\"value\":0})", "Reaches state \"b\": xstate.init → NEXT ({\"value\":100})", "Reaches state \"b\": xstate.init → NEXT ({\"value\":1000})");
    }
    private static void ToState(string target) => Equal(1, StateGraph.CreateTestModel(Machine(("open", Node(("CLOSE", "closed"))), ("closed", Node(("OPEN", "open"))))).GetShortestPaths(new() { ToState = state => state.Matches(target) }).Count);
    private static void Extend(bool simple)
    {
        var model = StateGraph.CreateTestModel(Machine(("a", Node(("NEXT", "b"), ("OTHER", "b"), ("TO_C", "c"), ("TO_D", "d"), ("TO_E", "e"))), ("b", Node(("TO_C", "c"), ("TO_D", "d"))), ("c", Node()), ("d", Node()), ("e", Node())));
        var options = new TestModelOptions<MachineSnapshot<Empty>> { ToState = state => state.Matches("b") };
        var paths = simple ? model.GetSimplePaths(options) : model.GetShortestPaths(options); Equal(2, paths.Count);
        var extended = simple ? model.GetSimplePathsFrom(paths) : model.GetShortestPathsFrom(paths);
        Equal(4, extended.Count); Equal(true, extended.All(path => path.Steps.Count == 3));
    }
}
