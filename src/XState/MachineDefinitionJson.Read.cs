using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
namespace XState;

public static partial class MachineDefinitionJson
{
    /// <summary>Reads JSON machine configuration. Context is supplied by the caller because definitions do not serialize it.</summary>
    public static StateConfig<TContext> Parse<TContext>(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new StateConfig<TContext>(document.RootElement);
    }
    private static IEnumerable<JsonElement> List(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray(),
        JsonValueKind.Null or JsonValueKind.Undefined => [],
        _ => [value]
    };
    internal static string Text(JsonElement value) => value.GetString() ?? throw new JsonException("Expected a string.");
    internal static string[] Strings(JsonElement value) => List(value).Select(Text).ToArray();
    internal static object? Value(JsonElement value) => SnapshotJson.ReadValue(value);
    internal static MachineAction<T>[] Actions<T>(JsonElement value) => List(value).Select(action => action.ValueKind switch
    {
        JsonValueKind.String => MachineActions.Named<T>(Text(action)),
        JsonValueKind.Object => MachineActions.FromProperties<T>((IReadOnlyDictionary<string, object?>)(Value(action) ?? throw new JsonException("Missing action properties."))),
        JsonValueKind.Null => MachineActions.SerializedNull<T>(),
        _ => throw new JsonException("Action must be a string, an object or a null array element.")
    }).ToArray();
    internal static MachineGuard<T>? Guard<T>(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => MachineGuards.Named<T>(Text(value)),
        JsonValueKind.Object => value.TryGetProperty("params", out var parameters)
            ? MachineGuards.Named<T>(Text(value.GetProperty("type")), Value(parameters))
            : MachineGuards.Named<T>(Text(value.GetProperty("type"))),
        _ => throw new JsonException("Guard must be a name or an object.")
    };
    internal static TransitionConfig<T>[] Transitions<T>(JsonElement value) =>
        List(value).Select(transition => new TransitionConfig<T>(transition)).ToArray();
    internal static IReadOnlyDictionary<string, IReadOnlyList<TransitionConfig<T>>> TransitionMap<T>(JsonElement value)
    {
        var result = new Dictionary<string, IReadOnlyList<TransitionConfig<T>>>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) result[property.Name] = Transitions<T>(property.Value);
        return result;
    }
    internal static InvokeConfig<T>[] Invokes<T>(JsonElement value) => List(value).Select(invoke => new InvokeConfig<T>(invoke)).ToArray();
    internal static IReadOnlyDictionary<string, StateConfig<T>> States<T>(JsonElement value)
    {
        var result = new Dictionary<string, StateConfig<T>>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) result[property.Name] = new(property.Value);
        return result;
    }
    // XState indexes child states using initial.target as a JS property key. A serialized
    // definition contains an array of absolute IDs here; it is not silently repaired into a local key.
    internal static string InitialTarget(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? string.Join(',', value.EnumerateArray().Select(Text)) : Text(value);
}

public sealed partial class StateConfig<TContext>
{
    public StateConfig() { }
    internal StateConfig(JsonElement value)
    {
        foreach (var property in value.EnumerateObject())
        {
            var item = property.Value;
            switch (property.Name)
            {
                case "id": Id = MachineDefinitionJson.Text(item); break;
                case "version": Version = item.GetString(); break;
                case "schemas": Schemas = MachineDefinitionJson.Value(item); break;
                case "type": Kind = MachineDefinitionJson.Text(item) switch
                {
                    "atomic" => StateKind.Atomic, "compound" => StateKind.Compound, "parallel" => StateKind.Parallel,
                    "final" => StateKind.Final, "history" => StateKind.History, _ => throw new JsonException("Unknown state node type.")
                }; break;
                case "description": Description = item.GetString(); break;
                case "meta": Meta = MachineDefinitionJson.Value(item); break;
                case "output": OutputValue = MachineDefinitionJson.Value(item); break;
                case "tags": Tags = MachineDefinitionJson.Strings(item); break;
                case "history": History = item.ValueKind switch
                {
                    JsonValueKind.False or JsonValueKind.Null => null, JsonValueKind.True => HistoryKind.Shallow,
                    JsonValueKind.String when item.GetString() == "shallow" => HistoryKind.Shallow,
                    JsonValueKind.String when item.GetString() == "deep" => HistoryKind.Deep,
                    _ => throw new JsonException("Unknown history kind.")
                }; break;
                case "target": HistoryTarget = MachineDefinitionJson.Strings(item); break;
                case "states": States = MachineDefinitionJson.States<TContext>(item); break;
                case "on": On = MachineDefinitionJson.TransitionMap<TContext>(item); break;
                case "after": After = MachineDefinitionJson.TransitionMap<TContext>(item); break;
                case "always": Always = MachineDefinitionJson.Transitions<TContext>(item); break;
                case "onDone": OnDone = MachineDefinitionJson.Transitions<TContext>(item); break;
                case "invoke": Invoke = MachineDefinitionJson.Invokes<TContext>(item); break;
                case "entry": Entry = MachineDefinitionJson.Actions<TContext>(item); break;
                case "exit": Exit = MachineDefinitionJson.Actions<TContext>(item); break;
                case "initial":
                    if (item.ValueKind == JsonValueKind.String) Initial = item.GetString();
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        InitialIsObject = true;
                        Initial = item.TryGetProperty("target", out var initialTarget) ? MachineDefinitionJson.InitialTarget(initialTarget) : "";
                        if (item.TryGetProperty("actions", out var actions)) InitialActions = MachineDefinitionJson.Actions<TContext>(actions);
                        if (item.TryGetProperty("meta", out var meta)) InitialMeta = MachineDefinitionJson.Value(meta);
                        if (item.TryGetProperty("description", out var description)) InitialDescription = description.GetString();
                    }
                    else if (item.ValueKind != JsonValueKind.Null) throw new JsonException("Initial must be a state key or a transition object.");
                    break;
                // key/order/source/transitions are compiled metadata, not config inputs in XState.
            }
        }
    }
}

public sealed partial class TransitionConfig<TContext>
{
    public TransitionConfig() { }
    internal TransitionConfig(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var target = MachineDefinitionJson.Text(value); if (target.Length != 0) Target = [target]; return;
        }
        foreach (var property in value.EnumerateObject())
        {
            var item = property.Value;
            switch (property.Name)
            {
                case "target":
                    if (item.ValueKind != JsonValueKind.String || item.GetString()?.Length != 0) Target = MachineDefinitionJson.Strings(item);
                    break;
                case "meta": Meta = MachineDefinitionJson.Value(item); break;
                case "description": Description = item.GetString(); break;
                case "reenter": if (item.ValueKind != JsonValueKind.Null) Reenter = item.GetBoolean(); break;
                case "guard": Guard = MachineDefinitionJson.Guard<TContext>(item); break;
                case "actions": Actions = MachineDefinitionJson.Actions<TContext>(item); break;
            }
        }
    }
}

public sealed partial class InvokeConfig<TContext>
{
    public InvokeConfig() { }
    [SetsRequiredMembers]
    internal InvokeConfig(JsonElement value)
    {
        Source = ActorSource.Named(MachineDefinitionJson.Text(value.GetProperty("src")));
        foreach (var property in value.EnumerateObject())
        {
            var item = property.Value;
            switch (property.Name)
            {
                case "id": Id = item.GetString(); break;
                case "systemId": SystemId = item.GetString(); break;
                case "onDone": OnDone = MachineDefinitionJson.Transitions<TContext>(item); break;
                case "onError": OnError = MachineDefinitionJson.Transitions<TContext>(item); break;
                case "onSnapshot": OnSnapshot = MachineDefinitionJson.Transitions<TContext>(item); break;
                case "input": InputValue = MachineDefinitionJson.Value(item); break;
            }
        }
    }
}
