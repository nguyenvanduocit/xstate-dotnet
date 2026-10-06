using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class InitialSnapshotTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        cases.Add(("packages/core/test/input.test.ts::input > should retain the machine snapshot interface when resolving input throws", MissingInputInterface));
        cases.Add(("packages/core/test/state.test.ts::State > .can > should not spawn actors when determining if an event is accepted", CanDoesNotSpawn));
        cases.Add(("packages/core/test/state.test.ts::State > .can > should not execute assignments when used with non-started actor", () => CanDoesNotAssign(false)));
        cases.Add(("packages/core/test/state.test.ts::State > .can > should not execute assignments when used with started actor", () => CanDoesNotAssign(true)));
    }

    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("context initializer failure preserves nested initial value and root metadata", ContextFailure);
        Case("initial assign failure preserves pre-entry context and metadata", EntryFailure);
        Case("initial always guard failure discards partially assigned context and state", AlwaysFailure);
        Case("pure initial transition returns an error snapshot and preceding effects", PureFailure);
        Case("initialization failure retains context-spawned children without starting callbacks", ChildFailure);
        Case("errored initial snapshots can inspect guardless transitions without fabricated context", ErrorCan);
    }
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Value)[] states) =>
        states.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    private static void CanDoesNotSpawn()
    {
        var spawned = false;
        var machine = new StateMachine<int>(new()
        {
            Initial = "a", States = States(("a", new()
            {
                On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
                {
                    ["SPAWN"] = [new() { Actions = [MachineActions.Assign<int>(args =>
                    {
                        args.Spawn(new CallbackLogic(_ => { spawned = true; return null; }));
                        return args.Context;
                    })] }]
                }
            }), ("b", new()))
        }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start();
        _ = actor.GetSnapshot().Can(new("SPAWN"));
        Equal(false, spawned);
        actor.Stop();
    }
    private static void CanDoesNotAssign(bool start)
    {
        var executed = false;
        var machine = new StateMachine<int>(new()
        {
            On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
            {
                ["EVENT"] = [new() { Actions = [MachineActions.Assign<int>(args => { executed = true; return args.Context; })] }]
            }
        }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine);
        if (start) actor.Start();
        Equal(true, actor.GetSnapshot().Can(new("EVENT")));
        Equal(false, executed);
        actor.Stop();
    }
    private static void ErrorCan()
    {
        var effects = 0;
        var machine = new StateMachine<int>(new()
        {
            Initial = "saving", States = States(
                ("saving", new()
                {
                    On = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
                    {
                        ["NEXT"] = [new() { Target = ["done"], Actions = [MachineActions.Effect<int>((_, _) => effects++)] }]
                    }
                }),
                ("done", new() { Kind = StateKind.Final }))
        }, _ => throw new InvalidOperationException("context failed"));
        var snapshot = new Actor<MachineSnapshot<int>>(machine).GetSnapshot();
        Equal(true, snapshot.Can(new("NEXT")));
        Equal(false, snapshot.Can(new("UNKNOWN")));
        Equal(false, snapshot.HasContext);
        Equal(0, effects);
    }
    private static void MissingInputInterface()
    {
        var machine = new StateMachine<string>(new()
        {
            Initial = "saving", States = new Dictionary<string, StateConfig<string>>(StringComparer.Ordinal) { ["saving"] = new() }
        }, args => "Hello, " + (args.Input as string ?? throw new InvalidOperationException("Greeting missing.")));
        var snapshot = new Actor<MachineSnapshot<string>>(machine).GetSnapshot();
        Equal(SnapshotStatus.Error, snapshot.Status);
        Equal(true, snapshot.Matches("saving"));
    }
    private static void ContextFailure()
    {
        var failure = new InvalidOperationException("context failed");
        var entries = 0;
        var machine = new StateMachine<int>(new()
        {
            Id = "root", Tags = ["root"], Meta = "root meta", Kind = StateKind.Parallel,
            Entry = [MachineActions.Effect<int>((_, _) => entries++)],
            States = States(
                ("left", new()
                {
                    Initial = "saving", States = States(("saving", new() { Tags = ["saving"], Meta = "saving meta" }))
                }),
                ("right", new()
                {
                    Initial = "idle", States = States(("idle", new()))
                }))
        }, _ => throw failure);
        var actor = new Actor<MachineSnapshot<int>>(machine);
        var snapshot = actor.GetSnapshot();
        Equal(SnapshotStatus.Error, snapshot.Status);
        Equal(true, ReferenceEquals(failure, snapshot.Failure));
        Equal(true, snapshot.Matches(StateValue.Parse("{\"left\":\"saving\",\"right\":\"idle\"}")));
        Equal(true, snapshot.HasTag("root"));
        Equal(false, snapshot.HasTag("saving"));
        Equal(1, snapshot.GetMeta().Count);
        Equal<object?>("root meta", snapshot.GetMeta()["root"]);
        Equal(false, snapshot.HasContext);
        var errors = new List<Exception>();
        actor.Subscribe(onError: failure => errors.Add(ActorTaskTests.RequireException(failure)));
        actor.Start();
        Equal(0, entries);
        Equal(1, errors.Count);
        Equal(true, ReferenceEquals(failure, errors[0]));
    }
    private static void EntryFailure()
    {
        var machine = new StateMachine<int>(new()
        {
            Id = "root", Tags = ["root"], Initial = "a",
            States = States(("a", new()
            {
                Tags = ["child"], Entry = [
                    MachineActions.Assign<int>((_, _) => 42),
                    MachineActions.Assign<int>((_, _) => throw new InvalidOperationException("assignment failed"))]
            }))
        }, _ => 7);
        var snapshot = new Actor<MachineSnapshot<int>>(machine).GetSnapshot();
        Equal(SnapshotStatus.Error, snapshot.Status);
        Equal(7, snapshot.Context);
        Equal(true, snapshot.Matches("a"));
        Equal(true, snapshot.HasTag("root"));
        Equal(false, snapshot.HasTag("child"));
    }
    private static void AlwaysFailure()
    {
        var machine = new StateMachine<int>(new()
        {
            Initial = "a", States = States(
                ("a", new() { Always = [new() { Target = ["b"], Actions = [MachineActions.Assign<int>((_, _) => 42)] }] }),
                ("b", new() { Always = [new() { Guard = MachineGuards.Predicate<int>((_, _) => throw new InvalidOperationException("guard failed")) }] }))
        }, _ => 7);
        var snapshot = new Actor<MachineSnapshot<int>>(machine).GetSnapshot();
        Equal(SnapshotStatus.Error, snapshot.Status);
        Equal(7, snapshot.Context);
        Equal(true, snapshot.Matches("a"));
        Equal("guard failed", ActorTaskTests.RequireException(snapshot.Failure).Message);
    }
    private static void PureFailure()
    {
        var effects = 0;
        var machine = new StateMachine<int>(new()
        {
            Initial = "a", States = States(("a", new() { Entry = [
                MachineActions.Effect<int>((_, _) => effects++),
                MachineActions.Assign<int>((_, _) => throw new InvalidOperationException("resolve failed")),
                MachineActions.Effect<int>((_, _) => effects++)] }))
        }, _ => 7);
        var initial = ActorTransitions.Initial(machine);
        Equal(SnapshotStatus.Error, initial.Snapshot.Status);
        Equal(true, initial.Snapshot.Matches("a"));
        Equal(7, initial.Snapshot.Context);
        Equal(1, initial.Actions.Count);
        Equal(0, effects);
    }
    private static void ChildFailure()
    {
        var started = 0;
        var child = new CallbackLogic(_ => { started++; return null; });
        var machine = new StateMachine<int>(new()
        {
            Entry = [MachineActions.Assign<int>((_, _) => throw new InvalidOperationException("entry failed"))]
        }, args => { args.Spawn(child, id: "context-child"); return 7; });
        var actor = new Actor<MachineSnapshot<int>>(machine);
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal(7, actor.GetSnapshot().Context);
        Equal(1, actor.GetSnapshot().Children.Count);
        Equal(true, actor.GetSnapshot().Children.ContainsKey("context-child"));
        actor.Subscribe(onError: _ => { });
        actor.Start();
        Equal(0, started);
    }
}
