using System.Text.Json;
using System.Text.Json.Nodes;
using XState;

namespace XStatePort.Tests;

internal static class DataTests
{
    public static void Register(List<(string Id, Action Run)> cases, string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var test in document.RootElement.GetProperty("tests").EnumerateArray())
        {
            var definition = test.Clone();
            var id = Text(test.GetProperty("id"));
            cases.Add((id, () => Run(definition)));
        }
    }

    private static void Run(JsonElement test)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var step in test.GetProperty("steps").EnumerateArray())
        {
            switch (Text(step.GetProperty("op")))
            {
                case "declare": variables.Add(Text(step.GetProperty("name")), Evaluate(step.GetProperty("value"), variables)); break;
                case "evaluate": Evaluate(step.GetProperty("value"), variables); break;
                case "assert":
                    var actual = Evaluate(step.GetProperty("actual"), variables);
                    var expected = Evaluate(step.GetProperty("expected"), variables);
                    var matcher = Text(step.GetProperty("matcher"));
                    var passes = matcher switch
                    {
                        "toBeTruthy" => Truthy(actual),
                        "toBeFalsy" => !Truthy(actual),
                        "toBe" => Same(actual, expected),
                        "toEqual" or "toStrictEqual" => JsonNode.DeepEquals(ToJson(actual), ToJson(expected)),
                        _ => throw new InvalidOperationException("Unknown matcher: " + matcher)
                    };
                    if (!passes) throw new InvalidOperationException($"{matcher}: expected {ToJson(expected)}, actual {ToJson(actual)}");
                    break;
                default: throw new InvalidOperationException("Unsupported test step.");
            }
        }
    }

    private static object? Evaluate(JsonElement expression, Dictionary<string, object?> variables)
    {
        switch (Text(expression.GetProperty("op")))
        {
            case "variable": return variables[Text(expression.GetProperty("name"))];
            case "literal": return Unwrap(expression.GetProperty("value"));
            case "machine":
                return new FixtureMachine(expression.GetProperty("config"));
            case "initialSnapshot":
                var initialMachine = Evaluate(expression.GetProperty("machine"), variables) as FixtureMachine ?? throw new InvalidOperationException("Machine missing.");
                return initialMachine.InitialSnapshot();
            case "nextSnapshot":
                var nextMachine = Evaluate(expression.GetProperty("machine"), variables) as FixtureMachine ?? throw new InvalidOperationException("Machine missing.");
                var previous = Evaluate(expression.GetProperty("snapshot"), variables) as MachineSnapshot<JsonElement> ?? throw new InvalidOperationException("Snapshot missing.");
                return nextMachine.NextSnapshot(previous, ReadEvent(Evaluate(expression.GetProperty("event"), variables)));            case "actor":
                var fixture = Evaluate(expression.GetProperty("machine"), variables) as FixtureMachine ?? throw new InvalidOperationException("Machine missing.");
                return fixture.CreateActor();
            case "track":
                var tracked = Evaluate(expression.GetProperty("machine"), variables) as FixtureMachine ?? throw new InvalidOperationException("Machine missing.");
                return tracked.Track();
            case "invoke":
                var function = Evaluate(expression.GetProperty("function"), variables) as Func<string[]> ?? throw new InvalidOperationException("Unsupported function.");
                return function();            case "property":
                var snapshot = Evaluate(expression.GetProperty("receiver"), variables) as MachineSnapshot<JsonElement> ?? throw new InvalidOperationException("Expected a snapshot.");
                return Text(expression.GetProperty("name")) switch
                {
                    "value" => snapshot.Value.IsAtomic ? snapshot.Value.AtomicValue : snapshot.Value,
                    "status" => snapshot.Status.ToString().ToLowerInvariant(),
                    "context" => snapshot.Context,
                    "output" => snapshot.Output,
                    _ => throw new InvalidOperationException("Unsupported snapshot property.")
                };
            case "call":
                var receiver = Evaluate(expression.GetProperty("receiver"), variables);
                var arguments = expression.GetProperty("args").EnumerateArray().Select(a => Evaluate(a, variables)).ToArray();
                var method = Text(expression.GetProperty("method"));
                if (receiver is FixtureMachine fixtureReceiver && method == "resolveState")
                {
                    if (arguments.Length != 1 || arguments[0] is not JsonElement resolveConfig) throw new InvalidOperationException("Invalid resolveState configuration.");
                    return fixtureReceiver.Resolve(resolveConfig);
                }                if (receiver is Actor<MachineSnapshot<JsonElement>> actor)
                {
                    switch (method)
                    {
                        case "start": return actor.Start();
                        case "stop": return actor.Stop();
                        case "send": actor.Send(ReadEvent(arguments[0])); return null;
                        case "getSnapshot": return actor.GetSnapshot();
                    }
                }
                if (receiver is MachineSnapshot<JsonElement> state)
                {
                    return method switch
                    {
                        "matches" => state.Matches(ReadStateValue(arguments[0])),
                        "hasTag" => state.HasTag((string)(arguments[0] ?? throw new InvalidOperationException("Tag missing."))),
                        "can" => state.Can(ReadEvent(arguments[0])),
                        _ => throw new InvalidOperationException("Unsupported snapshot method: " + method)
                    };
                }
                throw new InvalidOperationException("Unsupported receiver or method: " + method);
            default: throw new InvalidOperationException("Unsupported expression.");
        }
    }

    // Native translation of upstream test/utils.ts. Instrument config before any actor is created.
    private sealed class FixtureMachine
    {
        private readonly JsonElement config;
        private StateMachine<JsonElement> machine;
        private bool used;
        private bool tracked;
        public FixtureMachine(JsonElement config)
        {
            this.config = config.Clone();
            machine = Compile(null);
        }
        private StateMachine<JsonElement> Compile(Action<string>? tracker) => new(ReadConfig(config, tracker), _ => JsonSerializer.SerializeToElement(new Dictionary<string, object?>()));
        public MachineSnapshot<JsonElement> InitialSnapshot() { used = true; return machine.GetInitialSnapshot(); }
        public MachineSnapshot<JsonElement> NextSnapshot(MachineSnapshot<JsonElement> snapshot, MachineEvent ev) => machine.GetNextSnapshot(snapshot, ev);
        public MachineSnapshot<JsonElement> Resolve(JsonElement options)
        {
            foreach (var property in options.EnumerateObject()) if (property.Name is not ("value" or "context" or "status")) throw new InvalidOperationException("Unsupported resolveState option: " + property.Name);
            var value = StateValue.Parse(options.GetProperty("value").GetRawText());
            var context = options.TryGetProperty("context", out var data) ? data.Clone() : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
            var status = options.TryGetProperty("status", out var state) ? Enum.Parse<SnapshotStatus>(Text(state), true) : SnapshotStatus.Active;
            return machine.ResolveState(value, context, status);
        }        public Actor<MachineSnapshot<JsonElement>> CreateActor() { used = true; return new(machine); }
        public Func<string[]> Track()
        {
            if (used || tracked) throw new InvalidOperationException("Tracking must be installed once before actor creation.");
            tracked = true;
            var logs = new List<string>();
            machine = Compile(logs.Add);
            return () => { var result = logs.ToArray(); logs.Clear(); return result; };
        }
    }
    private static bool Same(object? actual, object? expected)
    {
        if (actual is null || expected is null) return actual is null && expected is null;
        if (actual is string or bool or double) return actual.Equals(expected);
        return ReferenceEquals(actual, expected);
    }

    private static bool Truthy(object? value) => value switch
    {
        null => false,
        bool boolean => boolean,
        string text => text.Length > 0,
        double number => number != 0 && !double.IsNaN(number),
        _ => true
    };

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        StateValue state => JsonNode.Parse(state.ToJson()),
        JsonElement json => JsonNode.Parse(json.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value)
    };

    private static object? Unwrap(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Text(value),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.Null => null,
        _ => value.Clone()
    };

    private static StateValue ReadStateValue(object? value) => value switch
    {
        string name => StateValue.Atomic(name),
        JsonElement json => StateValue.Parse(json.GetRawText()),
        _ => throw new InvalidOperationException("Invalid state value.")
    };

    private static MachineEvent ReadEvent(object? value)
    {
        if (value is not JsonElement json || json.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid event.");
        return new(Text(json.GetProperty("type")), json.Clone());
    }

    private static string Text(JsonElement value) => value.GetString() ?? throw new InvalidOperationException("Expected a non-null string.");
    private static string? OptionalString(JsonElement value, string key) => value.TryGetProperty(key, out var property) ? Text(property) : null;
    private static string[] Strings(JsonElement value) => value.ValueKind == JsonValueKind.String ? [Text(value)] : value.EnumerateArray().Select(Text).ToArray();

    internal static StateConfig<JsonElement> ReadConfig(JsonElement value, Action<string>? tracker = null, string path = "")
    {
        var allowed = new HashSet<string>(["id", "initial", "states", "on", "always", "onDone", "type", "history", "target", "tags", "meta", "entry", "exit"], StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!allowed.Contains(property.Name)) throw new InvalidOperationException("Unsupported configuration: " + property.Name);
        var states = new Dictionary<string, StateConfig<JsonElement>>(StringComparer.Ordinal);
        if (value.TryGetProperty("states", out var children)) foreach (var child in children.EnumerateObject()) states[child.Name] = ReadConfig(child.Value, tracker, path.Length == 0 ? child.Name : path + "." + child.Name);
        var on = new Dictionary<string, IReadOnlyList<TransitionConfig<JsonElement>>>(StringComparer.Ordinal);
        if (value.TryGetProperty("on", out var transitions)) foreach (var transition in transitions.EnumerateObject()) on[transition.Name] = ReadTransitions(transition.Value);
        var type = OptionalString(value, "type");
        StateKind? kind = type switch
        {
            null => null, "atomic" => StateKind.Atomic, "compound" => StateKind.Compound, "parallel" => StateKind.Parallel,
            "final" => StateKind.Final, "history" => StateKind.History, _ => throw new InvalidOperationException("Unsupported state type: " + type)
        };
        var entry = value.TryGetProperty("entry", out var entryValue) ? ReadActions(entryValue) : [];
        var exit = value.TryGetProperty("exit", out var exitValue) ? ReadActions(exitValue) : [];
        if (tracker is not null)
        {
            entry = [MachineActions.Effect<JsonElement>((_, _) => tracker("enter: " + (path.Length == 0 ? "__root__" : path))), .. entry];
            exit = [MachineActions.Effect<JsonElement>((_, _) => tracker("exit: " + (path.Length == 0 ? "__root__" : path))), .. exit];
        }
        var config = new StateConfig<JsonElement>()
        {
            Entry = entry, Exit = exit,
            Id = OptionalString(value, "id"), Initial = OptionalString(value, "initial"), Kind = kind, States = states, On = on,
            Always = value.TryGetProperty("always", out var always) ? ReadTransitions(always) : [],
            OnDone = value.TryGetProperty("onDone", out var done) ? ReadTransitions(done) : [],
            History = value.TryGetProperty("history", out var history) && history.ValueKind != JsonValueKind.False ? (history.ValueKind == JsonValueKind.String && Text(history) == "deep" ? HistoryKind.Deep : HistoryKind.Shallow) : null,
            HistoryTarget = value.TryGetProperty("target", out var target) ? Strings(target) : [],
            Tags = value.TryGetProperty("tags", out var tags) ? Strings(tags) : []
        };
        if (value.TryGetProperty("meta", out var meta)) config.Meta = meta.Clone();
        return config;
    }

    private static TransitionConfig<JsonElement>[] ReadTransitions(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().SelectMany(ReadTransitions).ToArray();
        if (value.ValueKind == JsonValueKind.String) return [new() { Target = [Text(value)] }];
        foreach (var property in value.EnumerateObject()) if (property.Name is not ("target" or "guard" or "reenter" or "actions")) throw new InvalidOperationException("Unsupported transition: " + property.Name);
        MachineGuard<JsonElement>? guard = null;
        if (value.TryGetProperty("guard", out var condition))
        {
            if (condition.ValueKind != JsonValueKind.Object || condition.EnumerateObject().Count() != 1)
                throw new InvalidOperationException("Unsupported fixture guard.");
            if (condition.TryGetProperty("__constantGuard", out var constantValue))
            {
                var constant = constantValue.GetBoolean();
                guard = MachineGuards.Predicate<JsonElement>((_, _) => constant);
            }
            else if (condition.TryGetProperty("__stateIn", out var stateValue))
                guard = MachineGuards.StateIn<JsonElement>(StateValue.Parse(stateValue.GetRawText()));
            else throw new InvalidOperationException("Unsupported fixture guard.");
        }
        return [new()
        {
            Target = value.TryGetProperty("target", out var target) ? Strings(target) : null,
            Reenter = value.TryGetProperty("reenter", out var reenter) && reenter.GetBoolean(), Guard = guard,
            Actions = value.TryGetProperty("actions", out var actions) ? ReadActions(actions) : []
        }];
    }
    private static MachineAction<JsonElement>[] ReadActions(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().SelectMany(ReadActions).ToArray();
        if (value.ValueKind == JsonValueKind.String) return [MachineActions.Named<JsonElement>(Text(value))];
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 1 || !value.TryGetProperty("__raiseEvent", out var raised))
            throw new InvalidOperationException("Unsupported fixture action.");
        var ev = ReadEvent(raised);
        return [MachineActions.Raise<JsonElement>((_, _) => ev)];
    }

}

