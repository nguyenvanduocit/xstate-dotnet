using System.Text.Json;
using System.Text.Json.Nodes;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class JsonSnapshotTests
{
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(string type, TransitionConfig<int> transition) => new(StringComparer.Ordinal) { [type] = new[] { transition } };
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string id, Action run) => cases.Add(("packages/core/test/" + id, () => { run(); return Task.CompletedTask; }));
        Case("rehydration.test.ts::rehydration > using persisted state > should be able to use `hasTag` immediately", Tags);
        Case("rehydration.test.ts::rehydration > using persisted state > should not call exit actions when machine gets stopped immediately", Stop);
        Case("rehydration.test.ts::rehydration > using persisted state > should get correct result back from `can` immediately", Can);
        Case("history.test.ts::revive history states > should restore from stringified snapshot", () => History(false));
        Case("history.test.ts::revive history states > should ignore unresolved ids as-is and log a warning", () => History(true));
        Case("history.test.ts::revive history states > should handle null, undefined, and primitive values", PrimitiveHistory);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string id, Action run) => cases.Add((id, () => { run(); return Task.CompletedTask; }));
        Case("machine JSON contains named child envelopes and actor ID markers", Tree);
        Case("native runtime restores the pinned upstream JSON fixture", UpstreamFixture);
        Case("JSON references restore into a concrete generic Actor property", Concrete);
        Case("parsed JSON snapshot reuse retains upstream revived context identity", Reuse);
        Case("JSON typed null context round-trips", NullContext);
        Case("JSON callback child restores primitive input without replaying parent entry", Callback);
        Case("JSON reducer root restores context and resumes transitions", Reducer);
        cases.Add(("JSON completed promise restores typed output without restarting", Promise));
        Case("JSON completed observable restores context without subscribing again", Observable);
        Case("live snapshot JSON uses actor markers, persisted JSON uses child envelopes", LiveChildren);
        Case("JSON omits unresolved child implementations and clears their references", Missing);
    }
    private static JsonSnapshot RoundTrip(object snapshot) => SnapshotJson.Parse(SnapshotJson.Serialize(snapshot));
    private static void Tags()
    {
        var machine = new StateMachine<int>(new() { Initial = "a", States = States(("a", new() { Tags = ["foo"] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); var saved = SnapshotJson.Serialize(actor.GetPersistedSnapshot()); actor.Stop();
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = SnapshotJson.Parse(saved) }).Start();
        Equal(true, restored.GetSnapshot().HasTag("foo")); restored.Stop();
    }
    private static void Stop()
    {
        var seen = new List<string>();
        var machine = new StateMachine<int>(new()
        {
            Exit = [MachineActions.Effect<int>((_, _) => seen.Add("root"))], Initial = "a",
            States = States(("a", new() { Exit = [MachineActions.Effect<int>((_, _) => seen.Add("a"))] }))
        }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); var saved = SnapshotJson.Serialize(actor.GetPersistedSnapshot()); actor.Stop();
        new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = SnapshotJson.Parse(saved) }).Start().Stop();
        Equal(0, seen.Count);
    }
    private static void Can()
    {
        var machine = new StateMachine<int>(new() { On = On("FOO", new() { Actions = [MachineActions.Effect<int>((_, _) => { })] }) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = RoundTrip(actor.GetSnapshot()) }).Start();
        Equal(true, restored.GetSnapshot().Can(new("FOO"))); actor.Stop(); restored.Stop();
    }
    private static (StateMachine<int> Machine, string Json) HistoryFixture()
    {
        var machine = new StateMachine<int>(new()
        {
            Initial = "on", States = States(("on", new()
            {
                Initial = "first", On = On("POWER", new() { Target = ["off"] }),
                States = States(("first", new() { On = On("SWITCH", new() { Target = ["second"] }) }),
                    ("second", new()), ("hist", new() { History = HistoryKind.Shallow }))
            }), ("off", new() { On = On("POWER", new() { Target = ["on.hist"] }) }))
        }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); actor.Send(new("SWITCH")); actor.Send(new("POWER"));
        var saved = SnapshotJson.Serialize(actor.GetPersistedSnapshot()); actor.Stop(); return (machine, saved);
    }
    private static void History(bool missing)
    {
        var (machine, json) = HistoryFixture();
        var data = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("JSON object missing.");
        if (missing) data["historyValue"] = JsonNode.Parse("{\"(machine).on.hist\":[{\"id\":\"nonexistent\"}]}");
        Equal("off", data["value"]?.GetValue<string>());
        var original = Console.Error; using var warning = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Console.SetError(warning);
            var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = SnapshotJson.Parse(data.ToJsonString()) }).Start();
            actor.Send(new("POWER")); Equal(true, actor.GetSnapshot().Matches(missing ? "on.first" : "on.second"));
            if (missing)
            {
                Equal(true, warning.ToString().Contains("Could not resolve StateNode for id: nonexistent", StringComparison.Ordinal));
                Equal(0, ((PersistedMachineSnapshot<int>)actor.GetPersistedSnapshot()).HistoryValue.Count);
            }
            actor.Stop();
        }
        finally { Console.SetError(original); }
    }
    private static void PrimitiveHistory()
    {
        var (machine, json) = HistoryFixture();
        foreach (var replacement in new[] { "null", "missing", "42", "\"foo\"", "true", "false" })
        {
            var data = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("JSON missing.");
            if (replacement == "missing") data.Remove("historyValue"); else data["historyValue"] = JsonNode.Parse(replacement);
            Equal("off", data["value"]?.GetValue<string>());
            var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = SnapshotJson.Parse(data.ToJsonString()) }).Start();
            actor.Send(new("POWER")); Equal(true, actor.GetSnapshot().Matches("on.first"));
            Equal(0, ((PersistedMachineSnapshot<int>)actor.GetPersistedSnapshot()).HistoryValue.Count); actor.Stop();
        }
    }
    private sealed record Context(IActor? Child, IActor?[] Array, List<IActor?> List, Dictionary<string, IActor?> Map);
    private static StateMachine<Context> TreeMachine() => new(new(), args =>
    {
        var child = args.Spawn(ActorSource.Named("counter"), "child", systemId: "counter-system", syncSnapshot: true);
        return new(child, [child], [child], new(StringComparer.Ordinal) { ["ref"] = child });
    }, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal)
    { ["counter"] = ActorSource.From(new TransitionLogic<int>((s, ev, _) => ev.Type == "INC" ? s + 1 : s, 7)) });
    private static void UpstreamFixture()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "upstream-json-snapshot.json"));
        var actor = new Actor<MachineSnapshot<Context>>(TreeMachine(), options: new() { Snapshot = SnapshotJson.Parse(json) }).Start();
        var state = actor.GetSnapshot(); var child = state.Children["child"] ?? throw new InvalidOperationException("Upstream child missing.");
        Equal(true, ReferenceEquals(state.Context.Child, child)); Equal(true, ReferenceEquals(state.Context.Array[0], child));
        Equal(true, ReferenceEquals(state.Context.List[0], child)); Equal(true, ReferenceEquals(state.Context.Map["ref"], child));
        Equal(true, ReferenceEquals(actor.System.Get("counter-system"), child));
        child.Send(new("INC")); Equal(8, ((TransitionSnapshot<int>)child.GetSnapshot()).Context); actor.Stop();
    }
    private static void Tree()
    {
        var machine = TreeMachine(); var actor = new Actor<MachineSnapshot<Context>>(machine).Start();
        var saved = SnapshotJson.Serialize(actor.GetPersistedSnapshot());
        Directory.CreateDirectory("tmp/xstate-parity");
        File.WriteAllText("tmp/xstate-parity/csharp-json-snapshot.json", saved);
        using (var json = JsonDocument.Parse(saved))
        {
            Equal("counter", json.RootElement.GetProperty("children").GetProperty("child").GetProperty("src").GetString());
            Equal(1, json.RootElement.GetProperty("context").GetProperty("child").GetProperty("xstate$$type").GetInt32());
            Equal("child", json.RootElement.GetProperty("context").GetProperty("map").GetProperty("ref").GetProperty("id").GetString());
        }
        actor.Stop(); var restored = new Actor<MachineSnapshot<Context>>(machine, options: new() { Snapshot = SnapshotJson.Parse(saved) }).Start();
        var context = restored.GetSnapshot().Context;
        var child = restored.GetSnapshot().Children["child"] ?? throw new InvalidOperationException("Child missing.");
        Equal(true, ReferenceEquals(child, context.Child)); Equal(true, ReferenceEquals(child, context.Array[0]));
        Equal(true, ReferenceEquals(child, context.List[0])); Equal(true, ReferenceEquals(child, context.Map["ref"]));
        Equal(true, ReferenceEquals(child, restored.System.Get("counter-system")));
        Equal(true, child.SyncSnapshot); child.Send(new("INC")); Equal(8, ((TransitionSnapshot<int>)child.GetSnapshot()).Context); restored.Stop();
    }
    private sealed record ConcreteContext(Actor<TransitionSnapshot<int>> Child);
    private static void Concrete()
    {
        var logic = new TransitionLogic<int>((s, _, _) => s, 42);
        var machine = new StateMachine<ConcreteContext>(new(), args => new((Actor<TransitionSnapshot<int>>)args.Spawn(ActorSource.Named("worker"), "child")),
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["worker"] = ActorSource.From(logic) });
        var actor = new Actor<MachineSnapshot<ConcreteContext>>(machine).Start();
        var restored = new Actor<MachineSnapshot<ConcreteContext>>(machine, options: new() { Snapshot = RoundTrip(actor.GetPersistedSnapshot()) }).Start();
        Equal(true, ReferenceEquals(restored.GetSnapshot().Context.Child, restored.GetSnapshot().Children["child"])); actor.Stop(); restored.Stop();
    }
    private static void Reuse()
    {
        var machine = TreeMachine(); var actor = new Actor<MachineSnapshot<Context>>(machine).Start(); var saved = RoundTrip(actor.GetPersistedSnapshot()); actor.Stop();
        var first = new Actor<MachineSnapshot<Context>>(machine, options: new() { Snapshot = saved }).Start();
        var second = new Actor<MachineSnapshot<Context>>(machine, options: new() { Snapshot = saved }).Start();
        Equal(true, ReferenceEquals(first.GetSnapshot().Context, second.GetSnapshot().Context));
        Equal(true, ReferenceEquals(first.GetSnapshot().Children["child"], second.GetSnapshot().Context.Child)); first.Stop(); second.Stop();
    }
    private static void NullContext()
    {
        var machine = new StateMachine<string?>(new(), _ => null); var actor = new Actor<MachineSnapshot<string?>>(machine).Start();
        var restored = new Actor<MachineSnapshot<string?>>(machine, options: new() { Snapshot = RoundTrip(actor.GetPersistedSnapshot()) }).Start();
        Equal<string?>(null, restored.GetSnapshot().Context); actor.Stop(); restored.Stop();
    }
    private static void Callback()
    {
        var seen = new List<object?>(); var entries = 0;
        var machine = new StateMachine<int>(new()
        {
            Entry = [MachineActions.Effect<int>((_, _) => entries++)],
            Invoke = [new() { Id = "callback", Source = ActorSource.From(new CallbackLogic(args => { seen.Add(args.Input); return null; })), Input = _ => 13 }]
        }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); var saved = RoundTrip(actor.GetPersistedSnapshot()); actor.Stop(); seen.Clear();
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = saved }).Start();
        Equal(1, entries); Equal(1, seen.Count); Equal<object?>(13, seen[0]); restored.Stop();
    }
    private sealed record Count(int Value);
    private static void Reducer()
    {
        var logic = new TransitionLogic<Count>((s, ev, _) => ev.Type == "INC" ? s with { Value = s.Value + 1 } : s, new Count(42));
        var actor = new Actor<TransitionSnapshot<Count>>(logic).Start();
        var restored = new Actor<TransitionSnapshot<Count>>(logic, options: new() { Snapshot = RoundTrip(actor.GetPersistedSnapshot()) }).Start();
        restored.Send(new("INC")); Equal(43, restored.GetSnapshot().Context.Value); actor.Stop(); restored.Stop();
    }
    private static async Task Promise()
    {
        var calls = 0; var logic = new PromiseLogic<Count>(_ => { calls++; return Task.FromResult(new Count(42)); });
        var actor = new Actor<PromiseSnapshot<Count>>(logic).Start(); await ActorTasks.ToPromiseAsync(actor).ConfigureAwait(false);
        var restored = new Actor<PromiseSnapshot<Count>>(logic, options: new() { Snapshot = RoundTrip(actor.GetPersistedSnapshot()) }).Start();
        Equal(1, calls); Equal<object?>(new Count(42), restored.GetSnapshot().Output);
    }
    private static void Observable()
    {
        var calls = 0; var logic = new ObservableLogic<Count>(_ => { calls++; return ObservableLogicTests.Of(new Count(42)); });
        var actor = new Actor<ObservableSnapshot<Count>>(logic).Start();
        var restored = new Actor<ObservableSnapshot<Count>>(logic, options: new() { Snapshot = RoundTrip(actor.GetPersistedSnapshot()) }).Start();
        Equal(1, calls); Equal(new Count(42), restored.GetSnapshot().Context);
    }
    private static void LiveChildren()
    {
        var machine = TreeMachine(); var actor = new Actor<MachineSnapshot<Context>>(machine).Start();
        using var live = JsonDocument.Parse(SnapshotJson.Serialize(actor.GetSnapshot()));
        var marker = live.RootElement.GetProperty("children").GetProperty("child");
        Equal(1, marker.GetProperty("xstate$$type").GetInt32()); Equal(false, marker.TryGetProperty("snapshot", out _));
        actor.Stop();
    }
    private static void Missing()
    {
        var machine = TreeMachine(); var actor = new Actor<MachineSnapshot<Context>>(machine).Start();
        var data = JsonNode.Parse(SnapshotJson.Serialize(actor.GetPersistedSnapshot())) ?? throw new InvalidOperationException("JSON missing.");
        data["children"]?["child"]?.AsObject().Remove("src"); actor.Stop();
        var restored = new Actor<MachineSnapshot<Context>>(machine, options: new() { Snapshot = SnapshotJson.Parse(data.ToJsonString()) }).Start();
        Equal(0, restored.GetSnapshot().Children.Count); Equal<IActor?>(null, restored.GetSnapshot().Context.Child); restored.Stop();
    }
}
