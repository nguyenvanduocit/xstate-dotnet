using System.Text.Json;
using XState;
using XState.Graph;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class DieHardTests
{
    private sealed record Context(int Three, int Five);
    private sealed class Jugs
    {
        internal int Three { get; private set; }
        internal int Five { get; private set; }
        internal void FillThree() => Three = 3;
        internal void FillFive() => Five = 5;
        internal void EmptyThree() => Three = 0;
        internal void EmptyFive() => Five = 0;
        internal void TransferThree() { var poured = Math.Min(5 - Five, Three); Three -= poured; Five += poured; }
        internal void TransferFive() { var poured = Math.Min(3 - Three, Five); Three += poured; Five -= poured; }
    }
    private sealed record Paths(TestModel<MachineSnapshot<Context>> Model, IReadOnlyList<TestPath<MachineSnapshot<Context>>> Values);
    private static readonly Lazy<Paths> Shortest = new(() => CreatePaths("shortest"));
    private static readonly Lazy<Paths> Simple = new(() => CreatePaths("simple"));
    private static readonly Lazy<Paths> EmptyThree = new(() => CreatePaths("emptyThree"));
    private static readonly Lazy<Paths> Sequence = new(() => CreatePaths("sequence"));
    private static readonly int[] ShortestTargets = [0, 3];
    private static readonly int[] SimpleTargets = [3, 0, 3, 0, 3, 0, 3, 3, 0, 0, 3, 3, 0, 3];
    private static readonly string[] SequenceEvents = ["FILL_5", "POUR_5_TO_3", "EMPTY_3", "POUR_5_TO_3", "FILL_5", "POUR_5_TO_3"];
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    private static TestModel<MachineSnapshot<Context>> CreateModel()
    {
        var transitions = new Dictionary<string, IReadOnlyList<TransitionConfig<Context>>>(StringComparer.Ordinal);
        void Assign(string name, Func<Context, Context> update) => transitions.Add(name, [new() { Actions = [MachineActions.Assign<Context>((context, _) => update(context))] }]);
        Assign("POUR_3_TO_5", context => { var poured = Math.Min(5 - context.Five, context.Three); return new(context.Three - poured, context.Five + poured); });
        Assign("POUR_5_TO_3", context => { var poured = Math.Min(3 - context.Three, context.Five); return new(context.Three + poured, context.Five - poured); });
        Assign("FILL_3", context => context with { Three = 3 }); Assign("FILL_5", context => context with { Five = 5 });
        Assign("EMPTY_3", context => context with { Three = 0 }); Assign("EMPTY_5", context => context with { Five = 0 });
        var machine = new StateMachine<Context>(new()
        {
            Id = "dieHard", Initial = "pending", States = new Dictionary<string, StateConfig<Context>>(StringComparer.Ordinal)
            {
                ["pending"] = new() { Always = [new() { Target = ["success"], Guard = MachineGuards.Named<Context>("weHave4Gallons") }], On = transitions },
                ["success"] = new() { Kind = StateKind.Final }
            }
        }, _ => new(0, 0), guards: new Dictionary<string, MachineGuard<Context>> { ["weHave4Gallons"] = MachineGuards.Predicate<Context>((context, _) => context.Five == 4) });
        return StateGraph.CreateTestModel(machine);
    }
    private static Paths CreatePaths(string kind)
    {
        var model = CreateModel(); var options = new TestModelOptions<MachineSnapshot<Context>> { ToState = state => state.Matches("success") && (kind != "emptyThree" || state.Context.Three == 0) };
        var paths = kind switch
        {
            "shortest" => model.GetShortestPaths(options), "sequence" => model.GetPathsFromEvents(SequenceEvents.Select(type => new MachineEvent(type)).ToArray(), options),
            _ => model.GetSimplePaths(options)
        };
        return new(model, paths);
    }
    private static string StateDescription(int three) => "state \"success\"({\"three\":" + three + ",\"five\":4})";
    private static string Reaches(int three) => "reaches state \"success\" ({\"three\":" + three + ",\"five\":4})";
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Func<Task> run) => cases.Add(("packages/core/src/graph/test/dieHard.test.ts::" + title, run));
        void Sync(string title, Action run) => Case(title, () => { run(); return Task.CompletedTask; });
        const string shortest = "die hard example > testing a model (shortestPathsTo) > ";
        Sync(shortest + "should generate the right number of paths", () => Equal(2, Shortest.Value.Values.Count));
        foreach (var (three, index) in ShortestTargets.Select((value, index) => (value, index)))
            Case(shortest + "path " + StateDescription(three) + " > path " + StateDescription(three), () => Execute(Shortest.Value, index, three));
        const string simple = "die hard example > testing a model (simplePathsTo) > ";
        Sync(simple + "should generate the right number of paths", () => Equal(14, Simple.Value.Values.Count));
        var occurrences = new Dictionary<int, int>();
        foreach (var (three, index) in SimpleTargets.Select((value, index) => (value, index)))
        {
            var occurrence = occurrences.GetValueOrDefault(three) + 1; occurrences[three] = occurrence;
            Case(simple + Reaches(three) + " > path " + StateDescription(three) + (occurrence == 1 ? "" : " [occurrence " + occurrence + "]"), () => Execute(Simple.Value, index, three));
        }
        const string sequence = "die hard example > testing a model (getPathFromEvents) > ";
        Case(sequence + Reaches(3) + " > path " + StateDescription(3), () => Execute(Sequence.Value, 0, 3));
        Sync(sequence + "should return no paths if the target does not match the last entered state", () => Equal(0, CreateModel().GetPathsFromEvents(new MachineEvent[] { new("FILL_5") }, new() { ToState = state => state.Matches("success") }).Count));
        const string testPath = "die hard example > .testPath(path) > ";
        Sync(testPath + "should generate the right number of paths", () => Equal(6, EmptyThree.Value.Values.Count));
        for (var index = 0; index < 6; index++)
        {
            var captured = index;
            Case(testPath + Reaches(0) + " > path " + StateDescription(0) + " > reaches the target state" + (index == 0 ? "" : " [occurrence " + (index + 1) + "]"), () => Execute(EmptyThree.Value, captured, 0));
        }
        const string error = "error path trace > should return trace for failed state > ";
        Sync(error + "should generate the right number of paths", () => Equal(1, ErrorModel().GetShortestPaths(new() { ToState = state => state.Matches("third") }).Count));
        Case(error + "should show an error path trace", ErrorTrace);
        foreach (var kind in new[] { "Event", "State" })
            cases.Add(("packages/core/src/graph/types.test.ts::getShortestPath types > `serialize" + kind + "` should be allowed to return plain string", () =>
            {
                var machine = new StateMachine<object>(new(), _ => new object());
                StateGraph.GetShortestPaths(machine, kind == "Event" ? new() { SerializeEvent = _ => "" } : new TraversalOptions<MachineSnapshot<object>> { SerializeState = (_, _, _) => "" });
                return Task.CompletedTask;
            }));
        foreach (var title in new[] { "should execute events (`exec` property)", "should execute events (function)" })
            cases.Add(("packages/core/src/graph/test/events.test.ts::events > " + title, Events));
    }
    private static async Task Execute(Paths paths, int index, int expectedThree)
    {
        var path = paths.Values[index]; Equal(expectedThree, path.State.Context.Three); Equal(4, path.State.Context.Five);
        var jugs = new Jugs(); var actions = new Dictionary<string, Action>
        {
            ["POUR_3_TO_5"] = jugs.TransferThree, ["POUR_5_TO_3"] = jugs.TransferFive,
            ["EMPTY_3"] = jugs.EmptyThree, ["EMPTY_5"] = jugs.EmptyFive, ["FILL_3"] = jugs.FillThree, ["FILL_5"] = jugs.FillFive
        };
        await paths.Model.TestPathAsync(path, new()
        {
            States = new Dictionary<string, Func<MachineSnapshot<Context>, Task>>
            {
                ["pending"] = state => { Equal(true, jugs.Five != 4); Equal(jugs.Three, state.Context.Three); Equal(jugs.Five, state.Context.Five); return Task.CompletedTask; },
                ["success"] = _ => { Equal(4, jugs.Five); return Task.CompletedTask; }
            },
            Events = actions.ToDictionary(pair => pair.Key, pair => (Func<GraphStep<MachineSnapshot<Context>>, Task>)(async _ => { pair.Value(); await Task.Yield(); }), StringComparer.Ordinal)
        }).ConfigureAwait(false);
    }
    private sealed record Empty;
    private static TestModel<MachineSnapshot<Empty>> ErrorModel() => StateGraph.CreateTestModel(new StateMachine<Empty>(new()
    {
        Initial = "first", States = new Dictionary<string, StateConfig<Empty>>
        {
            ["first"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<Empty>>> { ["NEXT_1"] = [new() { Target = ["second"] }] } },
            ["second"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<Empty>>> { ["NEXT_2"] = [new() { Target = ["third"] }] } },
            ["third"] = new()
        }
    }, _ => new()));
    private static async Task ErrorTrace()
    {
        var model = ErrorModel(); var path = model.GetShortestPaths(new() { ToState = state => state.Matches("third") })[0];
        try { await model.TestPathAsync(path, new() { States = new Dictionary<string, Func<MachineSnapshot<Empty>, Task>> { ["third"] = _ => throw new InvalidOperationException("test error") } }).ConfigureAwait(false); }
        catch (PathTestException error)
        {
            Equal(true, error.Message.Contains("test error", StringComparison.Ordinal));
            Equal("test error\nPath:\n\tState: {\"value\":\"first\"}\n\tEvent: {\"type\":\"xstate.init\"}\n\n\tState: {\"value\":\"second\"} via {\"type\":\"xstate.init\"}\n\tEvent: {\"type\":\"NEXT_1\"}\n\n\tState: {\"value\":\"third\"} via {\"type\":\"NEXT_1\"}\n\tEvent: {\"type\":\"NEXT_2\"}\n\n\tState: {\"value\":\"third\"} via {\"type\":\"NEXT_2\"}", error.Message);
            Observations["error"] = error.Message; return;
        }
        throw new InvalidOperationException("Should have failed");
    }
    private static async Task Events()
    {
        var model = StateGraph.CreateTestModel(new StateMachine<Empty>(new() { Initial = "a", States = new Dictionary<string, StateConfig<Empty>>
        {
            ["a"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<Empty>>> { ["EVENT"] = [new() { Target = ["b"] }] } }, ["b"] = new()
        } }, _ => new()));
        var executed = false;
        foreach (var path in model.GetShortestPaths()) await path.TestAsync(new() { Events = new Dictionary<string, Func<GraphStep<MachineSnapshot<Empty>>, Task>> { ["EVENT"] = _ => { executed = true; return Task.CompletedTask; } } }).ConfigureAwait(false);
        Equal(true, executed);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("exports every jug path snapshot event weight and description for pinned JS comparison", () =>
    {
        foreach (var (name, paths) in new[] { ("shortest", Shortest.Value), ("simple", Simple.Value), ("emptyThree", EmptyThree.Value), ("sequence", Sequence.Value) })
            Observations[name] = paths.Values.Select(path => new
            {
                value = path.State.Value.AtomicValue, context = new { three = path.State.Context.Three, five = path.State.Context.Five }, weight = path.Weight, description = path.Description,
                steps = path.Steps.Select(step => new { value = step.State.Value.AtomicValue, context = new { three = step.State.Context.Three, five = step.State.Context.Five }, @event = step.Event.Type }).ToArray()
            }).ToArray();
        File.WriteAllText("tmp/xstate-parity/csharp-die-hard.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask;
    }));
}
