using System.Runtime.CompilerServices;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class SchedulerTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string file, string group, string title, Action run) => cases.Add(("packages/core/test/" + file + ".test.ts::" + group + " > " + title, run));
        Case("clock", "clock", "system clock should be default clock for actors (invoked from machine)", InheritedClock);
        Case("after", "delayed transitions", "should transition after delay", TrafficLight);
        Case("after", "delayed transitions", "should defer a single send event for a delayed conditional transition (#886)", ConditionalAfter);
        Case("after", "delayed transitions > delay expressions", "should evaluate the expression (function) to determine the delay", () => NamedDelay(false));
        Case("after", "delayed transitions > delay expressions", "should evaluate the expression (string) to determine the delay", () => NamedDelay(true));
        Case("interpreter", "interpreter > send with delay", "can send an event after a delay (expression)", () => Expression(false));
        Case("interpreter", "interpreter > send with delay", "can send an event after a delay (expression using _event)", () => Expression(true));
        Case("interpreter", "interpreter > send with delay", "can send an event after a delay (delayed transitions)", Letter);
        Case("interpreter", "interpreter", "can cancel a delayed event", CancelTrafficLight);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("zero delay is external and starts only when the actor starts", ZeroDelay);
        Case("scheduler cancellation is scoped to sending actor", CancellationOwnership);
        Case("cancel does not clear a missing or fired timeout", MissingTimer);
        Case("stop cancels pending timers and releases their payloads", StopReleases);
        Case("done and error cancel scheduled events", TerminalCancellation);
        Case("after timers cancel on exit and restart on reentry", Reentry);
        Case("scheduler snapshot is copied and removed before event delivery", Snapshot);
        Case("delayed target remains bound to old child when its ID is reused", BoundTarget);
        Case("send resolves event then delay then target using params", ResolveOrder);
        Case("cancel after entry delayed send binds invoked child and prevents delivery", () => EntryCancel(true));
        Case("cancel cannot retract a non-delayed send in the same entry batch", () => EntryCancel(false));
        Case("duplicate scheduled IDs preserve upstream overwrite behavior", DuplicateId);
        Case("enqueue cancel and raise retain ordering and dynamic cancel params", EnqueuedCancellation);
        Case("pure delayed action calculations do not create timers", Pure);
        Case("missing named delay raises internally and Provide delays are isolated", MissingAndProvided);
        Case("after numeric keys use JavaScript coercion and event formatting", NumericKeys);
        Case("after canonical integer keys precede named delays", KeyOrdering);
        Case("after merges canonicalized event collisions with existing transitions", EventCollision);
    }
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Key, TransitionConfig<int> Transition)[] values) =>
        values.ToDictionary(x => x.Key, x => (IReadOnlyList<TransitionConfig<int>>)new[] { x.Transition }, StringComparer.Ordinal);
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> State)[] values) =>
        values.ToDictionary(x => x.Key, x => x.State, StringComparer.Ordinal);
    private static MachineAction<int> Raise(string type, double delay, string? id = null) => MachineActions.Raise<int>((_, _) => new(type), new() { Id = id, Delay = MachineDelays.From<int>(delay) });
    private static StateMachine<int> Machine(StateConfig<int> config) => new(config, _ => 0);
    private static Actor<MachineSnapshot<int>> Actor(StateMachine<int> machine, IClock clock) => new(machine, options: new() { Clock = clock, ErrorReporter = new FailureReporter() });
    private sealed class FailureReporter : IUnhandledErrorReporter
    {
        public void Report(object? failure) => throw new InvalidOperationException("Unexpected scheduler error.", ActorErrors.ToException(failure));
    }
    private static void InheritedClock()
    {
        var clock = new SimulatedClock();
        var child = Machine(new() { Initial = "a", States = States(("a", new() { After = On(("10000", new() { Target = ["b"] })) }), ("b", new())) });
        var actor = Actor(Machine(new() { Invoke = [new() { Id = "child", Source = ActorSource.From(child) }] }), clock).Start();
        var childActor = actor.GetSnapshot().Children["child"] as Actor<MachineSnapshot<int>> ?? throw new InvalidOperationException("Missing child.");
        Equal("a", childActor.GetSnapshot().Value.AtomicValue);
        Equal(clock, childActor.Clock);
        clock.Increment(10000);
        Equal("b", childActor.GetSnapshot().Value.AtomicValue);
        actor.Stop();
    }
    private static StateMachine<int> Light(bool explicitRaise) => Machine(new()
    {
        Id = "light", Initial = "green", States = States(
            ("green", explicitRaise ? new()
            {
                Entry = [Raise("TIMER", 10, "TIMER1")],
                On = On(("TIMER", new() { Target = ["yellow"] }), ("KEEP_GOING", new() { Actions = [MachineActions.Cancel<int>("TIMER1")] }))
            } : new() { After = On(("1000", new() { Target = ["yellow"] })) }),
            ("yellow", explicitRaise ? new() { Entry = [Raise("TIMER", 10)], On = On(("TIMER", new() { Target = ["red"] })) } :
                new() { After = On(("1000", new() { Target = ["red"] })) }),
            ("red", new() { After = On((explicitRaise ? "10" : "1000", new() { Target = ["green"] })) }))
    });
    private static void TrafficLight()
    {
        var clock = new SimulatedClock();
        var actor = Actor(Light(false), clock).Start();
        Equal(true, actor.GetSnapshot().Matches("green"));
        clock.Increment(500);
        Equal(true, actor.GetSnapshot().Matches("green"));
        clock.Increment(510);
        Equal(true, actor.GetSnapshot().Matches("yellow"));
        actor.Stop();
    }
    private static void ConditionalAfter()
    {
        var calls = 0;
        var clock = new SimulatedClock();
        var machine = Machine(new()
        {
            Initial = "X", States = States(("X", new()
            {
                After = new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
                { ["1"] = [new() { Target = ["Y"], Guard = MachineGuards.Predicate<int>((_, _) => true) }, new() { Target = ["Z"] }] }
            }), ("Y", new() { On = On(("*", new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] })) }), ("Z", new()))
        });
        var actor = Actor(machine, clock).Start();
        clock.Increment(10);
        Equal(0, calls);
        Equal(true, actor.GetSnapshot().Matches("Y"));
        actor.Stop();
    }
    private static void NamedDelay(bool fromEvent)
    {
        var seen = new List<object?>();
        var key = fromEvent ? "someDelay" : "myDelay";
        var config = new StateConfig<int>
        {
            Initial = "inactive", States = States(
                ("inactive", fromEvent ? new() { On = On(("ACTIVATE", new() { Target = ["active"] })) } : new() { After = On((key, new() { Target = ["active"] })) }),
                ("active", fromEvent ? new() { After = On((key, new() { Target = ["inactive"] })) } : new()))
        };
        var machine = new StateMachine<int>(config, _ => 500, delays: new Dictionary<string, MachineDelay<int>>(StringComparer.Ordinal)
        { [key] = MachineDelays.From<int>(args =>
        {
            seen.Add(fromEvent ? args.Event : args.Context);
            return fromEvent ? args.Event.Payload as int? : args.Context;
        }) });
        var clock = new SimulatedClock();
        var actor = Actor(machine, clock).Start();
        var ev = new MachineEvent("ACTIVATE", 500);
        if (fromEvent) actor.Send(ev);
        Equal(1, seen.Count);
        Equal<object?>(fromEvent ? ev : 500, seen[0]);
        Equal(true, actor.GetSnapshot().Matches(fromEvent ? "active" : "inactive"));
        clock.Increment(300);
        Equal(true, actor.GetSnapshot().Matches(fromEvent ? "active" : "inactive"));
        clock.Increment(200);
        Equal(true, actor.GetSnapshot().Matches(fromEvent ? "inactive" : "active"));
        actor.Stop();
    }
    private static void Expression(bool assertEvent)
    {
        var machine = new StateMachine<int>(new()
        {
            Id = "delayExpr", Initial = "idle", States = States(
                ("idle", new() { On = On(("ACTIVATE", new() { Target = ["pending"] })) }),
                ("pending", new()
                {
                    Entry = [MachineActions.Raise<int>((_, _) => new("FINISH"), new() { Delay = MachineDelays.From<int>(args =>
                    {
                        if (assertEvent) Equal("ACTIVATE", args.Event.Type);
                        return args.Context + (args.Event.Payload is int wait ? wait : 0);
                    }) })], On = On(("FINISH", new() { Target = ["finished"] }))
                }), ("finished", new() { Kind = StateKind.Final }))
        }, _ => 100);
        var clock = new SimulatedClock();
        var stopped = false;
        var actor = Actor(machine, clock);
        actor.Subscribe(onComplete: () => stopped = true);
        actor.Start();
        actor.Send(new("ACTIVATE", 50));
        clock.Increment(101);
        Equal(false, stopped);
        clock.Increment(50);
        Equal(true, stopped);
    }
    private static void Letter()
    {
        var machine = new StateMachine<int>(new()
        {
            Id = "letter", Initial = "a", States = States(
                ("a", new() { After = On(("delayA", new() { Target = ["b"] })) }),
                ("b", new() { After = On(("someDelay", new() { Target = ["c"] })) }),
                ("c", new() { Entry = [MachineActions.Raise<int>((_, _) => new("FIRE_DELAY", 200), new() { Delay = MachineDelays.From<int>(20) })], On = On(("FIRE_DELAY", new() { Target = ["d"] })) }),
                ("d", new() { After = On(("delayD", new() { Target = ["e"] })) }),
                ("e", new() { After = On(("someDelay", new() { Target = ["f"] })) }),
                ("f", new() { Kind = StateKind.Final }))
        }, _ => 100, delays: new Dictionary<string, MachineDelay<int>>(StringComparer.Ordinal)
        {
            ["someDelay"] = MachineDelays.From<int>(args => args.Context + 50),
            ["delayA"] = MachineDelays.From<int>(args => args.Context),
            ["delayD"] = MachineDelays.From<int>(args => args.Context + (args.Event.Payload is int amount ? amount : throw new InvalidOperationException("FIRE_DELAY data missing.")))
        });
        var clock = new SimulatedClock();
        var done = false;
        var actor = Actor(machine, clock);
        actor.Subscribe(onComplete: () => done = true);
        actor.Start();
        Equal("a", actor.GetSnapshot().Value.AtomicValue);
        foreach (var (delay, state) in new[] { (100, "b"), (150, "c"), (20, "d"), (300, "e"), (150, "f") })
        {
            clock.Increment(delay);
            Equal(state, actor.GetSnapshot().Value.AtomicValue);
        }
        Equal(true, done);
    }
    private static void CancelTrafficLight()
    {
        var actor = Actor(Light(true), new SimulatedClock());
        var clock = actor.Clock as SimulatedClock ?? throw new InvalidOperationException("Clock missing.");
        actor.Start();
        clock.Increment(5);
        actor.Send(new("KEEP_GOING"));
        Equal(true, actor.GetSnapshot().Matches("green"));
        clock.Increment(10);
        Equal(true, actor.GetSnapshot().Matches("green"));
        actor.Stop();
    }
    private static void ZeroDelay()
    {
        var clock = new SimulatedClock();
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(("a", new() { Entry = [Raise("GO", 0)], On = On(("GO", new() { Target = ["b"] })) }), ("b", new()))
        }), clock);
        Equal(0, clock.PendingCount);
        clock.Increment(100);
        Equal(true, actor.GetSnapshot().Matches("a"));
        actor.Start();
        Equal(true, actor.GetSnapshot().Matches("a"));
        Equal(1, clock.PendingCount);
        clock.Increment(0);
        Equal(true, actor.GetSnapshot().Matches("b"));
        actor.Stop();
    }
    private static void CancellationOwnership()
    {
        foreach (var cancelFirst in new[] { true, false })
        {
            var calls = new int[2];
            StateMachine<int> Child(int index) => Machine(new()
            {
                Entry = [Raise("event", 100, "sameId")],
                On = On(("event", new() { Actions = [MachineActions.Effect<int>((_, _) => calls[index]++)] }), ("cancel", new() { Actions = [MachineActions.Cancel<int>("sameId")] }))
            });
            var clock = new SimulatedClock();
            var actor = Actor(Machine(new() { Invoke = [new() { Id = "foo", Source = ActorSource.From(Child(0)) }, new() { Id = "bar", Source = ActorSource.From(Child(1)) }] }), clock).Start();
            clock.Increment(50);
            actor.GetSnapshot().Children[cancelFirst ? "foo" : "bar"]?.Send(new("cancel"));
            clock.Increment(55);
            Equal(cancelFirst ? 0 : 1, calls[0]);
            Equal(cancelFirst ? 1 : 0, calls[1]);
            actor.Stop();
        }
    }
    private sealed class TrackingClock : IClock
    {
        public SimulatedClock Inner { get; } = new();
        public int Clears { get; private set; }
        public double Now => Inner.Now;
        public long SetTimeout(Action callback, double timeout) => Inner.SetTimeout(callback, timeout);
        public void ClearTimeout(long id) { Clears++; Inner.ClearTimeout(id); }
    }
    private static void MissingTimer()
    {
        var clock = new TrackingClock();
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(("a", new() { After = On(("1", new() { Target = ["b"] })) }), ("b", new())),
            On = On(("CANCEL", new() { Actions = [MachineActions.Cancel<int>("missing")] }))
        }), clock).Start();
        actor.Send(new("CANCEL"));
        Equal(0, clock.Clears);
        clock.Inner.Increment(5);
        Equal(true, actor.GetSnapshot().Matches("b"));
        Equal(0, clock.Clears);
        actor.Stop();
        Equal(0, clock.Clears);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SchedulePayload(Actor<MachineSnapshot<int>> actor)
    {
        var payload = new byte[4096];
        actor.System.Scheduler.Schedule(actor, actor, new("value", payload), 100, "payload");
        return new(payload);
    }
    private static void StopReleases()
    {
        var clock = new SimulatedClock();
        var actor = Actor(Machine(new()), clock).Start();
        var weak = SchedulePayload(actor);
        Equal(1, clock.PendingCount);
        actor.Stop();
        Equal(0, clock.PendingCount);
        Equal(0, actor.System.GetSnapshot().ScheduledEvents.Count);
        for (var i = 0; i < 3 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, weak.IsAlive);
        GC.KeepAlive(actor);
    }
    private static void TerminalCancellation()
    {
        foreach (var fail in new[] { false, true })
        {
            var clock = new SimulatedClock();
            var actor = Actor(Machine(new()
            {
                Initial = "a", States = States(("a", new()
                {
                    Entry = [Raise("LATER", 100)],
                    On = On(("GO", fail ? new() { Actions = [MachineActions.Effect<int>((_, _) => throw new InvalidOperationException("failed"))] } : new() { Target = ["done"] }))
                }), ("done", new() { Kind = StateKind.Final }))
            }), clock);
            actor.Subscribe(onError: error => Equal("failed", ActorTaskTests.RequireException(error).Message));
            actor.Start();
            actor.Send(new("GO"));
            Equal(fail ? SnapshotStatus.Error : SnapshotStatus.Done, actor.GetSnapshot().Status);
            Equal(0, clock.PendingCount);
            Equal(0, actor.System.GetSnapshot().ScheduledEvents.Count);
        }
    }
    private static void Reentry()
    {
        var clock = new SimulatedClock();
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(("a", new()
            {
                After = On(("10", new() { Target = ["b"] })), On = On(("RESET", new() { Target = ["a"], Reenter = true }))
            }), ("b", new()))
        }), clock).Start();
        clock.Increment(5);
        actor.Send(new("RESET"));
        Equal(1, clock.PendingCount);
        clock.Increment(5);
        Equal(true, actor.GetSnapshot().Matches("a"));
        clock.Increment(5);
        Equal(true, actor.GetSnapshot().Matches("b"));
        actor.Stop();
    }
    private static void Snapshot()
    {
        var clock = new SimulatedClock();
        var countAtDelivery = -1;
        var actor = Actor(Machine(new() { On = On(("GO", new() { Actions = [MachineActions.Effect<int>(args => countAtDelivery = args.System.GetSnapshot().ScheduledEvents.Count)] })) }), clock).Start();
        actor.System.Scheduler.Schedule(actor, actor, new("GO"), 10, "event");
        var before = actor.System.GetSnapshot();
        var entry = before.ScheduledEvents.Single();
        Equal(actor.SessionId + ".event", entry.Key);
        Equal(actor, entry.Value.Source);
        Equal(actor, entry.Value.Target);
        Equal(10.0, entry.Value.Delay);
        Equal("GO", entry.Value.Event.Type);
        clock.Increment(10);
        Equal(0, countAtDelivery);
        Equal(1, before.ScheduledEvents.Count);
        Equal(0, actor.System.GetSnapshot().ScheduledEvents.Count);
        actor.Stop();
    }
    private static void BoundTarget()
    {
        var calls = new int[2];
        StateMachine<int> Child(int index) => Machine(new() { On = On(("PING", new() { Actions = [MachineActions.Effect<int>((_, _) => calls[index]++)] })) });
        var clock = new SimulatedClock();
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(("a", new()
            {
                Invoke = [new() { Id = "child", Source = ActorSource.From(Child(0)) }],
                Entry = [MachineActions.SendTo<int>("child", _ => new("PING"), new() { Delay = MachineDelays.From<int>(10) })],
                On = On(("NEXT", new() { Target = ["b"] }))
            }), ("b", new() { Invoke = [new() { Id = "child", Source = ActorSource.From(Child(1)) }] }))
        }), clock).Start();
        actor.Send(new("NEXT"));
        clock.Increment(10);
        Equal(0, calls[0]);
        Equal(0, calls[1]);
        actor.Stop();
    }
    private static void ResolveOrder()
    {
        var order = new List<string>();
        var clock = new SimulatedClock();
        var machine = Machine(new() { Entry = [MachineActions.Named<int>("send", 7)] }).Provide(actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal)
        {
            ["send"] = MachineActions.SendTo<int>(args => { order.Add("target"); Equal<object?>(7, args.Parameters); return args.Self; }, args =>
            { order.Add("event"); Equal<object?>(7, args.Parameters); return new("GO"); }, new()
            { Delay = MachineDelays.From<int>(args => { order.Add("delay"); Equal<object?>(7, args.Parameters); return 10; }) })
        });
        var actor = Actor(machine, clock).Start();
        Equal("event,delay,target", string.Join(',', order));
        actor.Stop();
    }
    private static void EntryCancel(bool delayed)
    {
        var calls = 0;
        var child = Machine(new() { On = On(("PING", new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] })) });
        var clock = new SimulatedClock();
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(("a", new() { On = On(("START", new() { Target = ["b"] })) }), ("b", new()
            {
                Entry = [MachineActions.SendTo<int>("child", _ => new("PING"), new() { Id = "event", Delay = delayed ? MachineDelays.From<int>(0) : null }), MachineActions.Cancel<int>("event")],
                Invoke = [new() { Id = "child", Source = ActorSource.From(child) }]
            }))
        }), clock).Start();
        actor.Send(new("START"));
        clock.Increment(10);
        Equal(delayed ? 0 : 1, calls);
        actor.Stop();
    }
    private static void DuplicateId()
    {
        var seen = new List<string>();
        var clock = new SimulatedClock();
        var actor = Actor(Machine(new() { On = On(("*", new() { Actions = [MachineActions.Effect<int>((_, ev) => seen.Add(ev.Type))] })) }), clock).Start();
        actor.System.Scheduler.Schedule(actor, actor, new("old"), 10, "same");
        actor.System.Scheduler.Schedule(actor, actor, new("new"), 20, "same");
        Equal(1, actor.System.GetSnapshot().ScheduledEvents.Count);
        Equal(2, clock.PendingCount);
        actor.System.Scheduler.Cancel(actor, "same");
        Equal(1, clock.PendingCount);
        clock.Increment(10);
        Equal("old", string.Join(',', seen));
        actor.Stop();
    }
    private static void EnqueuedCancellation()
    {
        var clock = new SimulatedClock();
        var actor = Actor(Machine(new()
        {
            Entry = [MachineActions.EnqueueActions<int>(args =>
            {
                args.Enqueue.Raise(new MachineEvent("LATER"), new() { Id = "later", Delay = MachineDelays.From<int>(10) });
                args.Enqueue.Add("cancel", "later");
            })]
        }).Provide(actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal)
        { ["cancel"] = MachineActions.Cancel<int>(args => args.Parameters as string ?? throw new InvalidOperationException("Missing cancel ID.")) }), clock).Start();
        Equal(0, clock.PendingCount);
        actor.Stop();
    }
    private static void Pure()
    {
        var result = ActorTransitions.Initial(Machine(new() { Entry = [Raise("LATER", 10, "later"), MachineActions.Cancel<int>("later")] }));
        Equal(SnapshotStatus.Active, result.Snapshot.Status);
        Equal(2, result.Actions.Count);
    }
    private static void NumericKeys()
    {
        foreach (var (key, expected, delay) in new (string, string, double)[]
        {
            ("01", "1", 1), ("0x10", "16", 16), ("0b11", "3", 3), ("0o10", "8", 8),
            ("", "0", 0), ("-0", "0", -0.0), ("1e3", "1000", 1000), ("1e20", "100000000000000000000", 1e20),
            ("1e21", "1e+21", 1e21), ("1e-6", "0.000001", 1e-6), ("1e-7", "1e-7", 1e-7), ("+Infinity", "Infinity", double.PositiveInfinity)
        })
        {
            var clock = new SimulatedClock();
            var actor = Actor(Machine(new() { Id = "numeric", After = On((key, new())) }), clock).Start();
            var entry = actor.System.GetSnapshot().ScheduledEvents.Single().Value;
            Equal("xstate.after." + expected + ".numeric", entry.Id);
            Equal(delay, entry.Delay);
            Equal(BitConverter.DoubleToInt64Bits(delay), BitConverter.DoubleToInt64Bits(entry.Delay));
            actor.Stop();
        }
        var namedClock = new SimulatedClock();
        var named = Actor(Machine(new() { Id = "named", After = On(("NaN", new())) }).Provide(delays:
            new Dictionary<string, MachineDelay<int>>(StringComparer.Ordinal) { ["NaN"] = MachineDelays.From<int>(9) }), namedClock).Start();
        Equal(9.0, named.System.GetSnapshot().ScheduledEvents.Single().Value.Delay);
        named.Stop();
    }
    private static void KeyOrdering()
    {
        var clock = new SimulatedClock();
        var actor = Actor(Machine(new() { Id = "keys", After = On(("named", new()), ("20", new()), ("10", new())) })
            .Provide(delays: new Dictionary<string, MachineDelay<int>>(StringComparer.Ordinal) { ["named"] = MachineDelays.From<int>(30) }), clock).Start();
        Equal("xstate.after.10.keys,xstate.after.20.keys,xstate.after.named.keys", string.Join(',', actor.System.GetSnapshot().ScheduledEvents.Values.Select(entry => entry.Id)));
        actor.Stop();
    }
    private static void EventCollision()
    {
        var seen = new List<int>();
        var clock = new SimulatedClock();
        var actor = Actor(Machine(new()
        {
            Id = "collision", On = On(("xstate.after.1.collision", new() { Actions = [MachineActions.Effect<int>((_, _) => seen.Add(0))] })),
            After = On(("01", new() { Actions = [MachineActions.Effect<int>((_, _) => seen.Add(2))] }), ("1", new() { Actions = [MachineActions.Effect<int>((_, _) => seen.Add(1))] }))
        }), clock).Start();
        clock.Increment(1);
        Equal("0,0", string.Join(',', seen));
        actor.Stop();
    }
    private static void MissingAndProvided()
    {
        var config = new StateConfig<int> { Initial = "a", States = States(("a", new() { After = On(("wait", new() { Target = ["b"] })) }), ("b", new())) };
        var original = Machine(config);
        var provided = original.Provide(delays: new Dictionary<string, MachineDelay<int>>(StringComparer.Ordinal) { ["wait"] = MachineDelays.From<int>(10) });
        var clock = new SimulatedClock();
        var first = Actor(original, clock).Start();
        Equal(true, first.GetSnapshot().Matches("b"));
        var second = Actor(provided, clock).Start();
        Equal(true, second.GetSnapshot().Matches("a"));
        clock.Increment(10);
        Equal(true, second.GetSnapshot().Matches("b"));
        first.Stop();
        second.Stop();
    }
}
