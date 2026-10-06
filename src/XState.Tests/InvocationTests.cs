using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;
internal static class InvocationTests
{
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Invoke(string title, Action run) => cases.Add(("packages/core/test/invoke.test.ts::invoke > " + title, run));
        void SystemCase(string title, Action run) => cases.Add(("packages/core/test/system.test.ts::system > " + title, run));
        Invoke("child can immediately respond to the parent with multiple events", ImmediateResponses);
        Invoke("should start services (machine as invoke config)", () => StartsService(false));
        Invoke("should start deeply nested service (machine as invoke config)", () => StartsService(true));
        Invoke("should get reinstantiated after reentering the invoking state in a microstep", MicrostepReentry);
        Invoke("invocations should be stopped when the machine reaches done state", () => CompletionStops(false));
        Invoke("deep invocations should be stopped when the machine reaches done state", () => CompletionStops(true));
        Invoke("should be able to restart an invoke when reentering the invoking state", RestartOrder);
        cases.Add(("packages/core/test/invoke.test.ts::invoke input > should provide self to input mapper", InputSelf));
        Invoke("with machines > should create invocations from machines in nested states", NestedPing);
        Invoke("with callbacks > should transition correctly if transient transition happens before current state invokes callback function and sends an event", TransientCallback);
        void Predictable(string title, Action run) => cases.Add(("packages/core/test/predictableExec.test.ts::predictableExec > " + title, run));
        Predictable("should be possible to send immediate events to initially invoked actors", InitialSend);
        Predictable("should be possible to send immediate events to initially invoked actors [occurrence 2]", InitialSend);
        Predictable("should deliver events sent from the entry actions to a service invoked in the same state", EntrySend);
        Predictable("should deliver events sent from the exit actions to a service invoked in the same state", ExitSend);
        foreach (var kind in new[] { "src with string reference", "src containing a machine directly", "src containing a callback actor directly" })
        {
            var captured = kind;
            Invoke("invoke config defined as " + kind + " should register unique and predictable child in state", () => PredictableId(captured));
        }
        SystemCase("should register an invoked actor", RegisteredCommunication);
        SystemCase("system can be immediately accessed outside the actor", InitialRegistry);
        SystemCase("should remove invoked actor from receptionist if stopped", UnregisterOnExit);
        SystemCase("should throw an error if an actor with the system ID already exists", Collision);
        SystemCase("should gracefully handle re-registration of a `systemId` during a reentering transition", ReuseSystemId);
        SystemCase("should be accessible in inline custom actions", InlineActionSystem);
        SystemCase("should be accessible in assign actions", AssignSystem);
        SystemCase("should unregister nested child systemIds when stopping a parent actor", NestedUnregister);
    }
    private static Dictionary<string, StateConfig<int>> States(params (string Key, StateConfig<int> State)[] states) => states.ToDictionary(x => x.Key, x => x.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Event, TransitionConfig<int> Transition)[] transitions) => transitions.ToDictionary(x => x.Event, x => (IReadOnlyList<TransitionConfig<int>>)new[] { x.Transition }, StringComparer.Ordinal);
    private static StateMachine<int> Machine(StateConfig<int> config, IReadOnlyDictionary<string, ActorSource>? actors = null) => new(config, _ => 0, actors: actors);
    private static ActorSource Empty() => ActorSource.From(Machine(new()));
    private static Actor<MachineSnapshot<int>> Actor(StateMachine<int> machine, string? systemId = null) =>
        new(machine, options: new() { SystemId = systemId, ErrorReporter = new FailReporter() });
    private sealed class FailReporter : IUnhandledErrorReporter
    {
        public void Report(object? failure) => throw new InvalidOperationException("Unexpected actor error.", ActorErrors.ToException(failure));
    }
    private static void NestedPing()
    {
        var pong = Machine(new()
        {
            Id = "pong", Initial = "active", States = States(("active", new()
            { On = On(("PING", new() { Actions = [MachineActions.SendParent<int>(_ => new("PONG"))] })) }))
        });
        var ping = Machine(new()
        {
            Id = "ping", Initial = "innerMachine", States = States(
                ("innerMachine", new()
                {
                    Initial = "active", OnDone = [new() { Target = ["success"] }],
                    States = States(
                        ("active", new()
                        {
                            Invoke = [new() { Id = "pong", Source = ActorSource.From(pong) }],
                            Entry = [MachineActions.SendTo<int>("pong", _ => new("PING"))],
                            On = On(("PONG", new() { Target = ["innerSuccess"] }))
                        }),
                        ("innerSuccess", new() { Kind = StateKind.Final }))
                }),
                ("success", new() { Kind = StateKind.Final }))
        });
        var completed = 0;
        var actor = Actor(ping);
        actor.Subscribe(onComplete: () => completed++);
        actor.Start();
        Equal(1, completed);
    }
    private static void TransientCallback()
    {
        var machine = Machine(new()
        {
            Id = "callback", Initial = "pending", States = States(
                ("pending", new() { On = On(("BEGIN", new() { Target = ["first"] })) }),
                ("first", new() { Always = [new() { Target = ["second"] }] }),
                ("second", new()
                {
                    Invoke = [new() { Source = ActorSource.Named("someCallback") }],
                    On = On(("CALLBACK", new() { Target = ["third"] }))
                }),
                ("third", new() { On = On(("NEXT", new() { Target = ["last"] })) }),
                ("last", new() { Kind = StateKind.Final }))
        }, new Dictionary<string, ActorSource>(StringComparer.Ordinal)
        {
            ["someCallback"] = ActorSource.From(new CallbackLogic(scope => { scope.SendBack(new("CALLBACK")); return null; }))
        });
        var values = new List<MachineSnapshot<int>>();
        var actor = Actor(machine);
        actor.Subscribe(values.Add);
        actor.Start().Send(new("BEGIN"));
        var expected = new[] { "pending", "second", "third" };
        for (var index = 0; index < expected.Length; index++) Equal(true, values[index].Matches(expected[index]));
        actor.Stop();
    }
    private static void InitialSend()
    {
        var child = Machine(new() { On = On(("PING", new() { Actions = [MachineActions.SendParent<int>(_ => new("PONG"))] })) });
        var actor = Actor(Machine(new()
        {
            Initial = "waiting", States = States(
                ("waiting", new()
                {
                    Entry = [MachineActions.SendTo<int>("ponger", _ => new("PING"))],
                    Invoke = [new() { Id = "ponger", Source = ActorSource.From(child) }],
                    On = On(("PONG", new() { Target = ["done"] }))
                }),
                ("done", new() { Kind = StateKind.Final }))
        })).Start();
        Equal(true, actor.GetSnapshot().Matches("done"));
    }
    private static void EntrySend()
    {
        MachineEvent? received = null;
        var child = Machine(new() { On = On(("*", new() { Actions = [MachineActions.Effect<int>((_, ev) => received = ev)] })) });
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(
                ("a", new() { On = On(("NEXT", new() { Target = ["b"] })) }),
                ("b", new()
                {
                    Entry = [MachineActions.SendTo<int>("myChild", _ => new("KNOCK_KNOCK"))],
                    Invoke = [new() { Id = "myChild", Source = ActorSource.From(child) }]
                }))
        })).Start();
        actor.Send(new("NEXT"));
        Equal<MachineEvent?>(new("KNOCK_KNOCK"), received);
        actor.Stop();
    }
    private static void ExitSend()
    {
        var delivered = false;
        var source = ActorSource.From(new CallbackLogic(scope =>
        {
            scope.Receive(ev => { if (ev.Type == "MY_EVENT") delivered = true; });
            return null;
        }));
        var actor = Actor(Machine(new()
        {
            Initial = "active", States = States(
                ("active", new()
                {
                    Invoke = [new() { Id = "my-service", Source = source }],
                    Exit = [MachineActions.SendTo<int>("my-service", _ => new("MY_EVENT"))],
                    On = On(("TOGGLE", new() { Target = ["inactive"] }))
                }), ("inactive", new()))
        })).Start();
        actor.Send(new("TOGGLE"));
        Equal(true, delivered);
        actor.Stop();
    }
    private static void ImmediateResponses()
    {
        var child = Machine(new()
        {
            Id = "child", Initial = "init", States = States(("init", new()
            {
                On = On(("FORWARD_DEC", new() { Actions = [
                    MachineActions.SendParent<int>(_ => new("DEC")), MachineActions.SendParent<int>(_ => new("DEC")), MachineActions.SendParent<int>(_ => new("DEC"))] }))
            }))
        });
        var parent = Machine(new()
        {
            Id = "parent", Initial = "start", States = States(
                ("start", new()
                {
                    Invoke = [new() { Source = ActorSource.Named("child"), Id = "someService" }],
                    Always = [new() { Target = ["stop"], Guard = MachineGuards.Predicate<int>((count, _) => count == -3) }],
                    On = On(("DEC", new() { Actions = [MachineActions.Assign<int>((count, _) => count - 1)] }),
                        ("FORWARD_DEC", new() { Actions = [MachineActions.SendTo<int>("someService", _ => new("FORWARD_DEC"))] }))
                }),
                ("stop", new() { Kind = StateKind.Final }))
        }, new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child"] = ActorSource.From(child) });
        var actor = Actor(parent).Start();
        actor.Send(new("FORWARD_DEC"));
        Equal(-3, actor.GetSnapshot().Context);
    }
    private static void StartsService(bool deep)
    {
        var child = Machine(new() { Id = "child", Initial = "sending", States = States(("sending", new()
        {
            Entry = [MachineActions.SendParent<int>(_ => new("SUCCESS", 42))]
        })) });
        var invoked = new StateConfig<int> { Invoke = [new() { Source = ActorSource.From(child) }] };
        var success = new TransitionConfig<int> { Target = [deep ? ".success" : "success"], Guard = MachineGuards.Predicate<int>((_, ev) => Equals(ev.Payload, 42)) };
        var config = deep
            ? new StateConfig<int>
            {
                Id = "parent", Initial = "a", States = States(
                    ("a", new() { Initial = "b", States = States(("b", invoked)) }),
                    ("success", new() { Id = "success", Kind = StateKind.Final })),
                On = On(("SUCCESS", success))
            }
            : new StateConfig<int>
            {
                Id = "machine-invoke", Initial = "pending", States = States(
                    ("pending", new() { Invoke = invoked.Invoke, On = On(("SUCCESS", success)) }),
                    ("success", new() { Kind = StateKind.Final }))
            };
        var completed = 0;
        var actor = Actor(Machine(config));
        actor.Subscribe(onComplete: () => completed++);
        actor.Start();
        Equal(1, completed);
    }
    private static void MicrostepReentry()
    {
        var invoked = 0;
        var source = ActorSource.From(new CallbackLogic(_ => { invoked++; return null; }));
        var machine = Machine(new()
        {
            Initial = "a", States = States(
                ("a", new() { Invoke = [new() { Source = source }], On = On(("GO_AWAY_AND_REENTER", new() { Target = ["b"] })) }),
                ("b", new() { Always = [new() { Target = ["a"] }] }))
        });
        var actor = Actor(machine).Start();
        actor.Send(new("GO_AWAY_AND_REENTER"));
        Equal(2, invoked);
        actor.Stop();
    }
    private static void CompletionStops(bool deep)
    {
        var disposed = false;
        var source = ActorSource.From(new CallbackLogic(_ => () => disposed = true));
        if (deep) source = ActorSource.From(Machine(new() { Invoke = [new() { Source = source }] }));
        var actor = Actor(Machine(new()
        {
            Invoke = [new() { Source = source }],
            Initial = "a", States = States(("a", new() { On = On(("FINISH", new() { Target = ["b"] })) }), ("b", new() { Kind = StateKind.Final }))
        })).Start();
        actor.Send(new("FINISH"));
        Equal(true, disposed);
    }
    private static void RestartOrder()
    {
        var actual = new List<string>();
        var count = 0;
        var source = ActorSource.From(new CallbackLogic(_ =>
        {
            var id = ++count;
            actual.Add("start " + id);
            return () => actual.Add("stop " + id);
        }));
        var actor = Actor(Machine(new()
        {
            Initial = "inactive", States = States(
                ("inactive", new() { On = On(("ACTIVATE", new() { Target = ["active"] })) }),
                ("active", new() { Invoke = [new() { Source = source }], On = On(("REENTER", new() { Target = ["active"], Reenter = true })) }))
        })).Start();
        actor.Send(new("ACTIVATE"));
        actual.Clear();
        actor.Send(new("REENTER"));
        Equal(true, actual.SequenceEqual(["stop 1", "start 2"]));
        actor.Stop();
    }
    private sealed record Responder(IActor Actor);
    private static void InputSelf()
    {
        var calls = 0;
        var actor = Actor(Machine(new()
        {
            Invoke = [new()
            {
                Input = args => new Responder(args.Self),
                Source = ActorSource.From(new CallbackLogic(scope =>
                {
                    var responder = scope.Input as Responder ?? throw new InvalidOperationException("Responder input missing.");
                    Action<MachineEvent> send = responder.Actor.Send;
                    Equal(true, send is not null);
                    calls++;
                    return null;
                }))
            }]
        })).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void PredictableId(string kind)
    {
        var callback = ActorSource.From(new CallbackLogic(_ => null));
        var source = kind switch
        {
            "src with string reference" => ActorSource.Named("someSrc"),
            "src containing a machine directly" => ActorSource.From(Machine(new() { Id = "someId" })),
            _ => callback
        };
        var machine = Machine(new()
        {
            Id = "machine", Initial = "a", States = States(("a", new() { Invoke = [new() { Source = source }] }))
        }, new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["someSrc"] = callback });
        var actor = Actor(machine);
        Equal(true, actor.GetSnapshot().Children.GetValueOrDefault("0.machine.a") is not null);
    }
    private static void RegisteredCommunication()
    {
        var calls = 0;
        var receiver = ActorSource.From(new CallbackLogic(scope =>
        {
            scope.Receive(ev => { Equal("HELLO", ev.Type); calls++; });
            return null;
        }));
        var sender = ActorSource.From(Machine(new()
        {
            Id = "childmachine", Entry = [MachineActions.Effect<int>(args => args.System.Get("receiver")?.Send(new("HELLO")))]
        }));
        var actor = Actor(Machine(new()
        {
            Id = "parent", Initial = "a", States = States(("a", new() { Invoke = [
                new() { Source = receiver, SystemId = "receiver" }, new() { Source = sender }] }))
        })).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void InitialRegistry()
    {
        var actor = Actor(Machine(new() { Invoke = [new() { Source = Empty(), SystemId = "someChild" }] }));
        Equal(true, actor.System.Get("someChild") is not null);
    }
    private static void UnregisterOnExit()
    {
        var actor = Actor(Machine(new()
        {
            Initial = "active", States = States(
                ("active", new() { Invoke = [new() { Source = Empty(), SystemId = "test" }], On = On(("toggle", new() { Target = ["inactive"] })) }),
                ("inactive", new()))
        })).Start();
        Equal(true, actor.System.Get("test") is not null);
        actor.Send(new("toggle"));
        Equal<IActor?>(null, actor.System.Get("test"));
        actor.Stop();
    }
    private static void Collision()
    {
        var actor = Actor(Machine(new()
        {
            Initial = "inactive", States = States(
                ("inactive", new() { On = On(("toggle", new() { Target = ["active"] })) }),
                ("active", new() { Invoke = [new() { Source = Empty(), SystemId = "test" }, new() { Source = Empty(), SystemId = "test" }] }))
        }), "test");
        var failures = new List<Exception>();
        actor.Subscribe(onError: failure => failures.Add(ActorTaskTests.RequireException(failure)));
        actor.Start().Send(new("toggle"));
        Equal(1, failures.Count);
        Equal("Actor with system ID 'test' already exists.", failures[0].Message);
    }
    private static void ReuseSystemId()
    {
        var calls = new List<(int Id, MachineEvent Event)>();
        var count = 0;
        var actor = Actor(Machine(new()
        {
            Initial = "listening", States = States(("listening", new()
            {
                Invoke = [new()
                {
                    SystemId = "listener", Source = ActorSource.From(new CallbackLogic(scope =>
                    {
                        var id = count++;
                        scope.Receive(ev => calls.Add((id, ev)));
                        return () => { };
                    }))
                }]
            })),
            On = On(("RESTART", new() { Target = [".listening"] }))
        })).Start();
        actor.Send(new("RESTART"));
        (actor.System.Get("listener") ?? throw new InvalidOperationException("Listener missing.")).Send(new("a"));
        Equal(true, calls.SequenceEqual([(1, new MachineEvent("a"))]));
        actor.Stop();
    }
    private static void InlineActionSystem()
    {
        var calls = 0;
        var actor = Actor(Machine(new()
        {
            Invoke = [new() { Source = Empty(), SystemId = "test" }],
            Entry = [MachineActions.Effect<int>(args => { Equal(true, args.System.Get("test") is not null); calls++; })]
        })).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void AssignSystem()
    {
        var calls = 0;
        var actor = Actor(Machine(new()
        {
            Invoke = [new() { Source = Empty(), SystemId = "test" }],
            Initial = "a", States = States(("a", new()
            {
                Entry = [MachineActions.Assign<int>(args => { Equal(true, args.System.Get("test") is not null); calls++; return args.Context; })]
            }))
        })).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void NestedUnregister()
    {
        var child = Machine(new()
        {
            Id = "childSystem", Invoke = [new() { Source = ActorSource.Named("subchild"), SystemId = "subchild" }]
        }, new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["subchild"] = Empty() });
        var parent = Machine(new()
        {
            Entry = [MachineActions.SpawnChild<int>(ActorSource.Named("child"), "childId")],
            On = On(("restart", new() { Actions = [MachineActions.StopChild<int>("childId"), MachineActions.SpawnChild<int>(ActorSource.Named("child"), "childId")] }))
        }, new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child"] = ActorSource.From(child) });
        var root = Actor(parent).Start();
        Equal(true, root.System.Get("subchild") is not null);
        root.Send(new("restart"));
        Equal(SnapshotStatus.Active, root.GetSnapshot().Status);
        Equal(true, root.System.Get("subchild") is not null);
        root.Stop();
    }
}
