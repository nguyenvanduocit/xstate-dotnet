using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;
internal static class MachineConstructionTests
{
    internal static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    private sealed record Data(string Value);
    private sealed record Context(Data Foo);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string group, string title, Action run) => cases.Add(("packages/core/test/machine.test.ts::machine > " + (group.Length == 0 ? "" : group + " > ") + title, run));
        Case("machine.provide", "should override an action", () => Override(false));
        Case("machine.provide", "should override a guard", () => Override(true));
        Case("machine.provide", "should not override context if not defined", ContextPreserved);
        Case("machine.provide", "should throw if initial state is missing in a compound state", MissingInitial);
        Case("machine.provide", "should lazily create context for all interpreter instances created from the same machine template created by `provide`", () => Lazy(true));
        Case("machine function context", "context from a function should be lazily evaluated", () => Lazy(false));
        Case("versioning", "should allow a version to be specified", Version);
        Case("id", "should represent the ID", () => Id(0));
        Case("id", "should represent the ID (state node)", () => Id(1));
        Case("id", "should use the key as the ID if no ID is provided (state node)", () => Id(2));
        Case("combinatorial machines", "should support combinatorial machines (single-state)", Combinatorial);
        Case("", "should pass through schemas", Schemas);
        cases.Add(("packages/core/test/machine.test.ts::StateNode > should list transitions", Transitions));
    }
    private static void Override(bool guard)
    {
        var original = 0; var overridden = 0;
        var machine = guard ? new StateMachine<int>(new() { On = On<int>(("EVENT", new() { Guard = MachineGuards.Named<int>("someCondition"), Actions = [MachineActions.Effect<int>((_, _) => { })] })) }, _ => 0,
            guards: new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal) { ["someCondition"] = MachineGuards.Predicate<int>((_, _) => { original++; return true; }) })
            : new StateMachine<int>(new() { Entry = [MachineActions.Named<int>("entryAction")] }, _ => 0,
                actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["entryAction"] = MachineActions.Effect<int>((_, _) => original++) });
        var changed = guard ? machine.Provide(guards: new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal) { ["someCondition"] = MachineGuards.Predicate<int>((_, _) => { overridden++; return true; }) })
            : machine.Provide(actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal) { ["entryAction"] = MachineActions.Effect<int>((_, _) => overridden++) });
        var actor = new Actor<MachineSnapshot<int>>(changed).Start();
        try { if (guard) actor.Send(new("EVENT")); Equal(0, original); Equal(1, overridden); Results["override:" + guard] = new { original, overridden }; }
        finally { actor.Stop(); }
    }
    private static void ContextPreserved()
    {
        var machine = new StateMachine<string>(new(), _ => "bar").Provide();
        var actor = new Actor<MachineSnapshot<string>>(machine).Start();
        try { Equal("bar", actor.GetSnapshot().Context); Results["contextPreserved"] = new { foo = actor.GetSnapshot().Context }; }
        finally { actor.Stop(); }
    }
    private static void MissingInitial()
    {
        try
        {
            _ = new StateMachine<int>(new() { Initial = "first", States = States<int>(("first", new() { States = States<int>(("second", new()), ("third", new())) })) }, _ => 0);
            throw new InvalidOperationException("Compound state without initial was accepted.");
        }
        catch (ArgumentException) { Results["missingInitial"] = true; }
    }
    private static void Lazy(bool provide)
    {
        var config = provide ? new StateConfig<Context>() : new() { Initial = "active", States = States<Context>(("active", new())) };
        var source = new StateMachine<Context>(config, _ => new(new("baz")));
        var aMachine = provide ? source.Provide() : source;
        var bMachine = provide ? aMachine : new StateMachine<Context>(config, _ => new(new("baz")));
        var a = new Actor<MachineSnapshot<Context>>(aMachine); var b = new Actor<MachineSnapshot<Context>>(bMachine);
        try
        {
            if (provide) { a.Start(); b.Start(); }
            if (provide) Equal(false, ReferenceEquals(a.GetSnapshot().Context.Foo, b.GetSnapshot().Context.Foo));
            else { Equal(false, ReferenceEquals(a.GetSnapshot().Context, b.GetSnapshot().Context)); Equal(new Context(new("baz")), a.GetSnapshot().Context); Equal(new Context(new("baz")), b.GetSnapshot().Context); }
            Results["lazy:" + provide] = new { same = provide ? ReferenceEquals(a.GetSnapshot().Context.Foo, b.GetSnapshot().Context.Foo) : ReferenceEquals(a.GetSnapshot().Context, b.GetSnapshot().Context), first = a.GetSnapshot().Context.Foo.Value, second = b.GetSnapshot().Context.Foo.Value };
        }
        finally { a.Stop(); b.Stop(); }
    }
    private static void Version()
    {
        var machine = new StateMachine<int>(new() { Id = "version", Version = "1.0.4" }, _ => 0);
        Equal("1.0.4", machine.Version); Results["version"] = machine.Version ?? throw new InvalidOperationException("Version missing.");
    }
    private static void Id(int kind)
    {
        var machine = new StateMachine<int>(new() { Id = "some-id", Initial = "idle", States = States<int>(("idle", new() { Id = kind == 1 ? "idle" : null })) }, _ => 0);
        var value = kind == 0 ? machine.Id : machine.States["idle"].Id; Equal(kind == 0 ? "some-id" : kind == 1 ? "idle" : "some-id.idle", value); Results["id:" + kind] = value;
    }
    private static void Combinatorial()
    {
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { On = On<int>(("INC", new() { Actions = [MachineActions.Assign<int>((context, _) => context + 1)] })) }, _ => 42));
        try { Equal("{}", actor.GetSnapshot().Value.ToJson()); actor.Start().Send(new("INC")); Equal(43, actor.GetSnapshot().Context); Results["combinatorial"] = actor.GetSnapshot().Context; }
        finally { actor.Stop(); }
    }
    private static void Schemas()
    {
        var schemas = new { context = new { count = new { type = "number" } } };
        var machine = new MachineSetup<int>(schemas: schemas).CreateMachine(new(), _ => 0);
        Equal(JsonSerializer.Serialize(schemas), JsonSerializer.Serialize(machine.Schemas)); Results["schemas"] = machine.Schemas ?? throw new InvalidOperationException("Schemas missing.");
    }
    private static void Transitions()
    {
        var greenOn = On<int>(("TIMER", new() { Target = ["yellow"] }), ("POWER_OUTAGE", new() { Target = ["red"] }));
        greenOn["FORBIDDEN_EVENT"] = [new()];
        var machine = new StateMachine<int>(new() { Initial = "green", States = States<int>(
            ("green", new() { On = greenOn }),
            ("yellow", new() { On = On<int>(("TIMER", new() { Target = ["red"] }), ("POWER_OUTAGE", new() { Target = ["red"] })) }),
            ("red", new() { Initial = "walk", On = On<int>(("TIMER", new() { Target = ["green"] }), ("POWER_OUTAGE", new() { Target = ["red"] })), States = States<int>(
                ("walk", new() { On = On<int>(("PED_COUNTDOWN", new() { Target = ["wait"] })) }), ("wait", new() { On = On<int>(("PED_COUNTDOWN", new() { Target = ["stop"] })) }), ("stop", new())) })) }, _ => 0);
        var keys = machine.States["green"].Transitions.Keys.ToArray(); Equal("TIMER,POWER_OUTAGE,FORBIDDEN_EVENT", string.Join(',', keys)); Results["transitions"] = keys;
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("machine construction differential observations export", () =>
    {
        File.WriteAllText("tmp/xstate-parity/csharp-machine-construction.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask;
    }));
}
