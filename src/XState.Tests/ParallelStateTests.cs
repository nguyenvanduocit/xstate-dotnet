using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static partial class ParallelStateTests
{
    private const string Prefix = "packages/core/test/parallel.test.ts::parallel states > ";
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures/upstream-parallel.json");
    private static readonly Dictionary<string, JsonElement> Fixtures = Load();
    private static Dictionary<string, JsonElement> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        return document.RootElement.GetProperty("fixtures").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("config").Clone(), StringComparer.Ordinal);
    }
    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
    private static StateMachine<JsonElement> Fixture(string name) => new(DataTests.ReadConfig(Fixtures[name]), _ => Empty());
    private static void Value<T>(string expected, MachineSnapshot<T> snapshot) => Equal(true, JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(snapshot.Value.ToJson())));
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((Prefix + title, run));
        Case("should have initial parallel states", () => Check("wordMachine@235", "{\"bold\":\"off\",\"italics\":\"off\",\"underline\":\"off\",\"list\":\"none\"}", start: false));
        Word("{\"bold\": \"off\"}", "TOGGLE_BOLD", "{\"bold\":\"on\",\"italics\":\"off\",\"underline\":\"off\",\"list\":\"none\"}");
        Word("{\"bold\": \"on\"}", "TOGGLE_BOLD", "{\"bold\":\"off\",\"italics\":\"off\",\"underline\":\"off\",\"list\":\"none\"}");
        Word("{\"bold\":\"off\",\"italics\":\"off\",\"underline\":\"on\",\"list\":\"bullets\"}", "TOGGLE_BOLD, TOGGLE_ITALICS", "{\"bold\":\"on\",\"italics\":\"on\",\"underline\":\"on\",\"list\":\"bullets\"}");
        Word("{\"bold\":\"off\",\"italics\":\"off\",\"underline\":\"on\",\"list\":\"bullets\"}", "RESET", "{\"bold\":\"off\",\"italics\":\"off\",\"underline\":\"off\",\"list\":\"none\"}");
        void Word(string from, string events, string expected) => Case($"should go from {from} to {expected} on {events}", () =>
        {
            var machine = Fixture("wordMachine@235"); var snapshot = machine.ResolveState(StateValue.Parse(from), Empty());
            foreach (var ev in EventSeparator().Split(events)) snapshot = machine.GetNextSnapshot(snapshot, new(ev));
            Value(expected, snapshot);
        });
        Case("should have all parallel states represented in the state value (2)", () => Check("wakMachine@191", "{\"wak1\":\"wak1sonA\",\"wak2\":\"wak2sonB\"}", ["WAK2"]));
        Case("should work with regions without states", () => Check("flatParallelMachine@292", "{\"foo\":{},\"bar\":{},\"baz\":\"one\"}", start: false));
        Case("should work with regions without states [occurrence 2]", () => Check("flatParallelMachine@292", "{\"foo\":{},\"bar\":{},\"baz\":\"two\"}", ["E"]));
        Case("should properly transition to relative substate", () => Check("composerMachine@6", "{\"ReadOnly\":{\"StructureEdit\":{\"SelectionStatus\":\"SelectedActivity\",\"ClipboardStatus\":\"Empty\"}}}", ["singleClickActivity"]));
        Case("should properly transition according to entry events on an initial state", () => Check("machine@638", "{\"OUTER1\":\"B\",\"OUTER2\":{\"INNER1\":\"OFF\",\"INNER2\":\"OFF\"}}", start: false));
        Case("should properly transition when raising events for a parallel state", () => Check("raisingParallelMachine@307", "{\"OUTER1\":\"B\",\"OUTER2\":{\"INNER1\":\"ON\",\"INNER2\":\"ON\"}}", ["EVENT_OUTER1_B"]));
        Case("should handle simultaneous orthogonal transitions", Simultaneous);
        Case("should execute actions of the initial transition of a parallel region when entering the initial state nodes of a machine", () => InitialAction(false));
        Case("should execute actions of the initial transition of a parallel region when the parallel state is targeted with an explicit transition", () => InitialAction(true));
        Case("transitions with nested parallel states > should properly transition when in a simple nested state", () => Check("nestedParallelState@374", "{\"OUTER1\":{\"STATE_ON\":{\"STATE_NTJ0\":\"STATE_WORKING_0\",\"STATE_NTJ1\":\"STATE_IDLE_1\"}},\"OUTER2\":\"STATE_ON_SIMPLE\"}", ["EVENT_SIMPLE", "EVENT_STATE_NTJ0_WORK"]));
        Case("transitions with nested parallel states > should properly transition when in a complex nested state", () => Check("nestedParallelState@374", "{\"OUTER1\":{\"STATE_ON\":{\"STATE_NTJ0\":\"STATE_WORKING_0\",\"STATE_NTJ1\":\"STATE_IDLE_1\"}},\"OUTER2\":{\"STATE_ON_COMPLEX\":{\"STATE_INNER1\":\"STATE_OFF\",\"STATE_INNER2\":\"STATE_OFF\"}}}", ["EVENT_COMPLEX", "EVENT_STATE_NTJ0_WORK"]));
        Case("nested flat parallel states > should represent the flat nested parallel states in the state value", () => Check("machine@865", "{\"B\":{\"C\":{},\"D\":{}}}", ["to-B"]));
        Case("deep flat parallel states > should properly evaluate deep flat parallel states", () => Check("deepFlatParallelMachine@457", "{\"V\":{\"B\":{\"BB\":{\"BBB_A\":{},\"BBB_B\":{}}}},\"X\":{}}", ["a", "c", "b"]));
        Case("deep flat parallel states > should not overlap resolved state nodes in state resolution", NoOverlap);
        Case("other > should calculate the entry set for reentering transitions in parallel states", Reentry);
        Case("targetless transition on a parallel state should not enter nor exit any states", () => Targetless(false));
        Case("targetless transition in one of the parallel regions should not enter nor exit any states", () => Targetless(true));
    }
    public static void RegisterAsync(List<(string Id, Func<Task> Run)> cases) => cases.Add((Prefix + "should raise a \"xstate.done.state.*\" event when all child states reach final state", Completion));
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("pinned parallel fixtures resolve initial and single-event snapshots for JS differential checks", () => { ExportFixtures(); return Task.CompletedTask; }));
    [GeneratedRegex(",\\s?")]
    private static partial Regex EventSeparator();
    private static void Check(string name, string expected, string[]? events = null, bool start = true)
    {
        var actor = new Actor<MachineSnapshot<JsonElement>>(Fixture(name)); if (start) actor.Start();
        foreach (var ev in events ?? []) actor.Send(new(ev)); Value(expected, actor.GetSnapshot()); actor.Stop();
    }
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] entries) => entries.ToDictionary(e => e.Key, e => e.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(params (string Key, TransitionConfig<T> Transition)[] entries) => entries.ToDictionary(e => e.Key, e => (IReadOnlyList<TransitionConfig<T>>)[e.Transition], StringComparer.Ordinal);
    private sealed record EditorContext(string Value);
    private static void Simultaneous()
    {
        var machine = new StateMachine<EditorContext>(new() { Id = "yamlEditor", Kind = StateKind.Parallel, States = States<EditorContext>(
            ("editing", new() { On = On<EditorContext>(("CHANGE", new() { Actions = [MachineActions.Assign<EditorContext>((_, ev) => new((string)(ev.Payload ?? throw new InvalidOperationException("CHANGE value missing."))))] })) }),
            ("status", new() { Initial = "unsaved", States = States<EditorContext>(
                ("unsaved", new() { On = On<EditorContext>(("SAVE", new() { Target = ["saved"], Actions = [MachineActions.Named<EditorContext>("save")] })) }),
                ("saved", new() { On = On<EditorContext>(("CHANGE", new() { Target = ["unsaved"] })) })) })) }, _ => new(""));
        var actor = new Actor<MachineSnapshot<EditorContext>>(machine).Start(); actor.Send(new("SAVE")); actor.Send(new("CHANGE", "something"));
        Value("{\"editing\":{},\"status\":\"unsaved\"}", actor.GetSnapshot()); Equal(new EditorContext("something"), actor.GetSnapshot().Context); actor.Stop();
    }
    private static void InitialAction(bool explicitTransition)
    {
        var count = 0; var action = MachineActions.Effect<int>((_, _) => count++);
        StateConfig<int> config = explicitTransition ? new() { Initial = "a", States = States<int>(
            ("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { Kind = StateKind.Parallel, States = States<int>(("c", new() { Initial = "c1", InitialActions = [action], States = States<int>(("c1", new())) })) })) }
            : new() { Kind = StateKind.Parallel, States = States<int>(("a", new() { Initial = "a1", InitialActions = [action], States = States<int>(("a1", new())) })) };
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0)).Start(); if (explicitTransition) actor.Send(new("NEXT")); Equal(1, count); actor.Stop();
    }
    private static void NoOverlap()
    {
        var machine = new StateMachine<int>(new() { Id = "pipeline", Kind = StateKind.Parallel, States = States<int>(
            ("foo", new() { On = On<int>(("UPDATE", new() { Actions = [MachineActions.Effect<int>((_, _) => { })] })) }),
            ("bar", new() { Initial = "idle", On = On<int>(("UPDATE", new() { Target = [".baz"] })), States = States<int>(("idle", new()), ("baz", new())) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("UPDATE")); actor.Stop();
    }
    private static void Reentry()
    {
        var machine = new StateMachine<string[]>(new() { Id = "test", Kind = StateKind.Parallel, States = States<string[]>(
            ("foo", new() { Initial = "foobar", States = States<string[]>(
                ("foobar", new() { On = On<string[]>(("GOTO_FOOBAZ", new() { Target = ["foobaz"] })) }),
                ("foobaz", new() { Entry = [MachineActions.Assign<string[]>((context, _) => [.. context, "entered foobaz"])], On = On<string[]>(("GOTO_FOOBAZ", new() { Target = ["foobaz"], Reenter = true })) })) }), ("bar", new())) }, _ => []);
        var actor = new Actor<MachineSnapshot<string[]>>(machine).Start(); actor.Send(new("GOTO_FOOBAZ")); actor.Send(new("GOTO_FOOBAZ")); Equal(2, actor.GetSnapshot().Context.Length); actor.Stop();
    }
    private static async Task Completion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new Actor<MachineSnapshot<JsonElement>>(Fixture("machine@1057"));
        using var subscription = actor.Subscribe(_ => { }, onComplete: () => completion.TrySetResult());
        actor.Start(); actor.Send(new("FINISH")); await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); actor.Stop();
    }
    private static void Targetless(bool region)
    {
        var log = new List<string>();
        var transition = On<int>(("MY_EVENT", new() { Actions = [MachineActions.Effect<int>((_, _) => { })] }));
        StateConfig<int> Tracked(string name, string? initial = null, Dictionary<string, StateConfig<int>>? states = null, Dictionary<string, IReadOnlyList<TransitionConfig<int>>>? on = null) => new()
        {
            Initial = initial, States = states ?? [], On = on ?? [], Entry = [MachineActions.Effect<int>((_, _) => log.Add("enter: " + name))], Exit = [MachineActions.Effect<int>((_, _) => log.Add("exit: " + name))]
        };
        var machine = new StateMachine<int>(new() { Id = "test", Kind = StateKind.Parallel, On = region ? [] : transition,
            Entry = [MachineActions.Effect<int>((_, _) => log.Add("enter: __root__"))], Exit = [MachineActions.Effect<int>((_, _) => log.Add("exit: __root__"))], States = States<int>(
                ("first", Tracked("first", "disabled", States<int>(("disabled", Tracked("first.disabled")), ("enabled", Tracked("first.enabled"))), region ? transition : null)),
                ("second", Tracked("second"))) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); log.Clear(); actor.Send(new("MY_EVENT")); Equal(0, log.Count); actor.Stop();
    }
    private static void ExportFixtures()
    {
        static object View(MachineSnapshot<JsonElement> snapshot) => new { value = snapshot.Value.ToJson(), status = snapshot.Status.ToString().ToLowerInvariant() };
        var rows = new List<object>();
        foreach (var name in Fixtures.Keys)
        {
            var machine = Fixture(name); var initial = machine.GetInitialSnapshot();
            Equal(SnapshotStatus.Active, initial.Status);
            rows.Add(new { name, initial = View(initial), events = machine.Events.Select(type => new { type, snapshot = View(machine.GetNextSnapshot(initial, new(type))) }).ToArray() });
        }
        var report = new { fixtureSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FixturePath))).ToLowerInvariant(), rows };
        File.WriteAllText("tmp/xstate-parity/csharp-parallel-fixtures.json", JsonSerializer.Serialize(report));
    }
}
