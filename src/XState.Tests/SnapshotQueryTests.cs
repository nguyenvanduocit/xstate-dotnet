using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;

namespace XStatePort.Tests;

internal static class SnapshotQueryTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    private sealed record Counter(int Count);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Add(string title, Action run) => cases.Add(("packages/core/test/state.test.ts::State > " + title, run));
        Add("status > should show that a machine has not reached its final state", () => Status(false));
        Add("status > should show that a machine has reached its final state", () => Status(true));
        string[] titles = ["an event object that results in a new action", "an event object that results in a context change", "a reentering self-transition with reentry action", "a reentering self-transition with transition action", "a targetless transition with actions", "a forbidden transition"];
        for (var i = 0; i < titles.Length; i++)
        {
            var kind = i;
            Add(".can > should return " + (kind == 5 ? "false" : "true") + " for " + titles[i], () => Can(kind));
        }
        Add(".hasTag > should be able to check a tag after recreating a persisted state", RestoredTag);
    }
    private static StateMachine<int> ExampleMachine() => new(new()
    {
        Initial = "one", On = On<int>(("MACHINE_EVENT", new() { Target = [".two"] })),
        States = States<int>(
            ("one", new() { Entry = [MachineActions.Named<int>("enter")], On = On<int>(
                ("EXTERNAL", new() { Target = ["one"], Reenter = true }), ("INERT", new()),
                ("INTERNAL", new() { Actions = [MachineActions.Named<int>("doSomething")] }),
                ("TO_TWO", new() { Target = ["two"] }),
                ("TO_TWO_MAYBE", new() { Target = ["two"], Guard = MachineGuards.Predicate<int>((_, _) => true) }),
                ("TO_THREE", new() { Target = ["three"] }), ("FORBIDDEN_EVENT", new()), ("TO_FINAL", new() { Target = ["success"] })) }),
            ("two", new() { Initial = "deep", On = On<int>(("DEEP_EVENT", new() { Target = ["."] })), States = States<int>(
                ("deep", new() { Initial = "foo", States = States<int>(
                    ("foo", new() { On = On<int>(("FOO_EVENT", new() { Target = ["bar"] }), ("FORBIDDEN_EVENT", new())) }),
                    ("bar", new() { On = On<int>(("BAR_EVENT", new() { Target = ["foo"] })) })) })) }),
            ("three", new() { Kind = StateKind.Parallel, On = On<int>(("THREE_EVENT", new() { Target = ["."] })), States = States<int>(
                ("first", new() { Initial = "p31", States = States<int>(("p31", new() { On = On<int>(("P31", new() { Target = ["."] })) })) }),
                ("guarded", new() { Initial = "p32", States = States<int>(("p32", new() { On = On<int>(("P32", new() { Target = ["."] })) })) })) }),
            ("success", new() { Kind = StateKind.Final }))
    }, _ => 0);
    private static void Status(bool done)
    {
        var actor = new Actor<MachineSnapshot<int>>(ExampleMachine());
        try
        {
            if (done) { actor.Start(); actor.Send(new("TO_FINAL")); }
            var snapshot = actor.GetSnapshot();
            Equal(done ? SnapshotStatus.Done : SnapshotStatus.Active, snapshot.Status);
            Results[done ? "done" : "active"] = new { status = snapshot.Status.ToString().ToLowerInvariant(), value = JsonSerializer.Deserialize<JsonElement>(snapshot.Value.ToJson()) };
        }
        finally { actor.Stop(); }
    }
    private static void Can(int kind)
    {
        var calls = 0;
        var effect = MachineActions.Effect<Counter>((_, _) => calls++);
        var transition = kind switch
        {
            0 => new TransitionConfig<Counter> { Actions = [MachineActions.Named<Counter>("newAction")] },
            1 => new() { Actions = [MachineActions.Assign<Counter>((_, _) => new(1))] },
            2 => new() { Target = ["a"] },
            3 => new() { Target = ["a"], Actions = [effect] },
            4 => new() { Actions = [effect] },
            _ => new()
        };
        var eventType = kind < 2 ? "NEXT" : "EV";
        var machine = new StateMachine<Counter>(new() { Initial = "a", States = States<Counter>(("a", new()
        { Entry = kind == 2 ? [effect] : [], On = On<Counter>((eventType, transition)) })) }, _ => new(0));
        var actor = new Actor<MachineSnapshot<Counter>>(machine);
        try
        {
            var snapshot = actor.GetSnapshot(); var can = snapshot.Can(new(eventType));
            Equal(kind != 5, can); Equal(0, calls); Equal(0, snapshot.Context.Count);
            Results["can:" + kind] = new { can, calls, count = snapshot.Context.Count, value = JsonSerializer.Deserialize<JsonElement>(snapshot.Value.ToJson()) };
        }
        finally { actor.Stop(); }
    }
    private static void RestoredTag()
    {
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { Tags = ["foo"] })) }, _ => 0);
        var source = new Actor<MachineSnapshot<int>>(machine).Start();
        var persisted = source.GetPersistedSnapshot(); source.Stop();
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = persisted });
        try
        {
            var snapshot = actor.GetSnapshot(); Equal(true, snapshot.HasTag("foo"));
            Results["restoredTag"] = new { hasTag = snapshot.HasTag("foo"), tags = snapshot.Tags.ToArray(), value = JsonSerializer.Deserialize<JsonElement>(snapshot.Value.ToJson()) };
        }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("snapshot query differential observations", () =>
    {
        File.WriteAllText("tmp/xstate-parity/csharp-snapshot-queries.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask;
    }));
}
