using System.Runtime.CompilerServices;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class PersistenceResourceTests
{
    private sealed record References(IActor? Direct, IActor?[] Array, List<IActor?> List, Dictionary<string, IActor?> Map, Payload Unchanged);
    private sealed record Payload(int Number);
    private static readonly ActorSource Counter = ActorSource.From(new TransitionLogic<int>((s, ev, _) => ev.Type == "INC" ? s + 1 : s, 1));
    private static StateMachine<References> ReferenceMachine() => new(new(), args =>
    {
        var child = args.Spawn(ActorSource.Named("counter"), "child");
        return new(child, [child], [child], new(StringComparer.OrdinalIgnoreCase) { ["Child"] = child }, new(7));
    }, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["counter"] = Counter });
    private static PersistedMachineSnapshot<T> Persist<T>(Actor<MachineSnapshot<T>> actor) => actor.GetPersistedSnapshot() as PersistedMachineSnapshot<T> ?? throw new InvalidOperationException("Wrong persisted snapshot type.");
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("persisted context reconstructs typed record/array/list/dictionary actor references", ReferencesRoundTrip);
        Case("persisted context preserves unchanged object identity and null", Unchanged);
        Case("missing restored child logic clears nested context references", MissingLogic);
        Case("persisted tree does not retain the old actor tree", Capture);
        Case("history IDs revive against the destination machine", History);
        Case("unresolved history IDs are omitted with a warning", MissingHistory);
        Case("restored actor source binds the destination Provide implementation", Provided);
        Case("syncSnapshot remains active after tree restoration", Sync);
        Case("unsafe inline persistence option propagates through nested machines", UnsafeInline);
        Case("restoration does not call context constructors", Constructors);
        Case("reusing persisted context preserves upstream first-restoration references", Reuse);
    }
    private static void Reuse()
    {
        var machine = ReferenceMachine();
        var actor = new Actor<MachineSnapshot<References>>(machine).Start(); var persisted = Persist(actor); actor.Stop();
        var first = new Actor<MachineSnapshot<References>>(machine, options: new() { Snapshot = persisted }).Start();
        Equal(true, ReferenceEquals(persisted.Context.Value, first.GetSnapshot().Context));
        var second = new Actor<MachineSnapshot<References>>(machine, options: new() { Snapshot = persisted }).Start();
        Equal(true, ReferenceEquals(first.GetSnapshot().Children["child"], second.GetSnapshot().Context.Direct));
        Equal(false, ReferenceEquals(second.GetSnapshot().Children["child"], second.GetSnapshot().Context.Direct));
        first.Stop(); second.Stop();
    }
    private static void ReferencesRoundTrip()
    {
        var machine = ReferenceMachine();
        var actor = new Actor<MachineSnapshot<References>>(machine).Start();
        var original = actor.GetSnapshot().Context;
        var persisted = Persist(actor);
        var restored = new Actor<MachineSnapshot<References>>(machine, options: new() { Snapshot = persisted }).Start();
        var context = restored.GetSnapshot().Context;
        var child = restored.GetSnapshot().Children["child"];
        Equal(true, ReferenceEquals(child, context.Direct));
        Equal(true, ReferenceEquals(child, context.Array[0]));
        Equal(true, ReferenceEquals(child, context.List[0]));
        Equal(true, ReferenceEquals(child, context.Map["CHILD"]));
        Equal(false, ReferenceEquals(original.Direct, child));
        Equal(true, ReferenceEquals(original.Unchanged, context.Unchanged));
        Equal(true, ReferenceEquals(original.Direct, original.Map["Child"]));
        actor.Stop(); restored.Stop();
    }
    private static void Unchanged()
    {
        var context = new Payload(42);
        var machine = new StateMachine<Payload>(new(), _ => context);
        var actor = new Actor<MachineSnapshot<Payload>>(machine).Start();
        var persisted = Persist(actor);
        Equal(true, ReferenceEquals(context, persisted.Context.Value));
        var restored = new Actor<MachineSnapshot<Payload>>(machine, options: new() { Snapshot = persisted }).Start();
        Equal(true, ReferenceEquals(context, restored.GetSnapshot().Context));
        actor.Stop(); restored.Stop();
        var nullable = new StateMachine<string?>(new(), _ => null);
        var nullActor = new Actor<MachineSnapshot<string?>>(nullable).Start();
        var nullRestored = new Actor<MachineSnapshot<string?>>(nullable, options: new() { Snapshot = Persist(nullActor) }).Start();
        Equal<string?>(null, nullRestored.GetSnapshot().Context);
        nullActor.Stop(); nullRestored.Stop();
    }
    private static void MissingLogic()
    {
        var machine = ReferenceMachine();
        var actor = new Actor<MachineSnapshot<References>>(machine).Start();
        var persisted = Persist(actor);
        persisted.Children["child"] = persisted.Children["child"] with { Source = ActorSource.Named("missing") };
        var restored = new Actor<MachineSnapshot<References>>(machine, options: new() { Snapshot = persisted }).Start();
        Equal(0, restored.GetSnapshot().Children.Count);
        var context = restored.GetSnapshot().Context;
        Equal<IActor?>(null, context.Direct); Equal<IActor?>(null, context.Array[0]);
        Equal<IActor?>(null, context.List[0]); Equal<IActor?>(null, context.Map["CHILD"]);
        actor.Stop(); restored.Stop();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PersistedMachineSnapshot<References> Snapshot, WeakReference Parent, WeakReference Child) Stopped(StateMachine<References> machine)
    {
        var actor = new Actor<MachineSnapshot<References>>(machine).Start();
        var child = actor.GetSnapshot().Context.Direct;
        var persisted = Persist(actor);
        actor.Stop();
        return (persisted, new(actor), new(child));
    }
    private static void Capture()
    {
        var machine = ReferenceMachine();
        var captured = Stopped(machine);
        for (var i = 0; i < 3 && (captured.Parent.IsAlive || captured.Child.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, captured.Parent.IsAlive); Equal(false, captured.Child.IsAlive);
        var restored = new Actor<MachineSnapshot<References>>(machine, options: new() { Snapshot = captured.Snapshot }).Start();
        Equal(true, restored.GetSnapshot().Context.Direct is not null);
        restored.Stop(); GC.KeepAlive(machine); GC.KeepAlive(captured.Snapshot);
    }
    private static StateMachine<int> HistoryMachine() => new(new()
    {
        Initial = "on", States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
        {
            ["on"] = new()
            {
                Initial = "first", On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["POWER"] = [new() { Target = ["off"] }] },
                States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
                {
                    ["first"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["SWITCH"] = [new() { Target = ["second"] }] } },
                    ["second"] = new(), ["hist"] = new() { History = HistoryKind.Shallow }
                }
            },
            ["off"] = new() { On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) { ["POWER"] = [new() { Target = ["on.hist"] }] } }
        }
    }, _ => 0);
    private static void History()
    {
        var machine = HistoryMachine(); var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        actor.Send(new("SWITCH")); actor.Send(new("POWER")); var persisted = Persist(actor); actor.Stop();
        Equal("(machine).on.second", persisted.HistoryValue["(machine).on.hist"].Single());
        var provided = machine.Provide();
        var restored = new Actor<MachineSnapshot<int>>(provided, options: new() { Snapshot = persisted }).Start();
        restored.Send(new("POWER")); Equal(true, restored.GetSnapshot().Matches("on.second")); restored.Stop();
    }
    private static void MissingHistory()
    {
        var machine = HistoryMachine(); var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        actor.Send(new("POWER")); var persisted = Persist(actor); actor.Stop();
        persisted.HistoryValue["(machine).on.hist"] = ["nonexistent"];
        var original = Console.Error; using var warning = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Console.SetError(warning);
            var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = persisted }).Start();
            Equal(0, Persist(restored).HistoryValue.Count);
            restored.Send(new("POWER")); Equal(true, restored.GetSnapshot().Matches("on.first")); restored.Stop();
        }
        finally { Console.SetError(original); }
        Equal(true, warning.ToString().Contains("Could not resolve StateNode for id: nonexistent", StringComparison.Ordinal));
    }
    private static void Provided()
    {
        var machine = ReferenceMachine(); var actor = new Actor<MachineSnapshot<References>>(machine).Start();
        var persisted = Persist(actor); actor.Stop();
        var replacement = ActorSource.From(new TransitionLogic<int>((s, ev, _) => ev.Type == "INC" ? s + 100 : s, 0));
        var provided = machine.Provide(actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["counter"] = replacement });
        var restored = new Actor<MachineSnapshot<References>>(provided, options: new() { Snapshot = persisted }).Start();
        var child = restored.GetSnapshot().Context.Direct ?? throw new InvalidOperationException("Reference missing.");
        child.Send(new("INC")); Equal(101, ((TransitionSnapshot<int>)child.GetSnapshot()).Context); restored.Stop();
    }
    private static void Sync()
    {
        var machine = new StateMachine<int>(new()
        {
            Invoke = [new() { Id = "child", Source = Counter, OnSnapshot = [new() { Actions = [MachineActions.Assign<int>((count, _) => count + 1)] }] }]
        }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); Equal(1, actor.GetSnapshot().Context);
        var persisted = Persist(actor); actor.Stop(); Equal(true, persisted.Children["child"].SyncSnapshot);
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = persisted }).Start();
        Equal(2, restored.GetSnapshot().Context);
        restored.GetSnapshot().Children["child"]?.Send(new("INC")); Equal(3, restored.GetSnapshot().Context); restored.Stop();
    }
    private static void UnsafeInline()
    {
        var nested = new StateMachine<int>(new(), args => { args.Spawn(Counter, "grandchild"); return 0; });
        var machine = new StateMachine<int>(new(), args => { args.Spawn(nested, "child"); return 0; });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        var persisted = actor.GetPersistedSnapshot(new() { UnsafeAllowInlineActors = true });
        var restored = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = persisted }).Start();
        var child = restored.GetSnapshot().Children["child"]?.GetSnapshot() as MachineSnapshot<int> ?? throw new InvalidOperationException("Nested machine missing.");
        Equal(1, ((TransitionSnapshot<int>)(child.Children["grandchild"]?.GetSnapshot() ?? throw new InvalidOperationException("Grandchild missing."))).Context);
        actor.Stop(); restored.Stop();
    }
    private sealed class Constructed
    {
        internal static int Calls;
        internal IActor Child { get; }
        internal Constructed(IActor child) { Child = child; Calls++; }
    }
    private static void Constructors()
    {
        Constructed.Calls = 0;
        var machine = new StateMachine<Constructed>(new(), args => new(args.Spawn(ActorSource.Named("counter"), "child")),
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["counter"] = Counter });
        var actor = new Actor<MachineSnapshot<Constructed>>(machine).Start(); var persisted = Persist(actor); actor.Stop();
        var restored = new Actor<MachineSnapshot<Constructed>>(machine, options: new() { Snapshot = persisted }).Start();
        Equal(1, Constructed.Calls); Equal(true, ReferenceEquals(restored.GetSnapshot().Context.Child, restored.GetSnapshot().Children["child"])); restored.Stop();
    }
}
