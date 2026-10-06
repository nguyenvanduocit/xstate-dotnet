using System.Runtime.CompilerServices;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class EnqueueActionTests
{
    private sealed record Parameter(string Key, int Value);
    public static void Register(List<(string Id, Action Run)> cases)
    {
        void Case(string title, Action run) => cases.Add(("packages/core/test/actions.test.ts::enqueueActions > " + title, run));
        Case("should execute a simple referenced action", () => Referenced(1, false));
        Case("should execute multiple different referenced actions", () => Referenced(2, true));
        Case("should execute multiple same referenced actions", () => Referenced(2, false));
        Case("should execute a parameterized action", Parameterized);
        Case("should execute a function", Inline);
        Case("should execute a builtin action using its own action creator", () => Raised(false));
        Case("should execute a builtin action using its bound action creator", () => Raised(true));
        Case("should execute assigns when resolving the initial snapshot", InitialAssign);
        Case("should be able to check a simple referenced guard", () => Checked(false));
        Case("should be able to check a parameterized guard", () => Checked(true));
        Case("should provide self", Self);
        Case("should be able to communicate with the parent using params", ParameterizedParent);
        Case("should enqueue.sendParent", SendParent);
        cases.Add(("packages/core/test/emit.test.ts::event emitter > listener should be able to read the updated snapshot of the emitting actor", UpdatedSnapshot));
        cases.Add(("packages/core/test/emit.test.ts::event emitter > wildcard listeners should be able to receive all emitted events", Wildcard));
    }
    public static void RegisterAsync(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Func<Task> run) => cases.Add(("packages/core/test/emit.test.ts::event emitter > " + title, run));
        Case("emits events that can be listened to on actorRef.on(…)", () => Listened(false, false));
        Case("enqueue.emit(…) emits events that can be listened to on actorRef.on(…)", () => Listened(true, false));
        Case("dynamically emits events that can be listened to on actorRef.on(…)", () => Listened(false, true));
        Case("handles errors", ListenerError);
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("enqueue checks capture pre-assign context even after the collector escapes", CollectionSnapshot);
        Case("nested enqueue actions resolve in order and do not inherit outer params", Nested);
        Case("collector failure does not resolve already collected actions", CollectionError);
        Case("enqueue observes actions appended during resolution", LiveCollection);
        Case("pure enqueue resolves assign and returns emit effects without emission", Pure);
        Case("enqueued spawn and stop share child ownership and cleanup", SpawnStop);
        Case("enqueued emit payload is collectable while actor and machine remain live", PayloadRelease);
        Case("escaped enqueue check retains nodes after actor transitions", CapturedNodes);
        Case("parameterized emit captures payload before later assignments and delivers after commit", EmissionOrder);
    }
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(params (string Event, TransitionConfig<int> Transition)[] entries) =>
        entries.ToDictionary(x => x.Event, x => (IReadOnlyList<TransitionConfig<int>>)new[] { x.Transition }, StringComparer.Ordinal);
    private static Actor<MachineSnapshot<int>> Actor(StateMachine<int> machine) => new(machine, options: new() { ErrorReporter = new FailureReporter() });
    private sealed class FailureReporter : IUnhandledErrorReporter
    {
        public void Report(object? failure) => throw new InvalidOperationException("Unexpected enqueue error.", ActorErrors.ToException(failure));
    }
    private static void Referenced(int count, bool different)
    {
        var calls = new int[2];
        var machine = new StateMachine<int>(new()
        {
            Entry = [MachineActions.EnqueueActions<int>(args =>
            {
                args.Enqueue.Add("someAction");
                if (count == 2) args.Enqueue.Add(different ? "otherAction" : "someAction");
            })]
        }, _ => 0, actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal)
        {
            ["someAction"] = MachineActions.Effect<int>((_, _) => calls[0]++),
            ["otherAction"] = MachineActions.Effect<int>((_, _) => calls[1]++)
        });
        var actor = Actor(machine).Start();
        Equal(different ? 1 : count, calls[0]);
        Equal(different ? 1 : 0, calls[1]);
        actor.Stop();
    }
    private static void Parameterized()
    {
        var seen = new List<object?>();
        var machine = new StateMachine<int>(new() { Entry = [MachineActions.EnqueueActions<int>(args => args.Enqueue.Add("someAction", new Parameter("answer", 42)))] },
            _ => 0, actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal)
            { ["someAction"] = MachineActions.Effect<int>(args => seen.Add(args.Parameters)) });
        var actor = Actor(machine).Start();
        Equal(1, seen.Count);
        Equal<object?>(new Parameter("answer", 42), seen[0]);
        actor.Stop();
    }
    private static void Inline()
    {
        var calls = 0;
        var actor = Actor(new(new() { Entry = [MachineActions.EnqueueActions<int>(args => args.Enqueue.Add(MachineActions.Effect<int>((_, _) => calls++)))] }, _ => 0)).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void Raised(bool bound)
    {
        var calls = 0;
        var actor = Actor(new(new() { On = On(
            ("FOO", new() { Actions = [MachineActions.EnqueueActions<int>(args =>
            {
                if (bound) args.Enqueue.Raise(new MachineEvent("RAISED"));
                else args.Enqueue.Add(MachineActions.Raise<int>((_, _) => new("RAISED")));
            })] }),
            ("RAISED", new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] })) }, _ => 0)).Start();
        actor.Send(new("FOO"));
        Equal(1, calls);
        actor.Stop();
    }
    private static void InitialAssign()
    {
        var actor = Actor(new(new() { Entry = [MachineActions.EnqueueActions<int>(args => args.Enqueue.Assign((_, _) => 42))] }, _ => 0));
        Equal(42, actor.GetSnapshot().Context);
        actor.Stop();
    }
    private static void Checked(bool parameterized)
    {
        var seen = new List<object?>();
        var machine = new StateMachine<int>(new() { Entry = [MachineActions.EnqueueActions<int>(args =>
        {
            if (parameterized) args.Check(MachineGuards.Named<int>("alwaysTrue", new Parameter("max", 100)));
            else args.Check("alwaysTrue");
        })] }, _ => 0, guards: new Dictionary<string, MachineGuard<int>>(StringComparer.Ordinal)
        { ["alwaysTrue"] = MachineGuards.Predicate<int>(args => { seen.Add(args.Parameters); return true; }) });
        var actor = Actor(machine);
        Equal(1, seen.Count);
        Equal<object?>(parameterized ? new Parameter("max", 100) : null, seen[0]);
        actor.Stop();
    }
    private static void Self()
    {
        var calls = 0;
        var actor = Actor(new(new() { Entry = [MachineActions.EnqueueActions<int>(args =>
        {
            Action<MachineEvent> send = args.Self.Send;
            Equal(true, send.Target is IActor);
            Equal(args.Self.System, args.System);
            calls++;
        })] }, _ => 0)).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void ParameterizedParent()
    {
        var child = new StateMachine<IActor?>(new() { Entry = [MachineActions.Named<IActor?>("mySendParent", new MachineEvent("FOO"))] },
            args => args.Input as IActor,
            actions: new Dictionary<string, MachineAction<IActor?>>(StringComparer.Ordinal)
            { ["mySendParent"] = MachineActions.EnqueueActions<IActor?>(args =>
            {
                if (args.Context is null) return;
                args.Enqueue.SendTo(args.Context, args.Parameters as MachineEvent ?? throw new InvalidOperationException("Missing parent event."));
            }) });
        var calls = 0;
        var parent = new StateMachine<int>(new()
        {
            Invoke = [new() { Source = ActorSource.Named("child"), Input = args => args.Self }],
            On = On(("FOO", new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] }))
        }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child"] = ActorSource.From(child) });
        var actor = Actor(parent).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static void SendParent()
    {
        var child = new StateMachine<int>(new() { Entry = [MachineActions.Named<int>("sendToParent")] }, _ => 0,
            actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal)
            { ["sendToParent"] = MachineActions.EnqueueActions<int>(args => args.Enqueue.SendParent(new MachineEvent("PARENT_EVENT"))) });
        var calls = 0;
        var parent = new StateMachine<int>(new()
        {
            Invoke = [new() { Source = ActorSource.Named("child") }],
            On = On(("PARENT_EVENT", new() { Actions = [MachineActions.Effect<int>((_, _) => calls++)] }))
        }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child"] = ActorSource.From(child) });
        var actor = Actor(parent).Start();
        Equal(1, calls);
        actor.Stop();
    }
    private static StateMachine<int> EmittingMachine(MachineAction<int> action) => new(new()
    { On = On(("someEvent", new() { Actions = [action] })) }, _ => 10);
    private static async Task Listened(bool enqueued, bool dynamic)
    {
        var action = enqueued ? MachineActions.EnqueueActions<int>(args =>
        {
            args.Enqueue.Emit(new MachineEvent("emitted", "bar"));
            args.Enqueue.Emit(new MachineEvent("unknown"));
        }) : dynamic ? MachineActions.Emit<int>(args => new("emitted", args.Context)) : MachineActions.Emit<int>(new MachineEvent("emitted", "bar"));
        var actor = Actor(EmittingMachine(action)).Start();
        var completion = new TaskCompletionSource<MachineEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = actor.On("emitted", ev => completion.TrySetResult(ev));
        await Task.Delay(1).ConfigureAwait(false);
        actor.Send(new("someEvent"));
        var received = await completion.Task.ConfigureAwait(false);
        Equal("emitted", received.Type);
        Equal<object?>(dynamic ? 10 : "bar", received.Payload);
        actor.Stop();
    }
    private sealed class ErrorCollector : IUnhandledErrorReporter
    {
        public List<Exception> Errors { get; } = [];
        public void Report(object? failure) => Errors.Add(ActorErrors.ToException(failure));
    }
    private static async Task ListenerError()
    {
        var reporter = new ErrorCollector();
        var actor = new Actor<MachineSnapshot<int>>(EmittingMachine(MachineActions.Emit<int>(new MachineEvent("emitted", "bar"))), options: new() { ErrorReporter = reporter }).Start();
        using var subscription = actor.On("emitted", _ => throw new InvalidOperationException("oops"));
        await Task.Delay(1).ConfigureAwait(false);
        actor.Send(new("someEvent"));
        await Task.Delay(10).ConfigureAwait(false);
        Equal(SnapshotStatus.Active, actor.GetSnapshot().Status);
        Equal(1, reporter.Errors.Count);
        Equal("oops", reporter.Errors[0].Message);
        actor.Stop();
    }
    private static void UpdatedSnapshot()
    {
        var seen = new List<string>();
        var actor = Actor(new(new()
        {
            Initial = "a", States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            {
                ["a"] = new() { On = On(("ev", new() { Target = ["b"], Actions = [MachineActions.Emit<int>(new MachineEvent("someEvent"))] })) },
                ["b"] = new()
            }
        }, _ => 0));
        using var subscription = actor.On("someEvent", _ => seen.Add(actor.GetSnapshot().Value.AtomicValue ?? "missing"));
        actor.Start();
        actor.Send(new("ev"));
        Equal(1, seen.Count);
        Equal("b", seen[0]);
        actor.Stop();
    }
    private static void Wildcard()
    {
        var calls = 0;
        var actor = Actor(new(new() { On = On(("event", new() { Actions = [MachineActions.Emit<int>(new MachineEvent("emitted"))] })) }, _ => 0));
        using var subscription = actor.On("*", _ => calls++);
        actor.Start();
        actor.Send(new("event"));
        Equal(1, calls);
        actor.Stop();
    }
    private static void CollectionSnapshot()
    {
        MachineEnqueueArgs<int>? collected = null;
        var seen = new List<int>();
        var actor = Actor(new(new() { Entry = [MachineActions.EnqueueActions<int>(args =>
        {
            collected = args;
            args.Enqueue.Assign((_, _) => 42);
            Equal(true, args.Check(MachineGuards.Predicate<int>((context, _) => context == 7)));
            args.Enqueue.Add(MachineActions.Effect<int>((context, _) => seen.Add(context)));
        })] }, _ => 7)).Start();
        Equal(42, actor.GetSnapshot().Context);
        Equal(42, seen.Single());
        Equal(true, collected?.Check(MachineGuards.Predicate<int>((context, _) => context == 7)));
        collected?.Enqueue.Assign((_, _) => 99);
        Equal(42, actor.GetSnapshot().Context);
        actor.Stop();
    }
    private static void Nested()
    {
        var seen = new List<string>();
        var actor = Actor(new(new() { Entry = [MachineActions.Named<int>("collect", 5)] }, _ => 1,
            actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal)
            { ["collect"] = MachineActions.EnqueueActions<int>(args =>
            {
                Equal<object?>(5, args.Parameters);
                args.Enqueue.Assign((context, _) => context + 1);
                args.Enqueue.Add(MachineActions.EnqueueActions<int>(nested =>
                {
                    Equal(false, nested.HasParameters);
                    Equal(2, nested.Context);
                    nested.Enqueue.Assign((context, _) => context * 2);
                    nested.Enqueue.Add(MachineActions.Effect<int>((context, _) => seen.Add("nested:" + context)));
                }));
                args.Enqueue.Add(MachineActions.Effect<int>((context, _) => seen.Add("outer:" + context)));
            }) })).Start();
        Equal("nested:4,outer:4", string.Join(',', seen));
        Equal(4, actor.GetSnapshot().Context);
        actor.Stop();
    }
    private static void CollectionError()
    {
        var assignCalls = 0;
        var effectCalls = 0;
        var failure = new InvalidOperationException("collector failed");
        var actor = Actor(new(new() { Entry = [MachineActions.EnqueueActions<int>(args =>
        {
            args.Enqueue.Assign((context, _) => { assignCalls++; return context + 1; });
            args.Enqueue.Add(MachineActions.Effect<int>((_, _) => effectCalls++));
            throw failure;
        })] }, _ => 7));
        actor.Subscribe(onError: error => Equal(failure, error));
        actor.Start();
        Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        Equal(7, actor.GetSnapshot().Context);
        Equal(0, assignCalls);
        Equal(0, effectCalls);
    }
    private static void LiveCollection()
    {
        var seen = new List<int>();
        var actor = Actor(new(new() { On = On(("GO", new() { Actions = [MachineActions.EnqueueActions<int>(args =>
        {
            args.Enqueue.Add(MachineActions.Effect<int>((_, _) =>
            {
                seen.Add(1);
                args.Enqueue.Add(MachineActions.Effect<int>((_, _) => seen.Add(3)));
            }));
            args.Enqueue.Add(MachineActions.Effect<int>((_, _) => seen.Add(2)));
        })] })) }, _ => 0)).Start();
        actor.Send(new("GO"));
        Equal("1,2,3", string.Join(',', seen));
        actor.Stop();
    }
    private static void Pure()
    {
        var calls = 0;
        var result = ActorTransitions.Initial(new StateMachine<int>(new() { Entry = [MachineActions.EnqueueActions<int>(args =>
        {
            args.Enqueue.Assign((_, _) => 42);
            args.Enqueue.Emit(new MachineEvent("emitted"));
            args.Enqueue.Add(MachineActions.Effect<int>((_, _) => calls++));
        })] }, _ => 0));
        Equal(42, result.Snapshot.Context);
        Equal(2, result.Actions.Count);
        Equal(0, calls);
    }
    private static void SpawnStop()
    {
        var starts = 0;
        var stops = 0;
        var child = ActorSource.From(new CallbackLogic(_ => { starts++; return () => stops++; }));
        var actor = Actor(new(new()
        {
            Entry = [MachineActions.EnqueueActions<int>(args => args.Enqueue.SpawnChild(child, "worker"))],
            On = On(("STOP", new() { Actions = [MachineActions.EnqueueActions<int>(args => args.Enqueue.StopChild("worker"))] }))
        }, _ => 0)).Start();
        Equal(1, starts);
        actor.Send(new("STOP"));
        Equal(1, stops);
        Equal(0, actor.GetSnapshot().Children.Count);
        actor.Stop();
        Equal(1, stops);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Actor<MachineSnapshot<int>> Actor, StateMachine<int> Machine, WeakReference Payload) EmittedPayload()
    {
        WeakReference? reference = null;
        var machine = EmittingMachine(MachineActions.EnqueueActions<int>(args =>
        {
            var payload = new byte[4096];
            reference = new WeakReference(payload);
            args.Enqueue.Emit(new MachineEvent("emitted", payload));
        }));
        var actor = Actor(machine).Start();
        actor.Send(new("someEvent"));
        return (actor, machine, reference ?? throw new InvalidOperationException("No payload created."));
    }
    private static void CapturedNodes()
    {
        MachineEnqueueArgs<int>? saved = null;
        var actor = Actor(new(new()
        {
            Initial = "a", States = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
            {
                ["a"] = new()
                {
                    Entry = [MachineActions.EnqueueActions<int>(args => saved = args)],
                    On = On(("GO", new() { Target = ["b"] }))
                },
                ["b"] = new()
            }
        }, _ => 0)).Start();
        actor.Send(new("GO"));
        Equal(true, actor.GetSnapshot().Matches("b"));
        Equal(true, saved?.Check(MachineGuards.StateIn<int>("a")));
        Equal(false, saved?.Check(MachineGuards.StateIn<int>("b")));
        actor.Stop();
    }
    private static void EmissionOrder()
    {
        var seen = new List<(int Payload, int Committed)>();
        var machine = new StateMachine<int>(new()
        {
            On = On(("GO", new() { Actions = [MachineActions.EnqueueActions<int>(args =>
            {
                args.Enqueue.Assign((_, _) => 2);
                args.Enqueue.Add("sendValue", 10);
                args.Enqueue.Assign((_, _) => 4);
            })] }))
        }, _ => 1, actions: new Dictionary<string, MachineAction<int>>(StringComparer.Ordinal)
        {
            ["sendValue"] = MachineActions.Emit<int>(args => new("value", args.Context +
                (args.Parameters is int amount ? amount : throw new InvalidOperationException("Missing emit params."))))
        });
        var actor = Actor(machine).Start();
        using var subscription = actor.On("value", ev => seen.Add((ev.Payload is int amount ? amount : -1, actor.GetSnapshot().Context)));
        actor.Send(new("GO"));
        Equal((12, 4), seen.Single());
        actor.Stop();
    }
    private static void PayloadRelease()
    {
        var (actor, machine, reference) = EmittedPayload();
        for (var i = 0; i < 3 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Equal(false, reference.IsAlive);
        actor.Stop();
        GC.KeepAlive(actor);
        GC.KeepAlive(machine);
    }
}
