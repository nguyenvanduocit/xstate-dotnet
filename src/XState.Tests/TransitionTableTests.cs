using System.Text.Json;
using System.Text.Json.Nodes;
using XState;
namespace XStatePort.Tests;

internal static class TransitionTableTests
{
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/upstream-transition-tables.json")));
        foreach (var table in document.RootElement.GetProperty("tables").EnumerateArray())
        {
            var config = table.GetProperty("config").Clone();
            foreach (var test in table.GetProperty("tests").EnumerateArray())
            {
                var definition = test.Clone(); var id = Text(definition.GetProperty("id"));
                cases.Add((id, () => Run(config, definition, id)));
            }
        }
        var historyTable = document.RootElement.GetProperty("tables").EnumerateArray().Single(table => Text(table.GetProperty("source")) == "packages/core/test/examples/6.8.test.ts");
        var historyConfig = historyTable.GetProperty("config").Clone();
        cases.Add(("packages/core/test/examples/6.8.test.ts::Example 6.8 > should respect the history mechanism", () => ActorHistory(historyConfig)));
    }
    private static void ActorHistory(JsonElement config)
    {
        var emptyContext = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        var machine = new StateMachine<JsonElement>(DataTests.ReadConfig(config), _ => emptyContext);
        var actor = new Actor<MachineSnapshot<JsonElement>>(machine).Start();
        try
        {
            var initial = JsonSerializer.Deserialize<JsonElement>(actor.GetSnapshot().Value.ToJson());
            actor.Send(new("1")); actor.Send(new("6")); actor.Send(new("5"));
            AssertValue(JsonSerializer.SerializeToElement(new { A = "C" }), actor.GetSnapshot().Value);
            Observations["actorHistory"] = new { initial, value = JsonSerializer.Deserialize<JsonElement>(actor.GetSnapshot().Value.ToJson()) };
        }
        finally { actor.Stop(); }
    }
    private static string Text(JsonElement value) => value.GetString() ?? throw new InvalidOperationException("String missing from transition table.");
    private static void AssertValue(JsonElement expected, StateValue actual)
    {
        if (!JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(actual.ToJson())))
            throw new InvalidOperationException($"Expected state {expected.GetRawText()}, got {actual.ToJson()}.");
    }
    private static void Run(JsonElement config, JsonElement definition, string id)
    {
        var emptyContext = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        var machine = new StateMachine<JsonElement>(DataTests.ReadConfig(config), _ => emptyContext);
        var from = Text(definition.GetProperty("from"));
        var value = from.StartsWith('{') ? StateValue.Parse(from) : StateValue.Atomic(from);
        var initial = machine.ResolveState(value, emptyContext);
        var snapshot = initial;
        foreach (var ev in definition.GetProperty("events").EnumerateArray())
            snapshot = ActorTransitions.GetNextSnapshot(machine, snapshot, new MachineEvent(Text(ev)));
        switch (Text(definition.GetProperty("kind")))
        {
            case "unchanged": AssertValue(JsonSerializer.SerializeToElement(JsonNode.Parse(machine.ResolveState(value, emptyContext).Value.ToJson())), snapshot.Value); break;
            case "equals": AssertValue(definition.GetProperty("expected"), snapshot.Value); break;
            case "matches":
                var expected = Text(definition.GetProperty("expected"));
                if (!snapshot.Matches(expected)) throw new InvalidOperationException($"State {snapshot.Value.ToJson()} does not match {expected}.");
                break;
            default: throw new InvalidOperationException("Unknown transition table assertion.");
        }
        Observations[id] = new { initial = JsonSerializer.Deserialize<JsonElement>(initial.Value.ToJson()), value = JsonSerializer.Deserialize<JsonElement>(snapshot.Value.ToJson()) };
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("transition table differential observations export", () =>
    {
        File.WriteAllText("tmp/xstate-parity/csharp-transition-tables.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask;
    }));
}
