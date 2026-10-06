using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
using ObjectContext = System.Collections.Generic.IReadOnlyDictionary<string, object?>;
namespace XStatePort.Tests;
internal static class JsonDefinitionTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "machine.schema.json")));
    public static void Register(List<(string Id, Action Run)> cases)
    {
        cases.Add(("packages/core/test/json.test.ts::json > should serialize the machine", SerializeMachine));
        cases.Add(("packages/core/test/json.test.ts::json > should detect an invalid machine", InvalidMachine));
        cases.Add(("packages/core/test/json.test.ts::json > should not double-serialize invoke transitions", RoundTrip));
    }
    private static void SerializeMachine()
    {
        var machine = new StateMachine<ObjectContext>(new()
        {
            Initial = "foo", Version = "1.0.0", Invoke = [new() { Id = "invokeId", Source = ActorSource.Named("invokeSrc") }],
            States = States<ObjectContext>(("testActions", new()
            {
                Invoke = [new() { Id = "invokeId", Source = ActorSource.Named("invokeSrc") }],
                Entry = [MachineActions.Named<ObjectContext>("stringActionType"),
                    MachineActions.FromProperties<ObjectContext>(new Dictionary<string, object?> { ["type"] = "objectActionType" }),
                    MachineActions.FromProperties<ObjectContext>(new Dictionary<string, object?> { ["type"] = "objectActionTypeWithExec", ["exec"] = (Func<bool>)(() => true), ["other"] = "any" }),
                    MachineActions.Effect<ObjectContext>((_, _) => { }, "actionFunction"),
                    MachineActions.Assign(new Dictionary<string, ContextPropertyAssignment> { ["number"] = ContextPropertyAssignment.Value(10), ["string"] = ContextPropertyAssignment.Value("test"), ["evalNumber"] = ContextPropertyAssignment.Expression(_ => 42) }),
                    MachineActions.Assign<ObjectContext>(args => new Dictionary<string, object?>(args.Context))],
                On = On<ObjectContext>(("TO_FOO", new() { Target = ["foo", "bar"], Guard = MachineGuards.Predicate<ObjectContext>((context, _) => context["string"] is string text && text.Length != 0) })),
                After = On<ObjectContext>(("1000", new() { Target = ["bar"] }))
            }), ("foo", new()), ("bar", new()), ("testHistory", new() { Kind = StateKind.History, History = HistoryKind.Deep }),
                ("testFinal", new() { Kind = StateKind.Final, OutputValue = new { something = "else" } }),
                ("testParallel", new() { Kind = StateKind.Parallel, States = States<ObjectContext>(
                    ("one", new() { Initial = "inactive", States = States<ObjectContext>(("inactive", new())) }),
                    ("two", new() { Initial = "inactive", States = States<ObjectContext>(("inactive", new())) })) })),
            OutputValue = new { result = 42 }
        }, _ => new Dictionary<string, object?> { ["number"] = 0, ["string"] = "hello" });
        using var document = JsonDocument.Parse(MachineDefinitionJson.Serialize(machine.Definition));
        var result = Schema.Value.Evaluate(document.RootElement);
        if (!result.IsValid) throw new InvalidOperationException(JsonSerializer.Serialize(result));
        Results["machine"] = document.RootElement.Clone(); Results["valid"] = result.IsValid;
    }
    private static void InvalidMachine()
    {
        using var document = JsonDocument.Parse("""{"id":"something","key":"something","type":"invalid type","states":{}}""");
        var result = Schema.Value.Evaluate(document.RootElement); Equal(false, result.IsValid); Results["invalid"] = result.IsValid;
    }
    private static void RoundTrip()
    {
        var machine = new StateMachine<int>(new() { Initial = "active", States = States<int>(
            ("active", new() { Id = "active", Invoke = [new() { Source = ActorSource.Named("someSrc"), OnDone = [new() { Target = ["foo"] }], OnError = [new() { Target = ["bar"] }] }], On = On<int>(("EVENT", new() { Target = ["foo"] })) }),
            ("foo", new()), ("bar", new())) }, _ => 0);
        var json = MachineDefinitionJson.Serialize(machine);
        var revived = new StateMachine<int>(MachineDefinitionJson.Parse<int>(json), _ => 0);
        var transitions = revived.States["active"].Transitions.Values.SelectMany(value => value).ToArray();
        Equal(3, transitions.Length);
        var events = new[] { "EVENT", "xstate.done.actor.0.active", "xstate.error.actor.0.active" };
        var targets = new[] { "#(machine).foo", "#(machine).foo", "#(machine).bar" };
        for (var i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i]; Equal(events[i], transition.EventType); Equal(false, transition.Reenter);
            Equal("#active", "#" + transition.Source.Id); Equal<object?>(null, transition.Guard); Equal(0, transition.Actions.Count);
            Equal(1, transition.Targets.Count); Equal(targets[i], "#" + transition.Targets[0].Id);
        }
        Equal(3, revived.GetStateNodeById("active").Transitions.Values.Sum(value => value.Count));
        var serialized = transitions.Select(transition => JsonSerializer.Deserialize<JsonElement>(MachineDefinitionJson.SerializeTransition<int>(transition))).ToArray();
        for (var i = 0; i < serialized.Length; i++)
        {
            Equal(events[i], serialized[i].GetProperty("eventType").GetString()); Equal("#active", serialized[i].GetProperty("source").GetString());
            Equal(targets[i], serialized[i].GetProperty("target")[0].GetString()); Equal(false, serialized[i].GetProperty("reenter").GetBoolean());
            Equal(0, serialized[i].GetProperty("actions").GetArrayLength()); Equal(false, serialized[i].TryGetProperty("guard", out _));
        }
        Results["roundTripTransitionJson"] = serialized;
        Results["roundTripTransitions"] = transitions.Select(transition => new { eventType = transition.EventType, reenter = transition.Reenter, source = "#" + transition.Source.Id, target = transition.Targets.Select(target => "#" + target.Id).ToArray(), actions = Array.Empty<object>(), guard = transition.Guard }).ToArray();
        using var document = JsonDocument.Parse(json); Results["roundTripJson"] = document.RootElement.Clone();
    }
    private static void ParserBehavior()
    {
        const string json = """{"id":"parsed","initial":"a","invoke":{"src":"reader","input":{"count":42}},"states":{"a":{"on":{"GO":{"target":"b","actions":{"type":"record","params":null}}}},"b":{"type":"final","output":7}},"output":null}""";
        object? input = null; var actions = 0;
        var config = MachineDefinitionJson.Parse<int>(json);
        var machine = new StateMachine<int>(config, _ => 0,
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["reader"] = ActorSource.From(new CallbackLogic(args => { input = args.Input; return null; })) },
            actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["record"] = MachineActions.Effect<int>(args => { Equal(true, args.HasParameters); Equal<object?>(null, args.Parameters); actions++; }) });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try
        {
            Equal(true, ReferenceEquals(config.Invoke[0].InputValue, input)); Equal(42, ((IReadOnlyDictionary<string, object?>)(input ?? throw new InvalidOperationException("Input missing.")))["count"]);
            actor.Send(new("GO")); Equal(SnapshotStatus.Done, actor.GetSnapshot().Status); Equal<object?>(null, actor.GetSnapshot().Output); Equal(1, actions);
            using var document = JsonDocument.Parse(MachineDefinitionJson.Serialize(machine)); Results["parsedConfigJson"] = document.RootElement.Clone();
            Results["parsedBehavior"] = new { input, actions, status = actor.GetSnapshot().Status.ToString().ToLowerInvariant(), output = actor.GetSnapshot().Output };
        }
        finally { actor.Stop(); }
        var broken = new StateMachine<int>(MachineDefinitionJson.Parse<int>("""{"entry":[null]}"""), _ => 0);
        Equal(1, broken.Definition.Entry.Count); Equal<ActionDefinition?>(null, broken.Definition.Entry[0]);
        using var brokenJson = JsonDocument.Parse(MachineDefinitionJson.Serialize(broken)); Results["nullActionJson"] = brokenJson.RootElement.Clone();
        var failed = new Actor<MachineSnapshot<int>>(broken); Exception? error = null;
        using var subscription = failed.Subscribe(onError: value => error = ActorErrors.ToException(value));
        failed.Start();
        try { Equal(SnapshotStatus.Error, failed.GetSnapshot().Status); Equal("Cannot read properties of null (reading 'type')", error?.Message); }
        finally { failed.Stop(); }
        Results["nullActionError"] = error?.Message ?? throw new InvalidOperationException("Expected null-action error.");
        var restored = new StateMachine<int>(MachineDefinitionJson.Parse<int>(((JsonElement)Results["roundTripJson"]).GetRawText()), _ => 0);
        var invalidInitial = new Actor<MachineSnapshot<int>>(restored); string? initialError = null;
        using var initialSubscription = invalidInitial.Subscribe(onError: failure => initialError = ActorErrors.ToException(failure).Message);
        invalidInitial.Start();
        try
        {
            Equal(SnapshotStatus.Error, invalidInitial.GetSnapshot().Status);
            Equal("Initial state node \"[object Object]\" not found on parent state node #(machine)", initialError);
            Results["roundTripInitialError"] = initialError ?? throw new InvalidOperationException("Expected initial error.");
        }
        finally { invalidInitial.Stop(); }
        var nullInput = new StateMachine<int>(MachineDefinitionJson.Parse<int>("""{"invoke":{"src":"reader","input":null}}"""), _ => 0);
        Equal(true, nullInput.Config.Invoke[0].HasInputValue);
        using var nullInputJson = JsonDocument.Parse(MachineDefinitionJson.Serialize(nullInput)); Results["nullInputJson"] = nullInputJson.RootElement.Clone();
    }
    private static void ActionProperties()
    {
        var calls = new List<string>(); var ignoredExec = 0;
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "first", ["params"] = null, ["other"] = "any", ["exec"] = (Action)(() => ignoredExec++) };
        var action = MachineActions.FromProperties<int>(properties); var definition = action.Definition;
        var machine = new StateMachine<int>(new() { On = On<int>(("GO", new() { Actions = [action] })), Entry = [action] }, _ => 7,
            actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal)
            {
                ["first"] = MachineActions.Effect<int>(args => calls.Add("first:" + args.HasParameters + ":" + (args.Parameters ?? "null"))),
                ["second"] = MachineActions.Effect<int>(args => calls.Add("second:" + args.HasParameters + ":" + (args.Parameters ?? "null")))
            });
        Equal(true, ReferenceEquals(properties, definition.Properties));
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try
        {
            properties["type"] = "second"; properties["params"] = (Func<int, MachineEvent, object?>)((context, ev) => context + ev.Type.Length);
            Equal("second", definition.Type); Equal(true, definition.HasParameters); Equal(true, ReferenceEquals(properties["params"], definition.Parameters));
            actor.Send(new("GO"));
            using var document = JsonDocument.Parse(MachineDefinitionJson.Serialize(machine)); Results["actionPropertiesJson"] = document.RootElement.Clone();
            Equal("any", document.RootElement.GetProperty("entry")[0].GetProperty("other").GetString());
            Equal(false, document.RootElement.GetProperty("entry")[0].TryGetProperty("exec", out _));
            Equal(false, document.RootElement.GetProperty("entry")[0].TryGetProperty("params", out _));
            properties.Remove("params"); Equal(false, definition.HasParameters); actor.Send(new("GO"));
            Equal("first:True:null,second:True:9,second:False:null", string.Join(',', calls)); Equal(0, ignoredExec);
            Results["actionPropertiesCalls"] = calls; Results["ignoredExec"] = ignoredExec;
        }
        finally { actor.Stop(); }
    }
    private static void SchemaControls()
    {
        var controls = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in new[] { "missingId", "nestedOrder", "historyKind", "actionType", "extraProperty" })
        {
            var node = JsonNode.Parse(((JsonElement)Results["machine"]).GetRawText())?.AsObject() ?? throw new InvalidOperationException("Missing machine JSON.");
            var states = node["states"]?.AsObject() ?? throw new InvalidOperationException("Missing states.");
            switch (name)
            {
                case "missingId": node.Remove("id"); break;
                case "nestedOrder": (states["foo"] ?? throw new InvalidOperationException("Missing foo."))["order"] = "wrong"; break;
                case "historyKind": (states["testHistory"] ?? throw new InvalidOperationException("Missing history."))["history"] = "wrong"; break;
                case "actionType": (states["testActions"]?["entry"]?[2]?.AsObject() ?? throw new InvalidOperationException("Missing action.")).Remove("type"); break;
                case "extraProperty": node["extra"] = new JsonObject { ["nested"] = 42 }; break;
            }
            using var document = JsonDocument.Parse(node.ToJsonString());
            var valid = Schema.Value.Evaluate(document.RootElement).IsValid; Equal(name == "extraProperty", valid); controls[name] = valid;
        }
        Results["schemaControls"] = controls;
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("action objects keep live properties while executing registry implementation and unresolved params", () => { ActionProperties(); return Task.CompletedTask; }));
        cases.Add(("upstream schema follows nested references and permits additional root properties", () => { SchemaControls(); return Task.CompletedTask; }));
        cases.Add(("JSON parser preserves executable named actions literal invoke inputs outputs and null action failure", () => { ParserBehavior(); return Task.CompletedTask; }));
        cases.Add(("JSON schema differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-json-definition.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
    }
}
