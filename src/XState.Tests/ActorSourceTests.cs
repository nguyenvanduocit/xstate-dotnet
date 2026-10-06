using System.Runtime.CompilerServices;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ActorSourceTests
{
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("root actor source identifies its original logic", Root);
        Case("inline invokes use config index and state-node ID rather than actor ID", InvokeNames);
        Case("spawn source metadata preserves inline identity, named identity and syncSnapshot", SpawnSources);
        Case("named actor sources resolve the provided implementation", Provided);
        Case("reserved invoke source names resolve config logic before implementation registry", Reserved);
        Case("compiled invoke source cache does not retain stopped actors", Capture);
        Case("noncanonical array invoke indices do not resolve a child source", Noncanonical);
    }
    private static void Root()
    {
        var logic = new TransitionLogic<int>((context, _, _) => context, 7);
        var actor = new Actor<TransitionSnapshot<int>>(logic);
        Equal<object?>(logic, actor.Source.Logic);
        Equal<string?>(null, actor.Source.Name);
        Equal(true, ReferenceEquals(actor.Source, actor.Source));
        Equal(false, actor.SyncSnapshot);
    }
    private static StateMachine<int> Invoker(ActorSource first, ActorSource second) => new(new()
    {
        Id = "machine", Initial = "active", States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
        {
            ["active"] = new()
            {
                Id = "active-node", Invoke = [new() { Id = "custom-id", Source = first }, new() { Source = second, OnSnapshot = [] }]
            }
        }
    }, _ => 0);
    private static void InvokeNames()
    {
        var source = ActorSource.From(new TransitionLogic<int>((context, _, _) => context, 7));
        var actor = new Actor<MachineSnapshot<int>>(Invoker(source, source)).Start();
        var children = actor.GetSnapshot().Children;
        var first = children["custom-id"] ?? throw new InvalidOperationException("First child missing.");
        var second = children["1.active-node"] ?? throw new InvalidOperationException("Second child missing.");
        Equal("xstate.invoke.0.active-node", first.Source.Name);
        Equal("xstate.invoke.1.active-node", second.Source.Name);
        Equal(false, first.SyncSnapshot);
        Equal(true, second.SyncSnapshot);
        Equal(7, ((TransitionSnapshot<int>)first.GetSnapshot()).Context);
        actor.Stop();
    }
    private static void SpawnSources()
    {
        var inline = ActorSource.From(new TransitionLogic<int>((context, _, _) => context, 7));
        var named = ActorSource.Named("worker");
        var machine = new StateMachine<int>(new()
        {
            Entry = [MachineActions.Assign<int>(args =>
            {
                args.Spawn(inline, "inline", syncSnapshot: true);
                args.Spawn(named, "named");
                return args.Context;
            }), MachineActions.SpawnChild<int>(inline, "action-inline"), MachineActions.SpawnChild<int>(named, "action-named")]
        }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["worker"] = inline });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        foreach (var key in new[] { "inline", "action-inline" }) Equal(inline, actor.GetSnapshot().Children[key]?.Source);
        foreach (var key in new[] { "named", "action-named" }) Equal(named, actor.GetSnapshot().Children[key]?.Source);
        Equal(true, actor.GetSnapshot().Children["inline"]?.SyncSnapshot);
        actor.Stop();
    }
    private static void Provided()
    {
        var first = ActorSource.From(new TransitionLogic<int>((context, _, _) => context, 1));
        var second = ActorSource.From(new TransitionLogic<int>((context, _, _) => context, 2));
        var machine = new StateMachine<int>(new() { Invoke = [new() { Id = "child", Source = ActorSource.Named("worker") }] }, _ => 0,
            actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["worker"] = first });
        var original = new Actor<MachineSnapshot<int>>(machine).Start();
        var provided = new Actor<MachineSnapshot<int>>(machine.Provide(actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["worker"] = second })).Start();
        Equal("worker", original.GetSnapshot().Children["child"]?.Source.Name);
        Equal("worker", provided.GetSnapshot().Children["child"]?.Source.Name);
        Equal(1, ((TransitionSnapshot<int>)(original.GetSnapshot().Children["child"]?.GetSnapshot() ?? throw new InvalidOperationException("Child missing."))).Context);
        Equal(2, ((TransitionSnapshot<int>)(provided.GetSnapshot().Children["child"]?.GetSnapshot() ?? throw new InvalidOperationException("Child missing."))).Context);
        original.Stop(); provided.Stop();
    }
    private static void Reserved()
    {
        var inline = ActorSource.From(new TransitionLogic<int>((context, _, _) => context, 7));
        var overrideLogic = ActorSource.From(new TransitionLogic<int>((context, _, _) => context, 99));
        var machine = new StateMachine<int>(new()
        {
            Id = "source", Invoke = [new() { Id = "original", Source = inline }],
            Entry = [MachineActions.SpawnChild<int>(ActorSource.Named("xstate.invoke.0.source"), "copy")]
        }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["xstate.invoke.0.source"] = overrideLogic });
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        Equal(7, ((TransitionSnapshot<int>)(actor.GetSnapshot().Children["copy"]?.GetSnapshot() ?? throw new InvalidOperationException("Copy missing."))).Context);
        actor.Stop();
    }
    private static void Noncanonical()
    {
        var inline = ActorSource.From(new TransitionLogic<int>((context, _, _) => context, 7));
        var machine = new StateMachine<int>(new()
        {
            Id = "source", Invoke = [new() { Id = "original", Source = inline }],
            Entry = [MachineActions.SpawnChild<int>(ActorSource.Named("xstate.invoke.00.source"), "copy")]
        }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine);
        Exception? seen = null;
        actor.Subscribe(onError: error => seen = ActorTaskTests.RequireException(error));
        actor.Start();
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal("Cannot read properties of undefined (reading 'src')", seen?.Message);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Root, WeakReference Child) Stopped(StateMachine<int> machine)
    {
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        var child = actor.GetSnapshot().Children["custom-id"] ?? throw new InvalidOperationException("Child missing.");
        actor.Stop();
        return (new(actor), new(child));
    }
    private static void Capture()
    {
        var source = ActorSource.From(new TransitionLogic<int>((context, _, _) => context, 7));
        var machine = Invoker(source, source);
        var weak = Stopped(machine);
        for (var i = 0; i < 3 && (weak.Root.IsAlive || weak.Child.IsAlive); i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, weak.Root.IsAlive);
        Equal(false, weak.Child.IsAlive);
        GC.KeepAlive(machine);
    }
}
