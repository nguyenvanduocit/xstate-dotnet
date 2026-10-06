using System.Text.Json;
using XState;
using XState.Graph;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static partial class TestModelTests
{
    private sealed record ValuesContext(int[] Values);
    private sealed record CountContext(int Count);
    private sealed record NameContext(string Name);
    private sealed record SubmitEvent(string Value);
    public static void RegisterIntegration(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Func<Task> run) => cases.Add(("packages/core/src/graph/test/index.test.ts::" + title, run));
        void Sync(string title, Action run) => Case(title, () => { run(); return Task.CompletedTask; });
        cases.Add(("packages/core/src/graph/test/adjacency.test.ts::adjacency maps > model generates an adjacency map (converted to an array)", () => { MarioAdjacency(); return Task.CompletedTask; }));
        cases.Add(("packages/core/src/graph/test/adjacency.test.ts::adjacency maps > function generates an adjacency map (converted to an array)", () => { TrafficAdjacency(); return Task.CompletedTask; }));
        Case("events > should allow for representing many cases", Feedback);
        Case("events > should not throw an error for unimplemented events", () => TestAll(StateGraph.CreateTestModel(Machine(("idle", Node(("ACTIVATE", "active"))), ("active", Node()))), new()));
        Case("events > should allow for dynamic generation of cases based on state", DynamicCases);
        Sync("state limiting > should limit states with filter option", StateLimit);
        Sync("prevents infinite recursion based on a provided limit", InfiniteLimit);
        Case("test model options > options.testState(...) should test state", WildcardOption);
        Case("tests transitions", StepMembers);
        Case("Event in event executor should contain payload from case", FunctionPayload);
        Case("state tests > should test states", () => CallbackStates(false));
        Case("state tests > should test wildcard state for non-matching states", () => CallbackStates(true));
        Case("state tests > should test nested states", NestedStates);
        Sync("state tests > should test with input", ModelInput);
    }
    private static async Task TestAll<T>(TestModel<T> model, TestParameters<T> parameters) where T : class, IActorSnapshot
    {
        foreach (var path in model.GetShortestPaths()) await path.TestAsync(parameters).ConfigureAwait(false);
    }
    private static void MarioAdjacency()
    {
        var model = StateGraph.CreateTestModel(Machine(("standing", Node(("left", "walking"), ("right", "walking"), ("down", "crouching"), ("up", "jumping"))),
            ("walking", Node(("up", "jumping"), ("stop", "standing"))), ("jumping", Node(("land", "standing"))), ("crouching", Node(("release_down", "standing")))));
        var actual = StateGraph.AdjacencyMapToArray(model.GetAdjacencyMap()).Select(edge => $"Given Mario is {edge.State.Value.AtomicValue}, when {edge.Event.Type}, then {edge.NextState.Value.AtomicValue}").ToArray();
        string[] expected = [
            "Given Mario is standing, when left, then walking", "Given Mario is standing, when right, then walking", "Given Mario is standing, when down, then crouching", "Given Mario is standing, when up, then jumping",
            "Given Mario is walking, when up, then jumping", "Given Mario is walking, when stop, then standing", "Given Mario is walking, when up, then jumping", "Given Mario is walking, when stop, then standing",
            "Given Mario is crouching, when release_down, then standing", "Given Mario is jumping, when land, then standing", "Given Mario is jumping, when land, then standing",
            "Given Mario is standing, when left, then walking", "Given Mario is standing, when right, then walking", "Given Mario is standing, when down, then crouching", "Given Mario is standing, when up, then jumping",
            "Given Mario is standing, when left, then walking", "Given Mario is standing, when right, then walking", "Given Mario is standing, when down, then crouching", "Given Mario is standing, when up, then jumping",
            "Given Mario is standing, when left, then walking", "Given Mario is standing, when right, then walking", "Given Mario is standing, when down, then crouching", "Given Mario is standing, when up, then jumping"];
        Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }
    private static void TrafficAdjacency()
    {
        var model = StateGraph.CreateTestModel(Machine(("green", Node(("TIMER", "yellow"))), ("yellow", Node(("TIMER", "red"))), ("red", Node(("TIMER", "green")))));
        var actual = StateGraph.AdjacencyMapToArray(model.GetAdjacencyMap()).Select(edge => new { state = edge.State.Value.AtomicValue, @event = edge.Event.Type, nextState = edge.NextState.Value.AtomicValue });
        Equal("[{\"state\":\"green\",\"event\":\"TIMER\",\"nextState\":\"yellow\"},{\"state\":\"yellow\",\"event\":\"TIMER\",\"nextState\":\"red\"},{\"state\":\"red\",\"event\":\"TIMER\",\"nextState\":\"green\"},{\"state\":\"green\",\"event\":\"TIMER\",\"nextState\":\"yellow\"}]", JsonSerializer.Serialize(actual));
    }
    private static async Task Feedback()
    {
        var formEvents = On<Empty>(("CLOSE", "closed"), ("ESC", "closed"));
        var formOn = new Dictionary<string, IReadOnlyList<TransitionConfig<Empty>>>(StringComparer.Ordinal)
        {
            ["SUBMIT"] = [new() { Target = ["thanks"], Guard = MachineGuards.Predicate<Empty>((_, ev) => ((SubmitEvent)(ev.Payload ?? throw new InvalidOperationException("Missing submission"))).Value.Length != 0) }, new() { Target = [".invalid"] }]
        };
        foreach (var item in formEvents) formOn.Add(item.Key, item.Value);
        var machine = new StateMachine<Empty>(new() { Id = "feedback", Initial = "question", States = States<Empty>(
            ("question", Node(("CLICK_GOOD", "thanks"), ("CLICK_BAD", "form"), ("CLOSE", "closed"), ("ESC", "closed"))),
            ("form", new() { Initial = "valid", On = formOn, States = States<Empty>(("valid", Node()), ("invalid", Node())) }),
            ("thanks", Node(("CLOSE", "closed"), ("ESC", "closed"))), ("closed", new() { Kind = StateKind.Final })) }, _ => new());
        var model = StateGraph.CreateTestModel(machine, new() { Events = new MachineEvent[] { new("SUBMIT", new SubmitEvent("something")), new("SUBMIT", new SubmitEvent("")) } });
        await TestAll(model, new()).ConfigureAwait(false);
    }
    private static async Task DynamicCases()
    {
        var values = new[] { 1, 2, 3 };
        var machine = new StateMachine<ValuesContext>(new() { Initial = "a", States = States<ValuesContext>(
            ("a", new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<ValuesContext>>> { ["EVENT"] = values.Select(value => new TransitionConfig<ValuesContext> { Target = [value == 1 ? "b" : value == 2 ? "c" : "d"], Guard = MachineGuards.Predicate<ValuesContext>((_, ev) => ((ValueEvent)(ev.Payload ?? throw new InvalidOperationException("Missing value"))).Value == value) }).ToArray() } }),
            ("b", new()), ("c", new()), ("d", new())) }, _ => new(values));
        var model = StateGraph.CreateTestModel(machine, new() { Events = new(state => state.Context.Values.Select(value => new MachineEvent("EVENT", new ValueEvent(value))).ToArray()) });
        var paths = model.GetShortestPaths(); Equal(3, paths.Count); var tested = new List<object>();
        foreach (var path in paths) await path.TestAsync(new() { Events = new Dictionary<string, Func<GraphStep<MachineSnapshot<ValuesContext>>, Task>> { ["EVENT"] = step => { tested.Add(new { type = step.Event.Type, value = ((ValueEvent)(step.Event.Payload ?? throw new InvalidOperationException("Missing payload"))).Value }); return Task.CompletedTask; } } }).ConfigureAwait(false);
        Equal("[{\"type\":\"EVENT\",\"value\":1},{\"type\":\"EVENT\",\"value\":2},{\"type\":\"EVENT\",\"value\":3}]", JsonSerializer.Serialize(tested));
    }
    private static void StateLimit()
    {
        var machine = new StateMachine<CountContext>(new() { Initial = "counting", States = States<CountContext>(("counting", new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<CountContext>>> { ["INC"] = [new() { Actions = [MachineActions.Assign<CountContext>((context, _) => new(context.Count + 1))] }] } })) }, _ => new(0));
        Equal(1, StateGraph.CreateTestModel(machine).GetShortestPaths(new() { StopWhen = state => state.Context.Count >= 5 }).Count);
    }
    private static void InfiniteLimit()
    {
        var machine = new StateMachine<CountContext>(new() { Id = "machine", On = new Dictionary<string, IReadOnlyList<TransitionConfig<CountContext>>> { ["TOGGLE"] = [new() { Actions = [MachineActions.Assign<CountContext>((context, _) => new(context.Count + 1))] }] } }, _ => new(0));
        try { StateGraph.CreateTestModel(machine).GetShortestPaths(new() { Limit = 100 }); }
        catch (InvalidOperationException error) { Equal("Traversal limit exceeded", error.Message); return; }
        throw new InvalidOperationException("Traversal limit was ignored");
    }
    private static async Task WildcardOption()
    {
        var tested = new List<string?>();
        await TestAll(StateGraph.CreateTestModel(Machine(("inactive", Node(("NEXT", "active"))), ("active", Node()))), new() { States = new Dictionary<string, Func<MachineSnapshot<Empty>, Task>> { ["*"] = state => { tested.Add(state.Value.AtomicValue); return Task.CompletedTask; } } }).ConfigureAwait(false);
        Equal(true, tested.SequenceEqual(["inactive", "active"]));
    }
    private static async Task StepMembers()
    {
        var model = StateGraph.CreateTestModel(Machine(("first", Node(("NEXT", "second"))), ("second", Node()))); var assertions = 0;
        var paths = model.GetShortestPaths(new() { ToState = state => state.Matches("second") });
        await paths[0].TestAsync(new() { Events = new Dictionary<string, Func<GraphStep<MachineSnapshot<Empty>>, Task>> { ["NEXT"] = step => { Equal(true, step.Event is not null); Equal(true, step.State is not null); assertions += 2; return Task.CompletedTask; } } }).ConfigureAwait(false);
        Equal(2, assertions);
    }
    private sealed record FunctionEvent(int Payload, Func<int> Fn);
    private static async Task FunctionPayload()
    {
        var machine = Machine(("first", Node(("NEXT", "second"))), ("second", Node()));
        Func<int> nonSerializableData = () => 42;
        var payload = new FunctionEvent(10, nonSerializableData);
        var model = StateGraph.CreateTestModel(machine, new() { Events = new MachineEvent[] { new("NEXT", payload) } });
        var paths = model.GetShortestPaths(new() { ToState = state => state.Matches("second") });
        var called = 0;
        await model.TestPathAsync(paths[0], new() { Events = new Dictionary<string, Func<GraphStep<MachineSnapshot<Empty>>, Task>>
        {
            ["NEXT"] = step => { Equal("NEXT", step.Event.Type); Equal(payload, step.Event.Payload); Equal(true, ReferenceEquals(nonSerializableData, ((FunctionEvent)(step.Event.Payload ?? throw new InvalidOperationException("Missing function payload"))).Fn)); called++; return Task.CompletedTask; }
        } }, new()).ConfigureAwait(false);
        Equal(1, called);
    }
    private static async Task CallbackStates(bool wildcard)
    {
        var model = StateGraph.CreateTestModel(wildcard ? Machine(("a", Node(("NEXT", "b"), ("OTHER", "c"))), ("b", Node()), ("c", Node())) : Machine(("a", Node(("NEXT", "b"))), ("b", Node())));
        var count = 0; var callbacks = new Dictionary<string, Func<MachineSnapshot<Empty>, Task>>(StringComparer.Ordinal);
        foreach (var expected in wildcard ? new[] { "a", "b", "c" } : ["a", "b"])
            callbacks.Add(expected == "c" ? "*" : expected, state => { Equal(expected, state.Value.AtomicValue); count++; return Task.CompletedTask; });
        await TestAll(model, new() { States = callbacks }).ConfigureAwait(false); Equal(wildcard ? 4 : 2, count);
    }
    private static async Task NestedStates()
    {
        var model = StateGraph.CreateTestModel(Machine(("a", Node(("NEXT", "b"))), ("b", new() { Initial = "b1", States = States<Empty>(("b1", Node())) }))); var tested = new List<string>();
        await TestAll(model, new() { States = new Dictionary<string, Func<MachineSnapshot<Empty>, Task>>
        {
            ["a"] = state => { tested.Add("a"); Equal("a", state.Value.AtomicValue); return Task.CompletedTask; },
            ["b"] = state => { tested.Add("b"); Equal(true, state.Matches("b")); return Task.CompletedTask; },
            ["b.b1"] = state => { tested.Add("b.b1"); Equal("{\"b\":\"b1\"}", state.Value.ToJson()); return Task.CompletedTask; }
        } }).ConfigureAwait(false);
        Equal(true, tested.SequenceEqual(["a", "b", "b.b1"]));
    }
    private static void ModelInput()
    {
        var machine = new StateMachine<NameContext>(new() { Initial = "checking", States = States<NameContext>(("checking", new() { Always = [new() { Guard = MachineGuards.Predicate<NameContext>((context, _) => context.Name.Length > 3), Target = ["longName"] }, new() { Target = ["shortName"] }] }), ("longName", new()), ("shortName", new())) }, args => new(((NameContext)(args.Input ?? throw new InvalidOperationException("Missing input"))).Name));
        var model = StateGraph.CreateTestModel(machine);
        Equal(true, model.GetShortestPaths(new() { Input = new NameContext("ed") })[0].Steps.Select(step => step.State.Value.AtomicValue).SequenceEqual(["shortName"]));
        Equal(true, model.GetShortestPaths(new() { Input = new NameContext("edward") })[0].Steps.Select(step => step.State.Value.AtomicValue).SequenceEqual(["longName"]));
    }
}
