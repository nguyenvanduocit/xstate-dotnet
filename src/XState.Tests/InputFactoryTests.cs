using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class InputFactoryTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Action run) => cases.Add(("packages/core/test/input.test.ts::input > " + name, () => { run(); return Task.CompletedTask; }));
        Case("should be a type error if input is not expected yet provided", NoInput);
        Case("should provide a static inline input to the referenced actor", () => Named(false));
        Case("should provide a dynamic inline input to the referenced actor", () => Named(true));
        Case("should call the input factory with self when invoking", () => Self(false));
        Case("should call the input factory with self when spawning", () => Self(true));
    }
    private static void NoInput()
    {
        // Upstream's body only asserts start does not throw; its title is not a compiler assertion.
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new(), _ => 42)).Start();
        Results["noInput"] = actor.GetSnapshot().Context; actor.Stop();
    }
    private static void Named(bool dynamic)
    {
        var inputs = new List<object?>(); var child = new StateMachine<int>(new(), args => { inputs.Add(args.Input); return 0; });
        var machine = new StateMachine<int>(new() { Invoke = [new() { Source = ActorSource.Named("child"), Input = dynamic ? args => args.Context + 100 : _ => 42 }] },
            args => dynamic ? (int)(args.Input ?? throw new InvalidOperationException("Input missing.")) : 0,
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child"] = ActorSource.From(child) });
        var actor = new Actor<MachineSnapshot<int>>(machine, dynamic ? 42 : null).Start();
        try { Equal(1, inputs.Count); Equal<object?>(dynamic ? 142 : 42, inputs[0]); Results[dynamic ? "dynamic" : "static"] = inputs; }
        finally { actor.Stop(); }
    }
    private static void Self(bool spawn)
    {
        var seen = new List<IActor>(); var child = new StateMachine<int>(new(), _ => 0);
        object? Input(MachineActionArgs<int> args) { seen.Add(args.Self); return null; }
        var config = spawn ? new StateConfig<int> { Entry = [MachineActions.SpawnChild<int>(ActorSource.Named("child"), input: Input)] } : new StateConfig<int> { Invoke = [new() { Source = ActorSource.From(child), Input = Input }] };
        var machine = new StateMachine<int>(config, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child"] = ActorSource.From(child) });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        try { Equal(1, seen.Count); Equal(true, ReferenceEquals(actor, seen[0])); Results[spawn ? "spawnSelf" : "invokeSelf"] = new { calls = seen.Count, sameSelf = ReferenceEquals(actor, seen[0]) }; }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("input factory differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-input-factory.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
}
