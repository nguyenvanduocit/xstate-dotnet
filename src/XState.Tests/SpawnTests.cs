using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class SpawnTests
{
    private sealed record Context(IActor? Ref = null, IReadOnlyDictionary<int, IActor>? Todos = null);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void ActorCase(string group, string title, Action run) => cases.Add(("packages/core/test/actor.test.ts::" + group + " > " + title, run));
        ActorCase("spawning machines", "should spawn machines", Todo);
        ActorCase("spawning machines", "should spawn referenced machines", Named);
        ActorCase("spawning machines", "should allow bidirectional communication between parent/child actors", PingPong);
        ActorCase("spawning callbacks", "should not deliver events sent to the parent after the callback actor gets stopped", StoppedCallback);
        ActorCase("actors", "should only spawn an initial actor once when it synchronously responds with an event", InitializeOnce);
        ActorCase("actors", "should stop multiple inline spawned actors that have no explicit ids", () => StopAnonymous(false));
        ActorCase("actors", "should stop multiple referenced spawned actors that have no explicit ids", () => StopAnonymous(true));
        ActorCase("actors > with actor logic", "should work with a transition function logic", Reducer);
        ActorCase("actors", "should be able to spawn callback actors in (lazy) initial context", () => Lazy(false));
        ActorCase("actors", "should be able to spawn machines in (lazy) initial context", () => Lazy(true));
        ActorCase("actors", "should be able to restart a spawned actor within a single macrostep", () => Restart("different"));
        ActorCase("actors", "should be able to restart a named spawned actor within a single macrostep when stopping by a ref", () => Restart("reference"));
        ActorCase("actors", "should be able to restart a named spawned actor within a single macrostep when stopping by static name", () => Restart("static"));
        ActorCase("actors", "should be able to restart a named spawned actor within a single macrostep when stopping by resolved name", () => Restart("resolved"));
        ActorCase("actors", "should be possible to pass `self` as input to a child machine from within the context factory", SelfInput);
        cases.Add(("packages/core/test/system.test.ts::system > should register a spawned actor", RegisteredSpawn));
        cases.Add(("packages/core/test/system.test.ts::system > should remove spawned actor from receptionist if stopped", UnregisterSpawn));
        void InputCase(string title, Action run) => cases.Add(("packages/core/test/input.test.ts::input > " + title, run));
        InputCase("should create a machine with input", Input);
        InputCase("initial event should have input property", InitialEvent);
        InputCase("should error if input is expected but not provided", MissingInput);
        InputCase("should provide input data to invoked machines", () => ChildInput(false));
        InputCase("should provide input data to spawned machines", () => ChildInput(true));
    }
    private static Dictionary<string, StateConfig<Context>> States(params (string Key, StateConfig<Context> Value)[] entries) =>
        entries.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<Context>>> On(params (string Type, TransitionConfig<Context> Transition)[] entries) =>
        entries.ToDictionary(x => x.Type, x => (IReadOnlyList<TransitionConfig<Context>>)new[] { x.Transition }, StringComparer.Ordinal);
    private static StateMachine<Context> Machine(StateConfig<Context> config, Func<MachineContextArgs<Context>, Context>? initialize = null,
        IReadOnlyDictionary<string, ActorSource>? actors = null) => new(config, initialize ?? (_ => new()), actors: actors);
    private static Actor<MachineSnapshot<Context>> Actor(StateMachine<Context> machine) =>
        new(machine, options: new() { ErrorReporter = new FailureReporter() });
    private sealed class FailureReporter : IUnhandledErrorReporter
    {
        public void Report(object? failure) => throw new InvalidOperationException("Unexpected spawn error.", ActorErrors.ToException(failure));
    }
    private static void Todo()
    {
        var todo = Machine(new()
        {
            Id = "todo", Initial = "incomplete", States = States(
                ("incomplete", new() { On = On(("SET_COMPLETE", new() { Target = ["complete"] })) }),
                ("complete", new() { Entry = [MachineActions.SendParent<Context>(_ => new("TODO_COMPLETED"))] }))
        });
        var todos = Machine(new()
        {
            Id = "todos", Initial = "active", States = States(
                ("active", new() { On = On(("TODO_COMPLETED", new() { Target = ["success"] })) }),
                ("success", new() { Kind = StateKind.Final })),
            On = On(
                ("ADD", new() { Actions = [MachineActions.Assign<Context>(args =>
                {
                    var refs = args.Context.Todos is { } previous ? new Dictionary<int, IActor>(previous) : [];
                    refs[(int)(args.Event.Payload ?? throw new InvalidOperationException("ID missing."))] = args.Spawn(todo);
                    return args.Context with { Todos = refs };
                })] }),
                ("SET_COMPLETE", new() { Actions = [MachineActions.SendTo<Context>(
                    args => args.Context.Todos?[(int)(args.Event.Payload ?? throw new InvalidOperationException("ID missing."))],
                    _ => new("SET_COMPLETE"))] }))
        });
        var completed = 0;
        var actor = Actor(todos);
        actor.Subscribe(onComplete: () => completed++);
        actor.Start().Send(new("ADD", 42));
        actor.Send(new("SET_COMPLETE", 42));
        Equal(1, completed);
    }
    private static void Named()
    {
        var child = Machine(new() { Entry = [MachineActions.SendParent<Context>(_ => new("DONE"))] });
        var actor = Actor(Machine(new()
        {
            Initial = "waiting", States = States(
                ("waiting", new()
                {
                    Entry = [MachineActions.Assign<Context>(args => args.Context with { Ref = args.Spawn(ActorSource.Named("child")) })],
                    On = On(("DONE", new() { Target = ["success"] }))
                }), ("success", new() { Kind = StateKind.Final }))
        }, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child"] = ActorSource.From(child) })).Start();
        Equal(true, actor.GetSnapshot().Matches("success"));
    }
    private static void PingPong()
    {
        var server = Machine(new()
        {
            Id = "server", Initial = "waitPing", States = States(
                ("waitPing", new() { On = On(("PING", new() { Target = ["sendPong"] })) }),
                ("sendPong", new()
                {
                    Entry = [MachineActions.SendParent<Context>(_ => new("PONG")), MachineActions.Raise<Context>((_, _) => new("SUCCESS"))],
                    On = On(("SUCCESS", new() { Target = ["waitPing"] }))
                }))
        });
        var client = Machine(new()
        {
            Id = "client", Initial = "init", States = States(
                ("init", new()
                {
                    Entry = [MachineActions.Assign<Context>(args => args.Context with { Ref = args.Spawn(server) }),
                        MachineActions.Raise<Context>((_, _) => new("SUCCESS"))],
                    On = On(("SUCCESS", new() { Target = ["sendPing"] }))
                }),
                ("sendPing", new()
                {
                    Entry = [MachineActions.SendTo<Context>(args => args.Context.Ref, _ => new("PING")),
                        MachineActions.Raise<Context>((_, _) => new("SUCCESS"))],
                    On = On(("SUCCESS", new() { Target = ["waitPong"] }))
                }),
                ("waitPong", new() { On = On(("PONG", new() { Target = ["complete"] })) }),
                ("complete", new() { Kind = StateKind.Final }))
        });
        var completed = 0;
        var actor = Actor(client);
        actor.Subscribe(onComplete: () => completed++);
        actor.Start();
        Equal(1, completed);
    }
    private static void StoppedCallback()
    {
        Action? send = null;
        var calls = 0;
        var actor = Actor(Machine(new()
        {
            Initial = "a", States = States(
                ("a", new()
                {
                    Invoke = [new() { Source = ActorSource.From(new CallbackLogic(scope => { send = () => scope.SendBack(new("FROM_CALLBACK")); return null; })) }],
                    On = On(("NEXT", new() { Target = ["b"] }))
                }), ("b", new())),
            On = On(("FROM_CALLBACK", new() { Actions = [MachineActions.Effect<Context>((_, _) => calls++)] }))
        })).Start();
        actor.Send(new("NEXT"));
        (send ?? throw new InvalidOperationException("Callback did not start."))();
        Equal(0, calls);
        actor.Stop();
    }
    private static void InitializeOnce()
    {
        var count = 0;
        var child = Machine(new()
        {
            Initial = "hello", States = States(("hello", new() { Entry = [MachineActions.SendParent<Context>(_ => new("ping"))] }))
        });
        var actor = Actor(Machine(new()
        {
            Initial = "testing", States = States(
                ("testing", new() { On = On(("ping", new() { Target = ["done"] })) }), ("done", new()))
        }, args =>
        {
            count++;
            Equal(1, count);
            return new(args.Spawn(child));
        })).Start();
        Equal(true, actor.GetSnapshot().Matches("done"));
        actor.Stop();
    }
    private static void StopAnonymous(bool named)
    {
        var cleanups = new int[2];
        var first = new CallbackLogic(_ => () => cleanups[0]++);
        var second = new CallbackLogic(_ => () => cleanups[1]++);
        var machine = Machine(new(), args =>
        {
            var a = named ? args.Spawn(ActorSource.Named("child1")) : args.Spawn(first);
            var b = named ? args.Spawn(ActorSource.Named("child2")) : args.Spawn(second);
            return new(a, new Dictionary<int, IActor> { [1] = a, [2] = b });
        }, new Dictionary<string, ActorSource>(StringComparer.Ordinal)
        {
            ["child1"] = ActorSource.From(first), ["child2"] = ActorSource.From(second)
        });
        var actor = Actor(machine).Start();
        Equal(2, actor.GetSnapshot().Children.Count);
        actor.Stop();
        Equal(1, cleanups[0]);
        Equal(1, cleanups[1]);
    }
    private static void Reducer()
    {
        var logic = new TransitionLogic<int>((count, ev, _) => ev.Type switch { "INC" => count + 1, "DEC" => count - 1, _ => count }, 0);
        var machine = Machine(new()
        {
            Entry = [MachineActions.Assign<Context>(args => args.Context with { Ref = args.Spawn(logic) })],
            On = On(("INC", new() { Actions = [MachineActions.ForwardTo<Context>(args => args.Context.Ref)] }))
        });
        var observed = false;
        var actor = Actor(machine);
        actor.Subscribe(state =>
        {
            if (state.Context.Ref is Actor<TransitionSnapshot<int>> child && child.GetSnapshot().Context == 2) observed = true;
        });
        actor.Start().Send(new("INC"));
        actor.Send(new("INC"));
        Equal(2, ((Actor<TransitionSnapshot<int>>)(actor.GetSnapshot().Context.Ref ?? throw new InvalidOperationException("Child missing."))).GetSnapshot().Context);
        Equal(true, observed);
        actor.Stop();
    }
    private static void Lazy(bool machineChild)
    {
        var child = machineChild
            ? ActorSource.From(Machine(new() { Entry = [MachineActions.SendParent<Context>(_ => new("TEST"))] }))
            : ActorSource.From(new CallbackLogic(scope => { scope.SendBack(new("TEST")); return null; }));
        var parent = Machine(new()
        {
            Initial = "waiting", States = States(
                ("waiting", new() { On = On(("TEST", new() { Target = ["success"] })) }),
                ("success", new() { Kind = StateKind.Final }))
        }, args => new(args.Spawn(child)));
        var completed = 0;
        var actor = Actor(parent);
        actor.Subscribe(onComplete: () => completed++);
        actor.Start();
        Equal(1, completed);
    }
    private static void Restart(string mode)
    {
        var actual = new List<string>();
        var count = 0;
        CallbackLogic Child()
        {
            var localId = ++count;
            return new(_ =>
            {
                if (mode != "resolved" || localId != 1) actual.Add("start " + localId);
                return () => actual.Add("stop " + localId);
            });
        }
        var firstId = mode == "different" ? "callback-1" : "my_name";
        var secondId = mode == "different" ? "callback-2" : "my_name";
        var stop = mode switch
        {
            "static" => MachineActions.StopChild<Context>("my_name"),
            "resolved" => MachineActions.StopChild<Context>(_ => "my_name"),
            _ => MachineActions.StopChild<Context>(args => args.Context.Ref)
        };
        var machine = Machine(new()
        {
            Initial = "active", States = States(("active", new()
            {
                On = On(("update", new() { Actions = [stop,
                    MachineActions.Assign<Context>(args => args.Context with { Ref = args.Spawn(Child(), id: secondId) })] }))
            }))
        }, args =>
        {
            var logic = Child();
            if (mode == "resolved") actual.Add("start 1");
            return new(args.Spawn(logic, id: firstId));
        });
        var actor = Actor(machine).Start();
        actual.Clear();
        actor.Send(new("update"));
        Equal(true, actual.SequenceEqual(["stop 1", "start 2"]));
        actor.Stop();
    }
    private static void SelfInput()
    {
        var calls = 0;
        var child = Machine(new()
        {
            Entry = [MachineActions.SendTo<Context>(args => args.Context.Ref, _ => new("GREET"))]
        }, args => new((IActor)(args.Input ?? throw new InvalidOperationException("Parent input missing."))));
        var actor = Actor(Machine(new()
        {
            On = On(("GREET", new() { Actions = [MachineActions.Effect<Context>((_, _) => calls++)] }))
        }, args => new(args.Spawn(child, input: args.Self)))).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void RegisteredSpawn()
    {
        var received = 0;
        var machine = Machine(new()
        {
            Id = "parent",
            On = On(("toggle", new() { Actions = [MachineActions.Assign<Context>(args =>
            {
                var child = Machine(new()
                {
                    Id = "childmachine", Entry = [MachineActions.Effect<Context>(action =>
                    {
                        var receiver = action.System.Get("receiver") ?? throw new InvalidOperationException("Receiver missing.");
                        receiver.Send(new("HELLO"));
                    })]
                });
                return args.Context with { Todos = new Dictionary<int, IActor> { [1] = args.Spawn(child) } };
            })] }))
        }, args => new(args.Spawn(new CallbackLogic(scope =>
        {
            scope.Receive(ev => { Equal("HELLO", ev.Type); received++; });
            return null;
        }), systemId: "receiver")));
        var actor = Actor(machine).Start();
        actor.Send(new("toggle"));
        Equal(1, received);
        actor.Stop();
    }
    private static void UnregisterSpawn()
    {
        var child = Machine(new());
        var machine = Machine(new()
        {
            On = On(("toggle", new() { Actions = [MachineActions.StopChild<Context>(args => args.Context.Ref)] }))
        }, args => new(args.Spawn(child, systemId: "test")));
        var actor = Actor(machine).Start();
        Equal(true, actor.System.Get("test") is not null);
        actor.Send(new("toggle"));
        Equal<IActor?>(null, actor.System.Get("test"));
        actor.Stop();
    }
    private static void Input()
    {
        var calls = new List<int>();
        var machine = new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>((context, _) => calls.Add(context))] },
            args => (int)(args.Input ?? throw new InvalidOperationException("Count missing.")));
        var actor = new Actor<MachineSnapshot<int>>(machine, input: 42).Start();
        Equal(true, calls.SequenceEqual([42]));
        actor.Stop();
    }
    private static void InitialEvent()
    {
        var calls = 0;
        var machine = Machine(new()
        {
            Entry = [MachineActions.Effect<Context>((_, ev) => { Equal<object?>("hello", ev.Payload); calls++; })]
        });
        var actor = new Actor<MachineSnapshot<Context>>(machine, input: "hello").Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void MissingInput()
    {
        var machine = new StateMachine<string>(new(), args =>
        {
            var greeting = args.Input as string ?? throw new InvalidOperationException("Greeting input missing.");
            return "Hello, " + greeting;
        });
        Equal(SnapshotStatus.Error, new Actor<MachineSnapshot<string>>(machine).GetSnapshot().Status);
    }
    private static void ChildInput(bool spawn)
    {
        var calls = 0;
        var child = new StateMachine<string>(new()
        {
            Entry = [MachineActions.Effect<string>((context, ev) =>
            {
                Equal("hello", context);
                Equal<object?>("hello", ev.Payload);
                calls++;
            })]
        }, args => args.Input as string ?? throw new InvalidOperationException("Greeting missing."));
        var config = spawn
            ? new StateConfig<Context> { Entry = [MachineActions.Assign<Context>(args => new(args.Spawn(child, input: "hello")))] }
            : new StateConfig<Context> { Invoke = [new() { Source = ActorSource.From(child), Input = _ => "hello" }] };
        var actor = Actor(Machine(config)).Start();
        Equal(1, calls);
        actor.Stop();
    }
}
