using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;

namespace XStatePort.Tests;

internal static class HistoryTransitionTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    private sealed record Counts(int Source, int Direct, int Deep);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void History(string title, Action run) => cases.Add(("packages/core/test/history.test.ts::history states > " + title, run));
        History("should reenter persisted state during reentering transition targeting a history state", Reenter);
        foreach (var visited in new[] { false, true })
        {
            var suffix = visited ? "was already visited" : "was never visited yet";
            History("should execute actions of the initial transition when a history state without a default target is targeted and its parent state " + suffix, () => HistoryActions(0, visited));
            History("should not execute actions of the initial transition when a history state with a default target is targeted and its parent state " + suffix, () => HistoryActions(1, visited));
            History("should execute entry actions of a parent of the targeted history state when its parent state " + suffix, () => HistoryActions(2, visited));
        }
        History("should execute actions of the initial transition when it select a history state as the initial state of its parent", InitialHistory);
        History("should invoke an actor when reentering the stored configuration through the history state", RestartInvocation);
        cases.Add(("packages/core/test/history.test.ts::revive history states > should not re-resolve already-instantiated StateNode", Revive));
        void Internal(string title, Action run) => cases.Add(("packages/core/test/internalTransitions.test.ts::internal transitions > " + title, run));
        foreach (var parent in new[] { false, true })
            foreach (var array in new[] { false, true })
                Internal("should work " + (parent ? "on parent " : "") + "with targetless transitions (in " + (array ? "conditional array" : "object") + ")", () => Targetless(parent, array));
        Internal("should maintain the child state when targetless transition is handled by parent", KeepChild);
        Internal("should reenter proper descendants of a source state of an internal transition", () => Descendants(false));
        Internal("should exit proper descendants of a source state of an internal transition", () => Descendants(true));
    }
    private static object Snapshot<T>(MachineSnapshot<T> snapshot) => new
    {
        value = JsonSerializer.Deserialize<JsonElement>(snapshot.Value.ToJson()),
        history = snapshot.HistoryValue.ToDictionary(pair => pair.Key, pair => pair.Value.Select(node => node.Id).ToArray(), StringComparer.Ordinal)
    };
    private static Actor<MachineSnapshot<int>> Start(StateConfig<int> config) => new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0)).Start();
    private static void Reenter()
    {
        var actual = new List<string>();
        var actor = Start(new() { Initial = "a", States = States<int>(("a", new()
        {
            Initial = "a1", On = On<int>(("REENTER", new() { Target = ["#b_hist"], Reenter = true })),
            States = States<int>(("a1", new() { On = On<int>(("NEXT", new() { Target = ["a2"] })) }),
                ("a2", new() { Entry = [MachineActions.Effect<int>((_, _) => actual.Add("a2 entered"))], Exit = [MachineActions.Effect<int>((_, _) => actual.Add("a2 exited"))] }),
                ("a3", new() { Kind = StateKind.History, Id = "b_hist" }))
        })) });
        try
        {
            actor.Send(new("NEXT")); actual.Clear(); actor.Send(new("REENTER"));
            Equal("a2 exited,a2 entered", string.Join(',', actual));
            Results["reenter"] = new { snapshot = Snapshot(actor.GetSnapshot()), actions = actual.ToArray() };
        }
        finally { actor.Stop(); }
    }
    private static void HistoryActions(int kind, bool visited)
    {
        var calls = 0; var action = MachineActions.Effect<int>((_, _) => calls++);
        var children = States<int>(("b1", new()), ("b2", new() { Id = "hist", Kind = StateKind.History, HistoryTarget = kind == 0 ? [] : ["b3"] }));
        if (kind != 0) children.Add("b3", new());
        var actor = Start(new() { Initial = "a", States = States<int>(
            ("a", new() { On = On<int>(("NEXT", new() { Target = ["#hist"] })) }),
            ("b", new() { Initial = "b1", InitialActions = kind == 2 ? [] : [action], Entry = kind == 2 ? [action] : [], States = children,
                On = visited ? On<int>(("NEXT", new() { Target = ["a"] })) : new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal) })) });
        try
        {
            actor.Send(new("NEXT"));
            if (visited) { calls = 0; actor.Send(new("NEXT")); actor.Send(new("NEXT")); }
            Equal(kind == 2 || (kind == 0 && !visited) ? 1 : 0, calls);
            Results[$"historyActions:{kind}:{visited}"] = new { calls, snapshot = Snapshot(actor.GetSnapshot()) };
        }
        finally { actor.Stop(); }
    }
    private static void InitialHistory()
    {
        var calls = 0;
        var actor = Start(new() { Initial = "a", States = States<int>(
            ("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { Initial = "b1", InitialActions = [MachineActions.Effect<int>((_, _) => calls++)], States = States<int>(
                ("b1", new() { Id = "hist", Kind = StateKind.History, HistoryTarget = ["b2"] }), ("b2", new())) })) });
        try { actor.Send(new("NEXT")); Equal(1, calls); Results["initialHistory"] = new { calls, snapshot = Snapshot(actor.GetSnapshot()) }; }
        finally { actor.Stop(); }
    }
    private static void RestartInvocation()
    {
        var calls = 0;
        var callback = new CallbackLogic(_ => { calls++; return null; });
        var actor = Start(new() { Initial = "running", States = States<int>(
            ("running", new() { On = On<int>(("PING", new() { Target = ["refresh"] })), Invoke = [new() { Source = ActorSource.From(callback) }] }),
            ("refresh", new() { Kind = StateKind.History })) });
        try { calls = 0; actor.Send(new("PING")); Equal(1, calls); Results["restartInvocation"] = new { calls, snapshot = Snapshot(actor.GetSnapshot()) }; }
        finally { actor.Stop(); }
    }
    private static void Revive()
    {
        var machine = new StateMachine<int>(new() { Initial = "on", States = States<int>(
            ("on", new() { Initial = "first", On = On<int>(("POWER", new() { Target = ["off"] })), States = States<int>(
                ("first", new() { On = On<int>(("SWITCH", new() { Target = ["second"] })) }), ("second", new()), ("hist", new() { Kind = StateKind.History })) }),
            ("off", new() { On = On<int>(("POWER", new() { Target = ["on.hist"] })) })) }, _ => 0);
        var source = new Actor<MachineSnapshot<int>>(machine).Start();
        source.Send(new("SWITCH")); source.Send(new("POWER")); var snapshot = source.GetSnapshot(); source.Stop();
        Equal("off", snapshot.Value.AtomicValue);
        var node = snapshot.HistoryValue["(machine).on.hist"][0]; Equal(typeof(StateNode<int>), node.GetType());
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Snapshot = snapshot }).Start();
        try
        {
            actor.Send(new("POWER")); Equal(true, actor.GetSnapshot().Matches("on.second"));
            Results["revive"] = new { before = Snapshot(snapshot), after = Snapshot(actor.GetSnapshot()), sameNode = ReferenceEquals(node, actor.GetSnapshot().HistoryValue["(machine).on.hist"][0]) };
        }
        finally { actor.Stop(); }
    }
    private static void Targetless(bool parent, bool array)
    {
        var calls = 0; var eventType = array ? "TARGETLESS_ARRAY" : "TARGETLESS_OBJECT";
        // Both TS object and conditional-array forms normalize to this typed transition list.
        var on = On<int>((eventType, new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] }));
        var empty = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal);
        var actor = Start(new() { Initial = "foo", On = parent ? on : empty, States = States<int>(("foo", new() { On = parent ? empty : on })) });
        try { actor.Send(new(eventType)); Equal(1, calls); Results[$"targetless:{parent}:{array}"] = new { calls, snapshot = Snapshot(actor.GetSnapshot()) }; }
        finally { actor.Stop(); }
    }
    private static void KeepChild()
    {
        var actor = Start(new() { Initial = "foo", On = On<int>(("PARENT_EVENT", new() { Actions = [MachineActions.Effect<int>((_, _) => { })] })), States = States<int>(("foo", new())) });
        try { actor.Send(new("PARENT_EVENT")); Equal("foo", actor.GetSnapshot().Value.AtomicValue); Results["keepChild"] = Snapshot(actor.GetSnapshot()); }
        finally { actor.Stop(); }
    }
    private static void Descendants(bool exit)
    {
        MachineAction<Counts> Increment(int depth) => MachineActions.Assign<Counts>((context, _) => depth switch
        {
            0 => context with { Source = context.Source + 1 }, 1 => context with { Direct = context.Direct + 1 }, _ => context with { Deep = context.Deep + 1 }
        });
        StateConfig<Counts> Node(int depth) => new()
        {
            Entry = exit ? [] : [Increment(depth)], Exit = exit ? [Increment(depth)] : [],
            Initial = depth < 2 ? (depth == 0 ? "a11" : "a111") : null,
            States = depth < 2 ? States((depth == 0 ? "a11" : "a111", Node(depth + 1))) : new Dictionary<string, StateConfig<Counts>>(StringComparer.Ordinal),
            On = depth == 0 ? On<Counts>(("REENTER", new() { Target = [".a11.a111"] })) : new Dictionary<string, IReadOnlyList<TransitionConfig<Counts>>>(StringComparer.Ordinal)
        };
        var machine = new StateMachine<Counts>(new() { Initial = "a1", States = States(("a1", Node(0))) }, _ => new(0, 0, 0));
        var actor = new Actor<MachineSnapshot<Counts>>(machine).Start();
        try
        {
            actor.Send(new("REENTER")); var context = actor.GetSnapshot().Context;
            Equal(exit ? new Counts(0, 1, 1) : new Counts(1, 2, 2), context);
            Results[exit ? "descendantExits" : "descendantEntries"] = new { source = context.Source, direct = context.Direct, deep = context.Deep, snapshot = Snapshot(actor.GetSnapshot()) };
        }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("history/internal transition differential observations", () =>
    {
        File.WriteAllText("tmp/xstate-parity/csharp-history-transitions.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask;
    }));
}
