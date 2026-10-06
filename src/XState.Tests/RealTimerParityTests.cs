using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class RealTimerParityTests
{
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string file, string group, string title, Func<Task> run) => cases.Add(("packages/core/test/" + file + ".test.ts::" + group + " > " + title, run));
        Case("actions", "raise", "should be able to send a delayed event to itself", DelayedSelf);
        Case("actions", "raise", "should be able to send a delayed event to itself with delay = 0", ZeroDelay);
        Case("actions", "raise", "should be able to raise a delayed event and respond to it in the same state", SameState);
        Case("actions", "cancel", "should be possible to cancel a raised delayed event", CancelRaised);
        Case("actions", "cancel", "should cancel only the delayed event in the machine that scheduled it when canceling the event with the same ID in the machine that sent it first", () => Ownership(true));
        Case("actions", "cancel", "should cancel only the delayed event in the machine that scheduled it when canceling the event with the same ID in the machine that sent it second", () => Ownership(false));
        Case("actions", "cancel", "should not try to clear an undefined timeout when canceling an unscheduled timer", CancelMissing);
        Case("actions", "cancel", "should be able to cancel a just scheduled delayed event to a just invoked child", () => EntryCancel(true));
        Case("actions", "cancel", "should not be able to cancel a just scheduled non-delayed event to a just invoked child", () => EntryCancel(false));
        Case("after", "delayed transitions", "should not try to clear an undefined timeout when exiting source state of a delayed transition", AfterClear);
        Case("after", "delayed transitions", "should be able to transition with delay from nested initial state", NestedAfter);
        Case("after", "delayed transitions", "parent state should enter child state without re-entering self (relative target)", RelativeAfter);
        Case("interpreter", "interpreter > send with delay", "can send an event after a delay", BeforeStart);
        Case("interpreter", "interpreter", "can cancel a delayed event using expression to resolve send id", CancelExpression);
    }
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Key, TransitionConfig<int> Value)[] values) =>
        values.ToDictionary(x => x.Key, x => (IReadOnlyList<TransitionConfig<int>>)new[] { x.Value }, StringComparer.Ordinal);
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> Value)[] values) =>
        values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    private static MachineAction<int> Raise(string type, double delay, string? id = null) =>
        MachineActions.Raise<int>((_, _) => new(type), new() { Delay = MachineDelays.From<int>(delay), Id = id });
    private static StateMachine<int> Machine(StateConfig<int> config) => new(config, _ => 0);
    private static Actor<MachineSnapshot<int>> Actor(StateMachine<int> machine, IClock? clock = null) => new(machine, options: new() { Clock = clock });
    // Like timers/promises in upstream, this sleep uses the same real timer queue as actor timers.
    private static Task Sleep(double milliseconds)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new RealClock().SetTimeout(() => completion.TrySetResult(), milliseconds);
        return completion.Task;
    }
    private static async Task DelayedSelf()
    {
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(("a", new() { Entry = [Raise("EVENT", 1)], On = On(("TO_B", new() { Target = ["b"] })) }),
                ("b", new() { On = On(("EVENT", new() { Target = ["c"] })) }), ("c", new() { Kind = StateKind.Final }))
        }));
        var completion = ActorTasks.ToPromiseAsync(actor);
        ActorRuntime.Run(() => { actor.Start(); actor.Send(new("TO_B")); });
        await completion.ConfigureAwait(false);
        Equal(SnapshotStatus.Done, actor.GetSnapshot().Status);
    }
    private static async Task ZeroDelay()
    {
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(("a", new() { Entry = [Raise("EVENT", 0)], On = On(("EVENT", new() { Target = ["b"] })) }), ("b", new()))
        }));
        ActorRuntime.Run(() => { actor.Start(); Equal("a", actor.GetSnapshot().Value.AtomicValue); });
        await Sleep(0).ConfigureAwait(false);
        Equal("b", actor.GetSnapshot().Value.AtomicValue);
        actor.Stop();
    }
    private static async Task SameState()
    {
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(("a", new() { Entry = [Raise("TO_B", 100)], On = On(("TO_B", new() { Target = ["b"] })) }), ("b", new() { Kind = StateKind.Final }))
        }));
        var completion = ActorTasks.ToPromiseAsync(actor);
        actor.Start();
        await Sleep(50).ConfigureAwait(false);
        Equal("a", actor.GetSnapshot().Value.AtomicValue);
        await completion.ConfigureAwait(false);
    }
    private static async Task CancelRaised()
    {
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(("a", new() { On = On(
                ("NEXT", new() { Actions = [Raise("RAISED", 1, "myId")] }), ("RAISED", new() { Target = ["b"] }),
                ("CANCEL", new() { Actions = [MachineActions.Cancel<int>("myId")] })) }), ("b", new()))
        })).Start();
        ActorRuntime.Run(() => { actor.Send(new("NEXT")); actor.Send(new("CANCEL")); });
        await Sleep(10).ConfigureAwait(false);
        Equal("a", actor.GetSnapshot().Value.AtomicValue);
        actor.Stop();
    }
    private static async Task Ownership(bool first)
    {
        var calls = new int[2];
        StateMachine<int> Child(int index) => Machine(new()
        {
            Id = index == 0 ? "foo" : "bar", Entry = [Raise("event", 100, "sameId")],
            On = index == (first ? 0 : 1) ? On(("event", new() { Actions = [MachineActions.Effect<int>((_, _) => calls[index]++)] }),
                ("cancel", new() { Actions = [MachineActions.Cancel<int>("sameId")] })) :
                On(("event", new() { Actions = [MachineActions.Effect<int>((_, _) => calls[index]++)] }))
        });
        var childId = first ? "foo" : "bar";
        var eventType = first ? "cancelFoo" : "cancelBar";
        var actor = Actor(Machine(new()
        {
            Invoke = [new() { Id = "foo", Source = ActorSource.From(Child(0)) }, new() { Id = "bar", Source = ActorSource.From(Child(1)) }],
            On = On((eventType, new() { Actions = [MachineActions.SendTo<int>(childId, _ => new("cancel"))] }))
        })).Start();
        await Sleep(50).ConfigureAwait(false);
        actor.Send(new(eventType));
        await Sleep(55).ConfigureAwait(false);
        Equal(first ? 0 : 1, calls[0]);
        Equal(first ? 1 : 0, calls[1]);
        actor.Stop();
    }
    private sealed class TrackingClock : IClock
    {
        private readonly RealClock inner = new();
        public int Clears { get; private set; }
        public double Now => inner.Now;
        public long SetTimeout(Action callback, double timeout) => inner.SetTimeout(callback, timeout);
        public void ClearTimeout(long id) { Clears++; inner.ClearTimeout(id); }
    }
    private static Task CancelMissing()
    {
        var clock = new TrackingClock();
        var actor = Actor(Machine(new() { On = On(("FOO", new() { Actions = [MachineActions.Cancel<int>("foo")] })) }), clock).Start();
        actor.Send(new("FOO"));
        Equal(0, clock.Clears);
        actor.Stop();
        return Task.CompletedTask;
    }
    private static async Task EntryCancel(bool delayed)
    {
        var calls = 0;
        var child = Machine(new() { On = On(("PING", new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] })) });
        var machine = new StateMachine<int>(new()
        {
            Initial = "a", States = States(("a", new() { On = On(("START", new() { Target = ["b"] })) }), ("b", new()
            {
                Entry = [MachineActions.SendTo<int>("myChild", _ => new("PING"), new() { Id = "myEvent", Delay = delayed ? MachineDelays.From<int>(0) : null }), MachineActions.Cancel<int>("myEvent")],
                Invoke = [new() { Id = "myChild", Source = ActorSource.Named("child") }]
            }))
        }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child"] = ActorSource.From(child) });
        var actor = Actor(machine).Start();
        actor.Send(new("START"));
        if (delayed) await Sleep(10).ConfigureAwait(false);
        Equal(delayed ? 0 : 1, calls);
        actor.Stop();
    }
    private static async Task AfterClear()
    {
        var clock = new TrackingClock();
        var actor = Actor(Machine(new()
        {
            Initial = "green", States = States(("green", new() { After = On(("1", new() { Target = ["yellow"] })) }), ("yellow", new()))
        }), clock).Start();
        await Sleep(5).ConfigureAwait(false);
        Equal("yellow", actor.GetSnapshot().Value.AtomicValue);
        Equal(0, clock.Clears);
        actor.Stop();
    }
    private static async Task NestedAfter()
    {
        var actor = Actor(Machine(new()
        {
            Initial = "nested", States = States(("nested", new() { Initial = "wait", States = States(("wait", new() { After = On(("10", new() { Target = ["#end"] })) })) }),
                ("end", new() { Id = "end", Kind = StateKind.Final }))
        }));
        var completion = ActorTasks.ToPromiseAsync(actor);
        actor.Start();
        await completion.ConfigureAwait(false);
        Equal(SnapshotStatus.Done, actor.GetSnapshot().Status);
    }
    private static async Task RelativeAfter()
    {
        var seen = new List<string>();
        var actor = Actor(Machine(new()
        {
            Initial = "one", States = States(("one", new()
            {
                Initial = "two", Entry = [MachineActions.Effect<int>((_, _) => seen.Add("entered one"))],
                States = States(("two", new() { Entry = [MachineActions.Effect<int>((_, _) => seen.Add("entered two"))] }),
                    ("three", new() { Entry = [MachineActions.Effect<int>((_, _) => seen.Add("entered three"))], Always = [new() { Target = ["#end"] }] })),
                After = On(("10", new() { Target = [".three"] }))
            }), ("end", new() { Id = "end", Kind = StateKind.Final }))
        }));
        var completion = ActorTasks.ToPromiseAsync(actor);
        actor.Start();
        await completion.ConfigureAwait(false);
        Equal("entered one,entered two,entered three", string.Join(',', seen));
    }
    private static Task BeforeStart()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new RealClock();
        var actor = Actor(Machine(new()
        {
            Initial = "foo", States = States(("foo", new() { Entry = [Raise("TIMER", 10)], On = On(("TIMER", new() { Target = ["bar"] })) }), ("bar", new()))
        }), clock);
        // Translate each JS await continuation into a timer turn. A ThreadPool Task continuation
        // can resume after later timers; JS runs this continuation before the next timer callback.
        void After(double delay, Action turn) => clock.SetTimeout(() =>
        {
            try { turn(); }
            catch (Exception error) { actor.Stop(); completion.TrySetException(error); }
        }, delay);
        ActorRuntime.Run(() =>
        {
            Equal("foo", actor.GetSnapshot().Value.AtomicValue);
            After(10, () =>
            {
                Equal("foo", actor.GetSnapshot().Value.AtomicValue);
                actor.Start();
                Equal("foo", actor.GetSnapshot().Value.AtomicValue);
                After(5, () =>
                {
                    Equal("foo", actor.GetSnapshot().Value.AtomicValue);
                    After(10, () =>
                    {
                        Equal("bar", actor.GetSnapshot().Value.AtomicValue);
                        actor.Stop();
                        completion.TrySetResult();
                    });
                });
            });
        });
        return completion.Task;
    }
    private static async Task CancelExpression()
    {
        var actor = Actor(Machine(new()
        {
            Initial = "first", States = States(("first", new()
            {
                Entry = [Raise("FOO", 100, "foo"), Raise("BAR", 200), MachineActions.Cancel<int>(_ => "foo")],
                On = On(("FOO", new() { Target = ["fail"] }), ("BAR", new() { Target = ["pass"] }))
            }), ("fail", new() { Kind = StateKind.Final }), ("pass", new() { Kind = StateKind.Final }))
        }));
        var completion = ActorTasks.ToPromiseAsync(actor);
        actor.Start();
        await completion.ConfigureAwait(false);
        Equal("pass", actor.GetSnapshot().Value.AtomicValue);
    }
}
