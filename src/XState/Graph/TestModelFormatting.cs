using System.Collections;
using System.Reflection;
using System.Text.Json;
namespace XState.Graph;

internal interface IGraphDescriptiveSnapshot { string DescribeForGraph(); }
internal static class TestModelFormatting
{
    internal static string Event(MachineEvent ev)
    {
        using var serialized = JsonDocument.Parse(GraphSerialization.Event(ev));
        var other = serialized.RootElement.EnumerateObject().Where(p => p.Name != "type").ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return ev.Type + (other.Count == 0 && !SnapshotJson.HasEnumerableProperties(ev.Payload, "type") ? "" : " (" + GraphSerialization.Value(other) + ")");
    }
    private static bool Truthy(object? value) => value switch
    {
        null => false, bool boolean => boolean, string text => text.Length != 0,
        double number => number != 0 && !double.IsNaN(number), float number => number != 0 && !float.IsNaN(number),
        int number => number != 0, long number => number != 0,
        JsonElement json => json.ValueKind switch { JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false, JsonValueKind.String => json.GetString()?.Length > 0, JsonValueKind.Number => json.GetDouble() != 0, _ => true },
        _ => true
    };
    private static object? Description(object meta) => meta switch
    {
        JsonElement json when json.ValueKind == JsonValueKind.Object && json.TryGetProperty("description", out var value) => value.ValueKind == JsonValueKind.String ? value.GetString() : value.Clone(),
        IDictionary dictionary => dictionary["description"] ?? dictionary["Description"],
        _ => meta.GetType().GetProperty("description", BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public)?.GetValue(meta)
    };
    internal static string Describe<TContext>(MachineSnapshot<TContext> snapshot)
    {
        var contextJson = GraphSerialization.Value(snapshot.Context);
        var hasContext = SnapshotJson.HasEnumerableProperties(snapshot.Context);
        var states = new List<string>();
        foreach (var node in snapshot.Nodes.Where(node => node.IsAtomic))
        {
            var meta = snapshot.GetMeta().GetValueOrDefault(node.Id);
            if (!Truthy(meta)) { states.Add("\"" + string.Join('.', node.Path) + "\""); continue; }
            var description = Description(meta ?? throw new InvalidOperationException("Metadata missing."));
            states.Add(description is Func<MachineSnapshot<TContext>, string> dynamicDescription ? dynamicDescription(snapshot) : Truthy(description)
                ? "\"" + description + "\"" : snapshot.Value.ToJson());
        }
        return "state" + (states.Count == 1 ? "" : "s") + " " + string.Join(", ", states) + (hasContext ? "(" + contextJson + ")" : "");
    }
    internal static string PathResult<TSnapshot>(StatePath<TSnapshot> path, TestPathResult<TSnapshot> result, TestModelOptions<TSnapshot> options) where TSnapshot : class, IActorSnapshot
    {
        var serializeState = options.SerializeState ?? throw new InvalidOperationException("State serializer is null.");
        var serializeEvent = options.SerializeEvent ?? throw new InvalidOperationException("Event serializer is null.");
        var target = serializeState(path.State, path.Steps.Count == 0 ? null : path.Steps[^1].Event, null);
        var lines = new List<string>();
        for (var i = 0; i < result.Steps.Count; i++)
        {
            var step = result.Steps[i].Step;
            lines.Add("\tState: " + serializeState(step.State, i == 0 ? null : result.Steps[i - 1].Step.Event, null) + "\n\tEvent: " + serializeEvent(step.Event));
        }
        lines.Add("\tState: " + target);
        return "\nPath:\n" + string.Join("\n\n", lines);
    }
}
