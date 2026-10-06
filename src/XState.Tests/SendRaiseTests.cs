using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ConcurrentInvocationTests;
namespace XStatePort.Tests;

internal static class SendRaiseTests
{
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    private sealed record Context(IActor Child, int Count);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string group, string title, Action run) => cases.Add(("packages/core/test/actions.test.ts::" + group + " > " + title, () => { run(); return Task.CompletedTask; }));
        Case("sendTo", "should be able to send an event to an actor", () => Child("actor"));
        Case("sendTo", "should be able to send an event from expression to an actor", () => Child("expression"));
        Case("sendTo", "should be able to send an event to a named actor", () => Child("named"));
        Case("sendTo", "should be able to send an event directly to an ActorRef", () => Child("reference"));
        Case("sendTo", "should be able to read from event", EventTarget);
        Case("sendTo", "should error if given a string", () => Invalid(false));
        Case("sendTo", "a self-event \"handler\" of an event sent using sendTo should be able to read updated snapshot of self", SelfSnapshot);
        cases.Add(("packages/core/test/actions.test.ts::sendTo > should not attempt to deliver a delayed event to the spawned actor's ID that was stopped since the event was scheduled", () => Replaced(false)));
        cases.Add(("packages/core/test/actions.test.ts::sendTo > should not attempt to deliver a delayed event to the invoked actor's ID that was stopped since the event was scheduled", () => Replaced(true)));
        Case("raise", "should be able to raise an event and respond to it in the same state", StaticRaise);
        Case("raise", "should accept event expression", () => RaiseExpression(false));
        Case("raise", "should be possible to access context in the event expression", () => RaiseExpression(true));
        Case("raise", "should error if given a string", () => Invalid(true));
    }
    private static void Child(string mode)
    {
        var received = new List<MachineEvent>(); var child = new StateMachine<int>(new() { Initial = "waiting", States = States<int>(("waiting", new() { On = On<int>(("EVENT", new() { Actions = [MachineActions.Effect<int>((_, ev) => received.Add(ev))] })) })) }, _ => 0);
        var action = mode switch { "named" => MachineActions.SendTo<Context>("child", new MachineEvent("EVENT")), "expression" => MachineActions.SendTo<Context>(args => args.Context.Child, args => new("EVENT", args.Context.Count)), _ => MachineActions.SendTo<Context>(args => args.Context.Child, new MachineEvent("EVENT")) };
        // The upstream test titled "directly to an ActorRef" also uses a target expression.
        var machine = new StateMachine<Context>(new() { Entry = [action] }, args => new(args.Spawn(child, id: mode is "named" or "expression" ? "child" : null), 42));
        var actor = new Actor<MachineSnapshot<Context>>(machine).Start(); try { Equal(1, received.Count); Equal("EVENT", received[0].Type); if (mode == "expression") Equal<object?>(42, received[0].Payload); Results[mode] = received.Select(ev => new { type = ev.Type, count = ev.Payload }).ToArray(); } finally { actor.Stop(); }
    }
    private static void EventTarget()
    {
        var received = new List<MachineEvent>(); var child = new CallbackLogic(scope => { scope.Receive(received.Add); return null; });
        var machine = new StateMachine<Dictionary<string, IActor>>(new() { Initial = "a", States = States<Dictionary<string, IActor>>(("a", new() { On = On<Dictionary<string, IActor>>(("EVENT", new() { Actions = [MachineActions.SendTo<Dictionary<string, IActor>>(args => args.Context[(string)(args.Event.Payload ?? throw new InvalidOperationException("Target key missing."))], new MachineEvent("EVENT"))] })) })) }, args => new(StringComparer.Ordinal) { ["foo"] = args.Spawn(child) });
        var actor = new Actor<MachineSnapshot<Dictionary<string, IActor>>>(machine).Start(); try { actor.Send(new("EVENT", "foo")); Equal(1, received.Count); Equal(new MachineEvent("EVENT"), received[0]); Results["eventTarget"] = received.Select(ev => ev.Type).ToArray(); } finally { actor.Stop(); }
    }
    private static void Invalid(bool raise)
    {
        var config = raise ? new StateConfig<int> { Entry = [MachineActions.Raise<int>("a string")] } : new StateConfig<int> { Invoke = [new() { Id = "child", Source = ActorSource.From(new CallbackLogic(_ => null)) }], Entry = [MachineActions.SendTo<int>("child", "a string")] };
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0)); var errors = new List<object?>(); using var subscription = actor.Subscribe(onError: errors.Add);
        try { actor.Start(); Equal(1, errors.Count); var message = RequireException(errors[0]).Message; var action = raise ? "raise" : "sendTo"; Equal($"Only event objects may be used with {action}; use {action}({{ type: \"a string\" }}) instead", message); Results["invalid:" + action] = message; } finally { actor.Stop(); }
    }
    private static void SelfSnapshot()
    {
        var captured = new List<int>(); var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { On = On<int>(("NEXT", new() { Target = ["b"] })) }),
            ("b", new() { Entry = [MachineActions.Assign<int>((_, _) => 1), MachineActions.SendTo<int>(args => args.Self, new MachineEvent("EVENT"))], On = On<int>(("EVENT", new() { Target = ["c"], Actions = [MachineActions.Effect<int>(args => captured.Add(((MachineSnapshot<int>)args.Self.GetSnapshot()).Context))] })) }), ("c", new())) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { actor.Send(new("NEXT")); actor.Send(new("EVENT")); Equal("1", string.Join(',', captured)); Results["selfSnapshot"] = captured; } finally { actor.Stop(); }
    }
    private static async Task Replaced(bool invoke)
    {
        var calls1 = 0; var calls2 = 0; var warnings = new List<string>(); var oldIds = new List<string>();
        var child1 = new StateMachine<int>(new() { On = On<int>(("PING", new() { Actions = [MachineActions.Effect<int>((_, _) => calls1++)] })) }, _ => 0);
        var child2 = new StateMachine<int>(new() { On = On<int>(("PING", new() { Actions = [MachineActions.Effect<int>((_, _) => calls2++)] })) }, _ => 0);
        var send = MachineActions.SendTo<int>("myChild", new MachineEvent("PING"), new() { Delay = MachineDelays.From<int>(1) });
        var states = States<int>(("a", new() { On = On<int>(("START", new() { Target = ["b"] })) }));
        if (invoke)
        {
            states["b"] = new() { Entry = [send], Invoke = [new() { Source = ActorSource.Named("child1"), Id = "myChild" }], On = On<int>(("NEXT", new() { Target = ["c"] })) };
            states["c"] = new() { Invoke = [new() { Source = ActorSource.Named("child2"), Id = "myChild" }] };
        }
        else states["b"] = new() { Entry = [MachineActions.SpawnChild<int>(ActorSource.Named("child1"), id: "myChild"), send, MachineActions.StopChild<int>("myChild"), MachineActions.SpawnChild<int>(ActorSource.Named("child2"), id: "myChild")] };
        var machine = new StateMachine<int>(new() { Initial = "a", States = states }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["child1"] = ActorSource.From(child1), ["child2"] = ActorSource.From(child2) });
        var clock = new RealClock(); var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Clock = clock, Warning = warnings.Add, Inspect = ev => { if (ev.Type == "@xstate.actor" && ev.ActorRef.Id == "myChild") oldIds.Add(ev.ActorRef.SessionId); } }); long timer = 0;
        try
        {
            ActorRuntime.Run(() => { actor.Start(); actor.Send(new("START")); if (invoke) actor.Send(new("NEXT")); timer = clock.SetTimeout(() => elapsed.TrySetResult(), 10); });
            await elapsed.Task.ConfigureAwait(false); Equal(0, calls1); Equal(0, calls2); Equal(2, oldIds.Count); Equal(1, warnings.Count);
            Equal($"Event \"PING\" was sent to stopped actor \"myChild ({oldIds[0]})\". This actor has already reached its final state, and will not transition.\nEvent: {{\"type\":\"PING\"}}", warnings[0]);
            Results[invoke ? "replacedInvoke" : "replacedSpawn"] = new { calls1, calls2, warnings = warnings.Select(value => value.Replace(oldIds[0], "<stopped>", StringComparison.Ordinal)).ToArray() };
        }
        finally { clock.ClearTimeout(timer); actor.Stop(); }
    }
    private static void StaticRaise()
    {
        var machine = new StateMachine<int>(new() { Initial = "a", States = States<int>(("a", new() { Entry = [MachineActions.Raise<int>(new MachineEvent("TO_B"))], On = On<int>(("TO_B", new() { Target = ["b"] })) }), ("b", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine).Start(); try { Equal("b", actor.GetSnapshot().Value.AtomicValue); Results["raiseStatic"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing."); } finally { actor.Stop(); }
    }
    private static void RaiseExpression(bool context)
    {
        var machine = new StateMachine<string>(new() { Initial = "a", States = States<string>(("a", new() { On = On<string>(("NEXT", new() { Actions = [MachineActions.Raise<string>(args => new(context ? args.Context : "RAISED"))] }), ("RAISED", new() { Target = ["b"] })) }), ("b", new())) }, _ => "RAISED");
        var actor = new Actor<MachineSnapshot<string>>(machine).Start(); try { actor.Send(new("NEXT")); Equal("b", actor.GetSnapshot().Value.AtomicValue); Results[context ? "raiseContext" : "raiseExpression"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("State missing."); } finally { actor.Stop(); }
    }
    private static void DirectReference()
    {
        var received = new List<MachineEvent>(); var target = new Actor<CallbackSnapshot>(new CallbackLogic(scope => { scope.Receive(received.Add); return null; })).Start();
        var ev = new MachineEvent("EVENT", 42); var clock = new SimulatedClock();
        var machine = new StateMachine<int>(new() { Entry = [MachineActions.SendTo<int>(target, ev), MachineActions.SendTo<int>(target, _ => ev, new() { Delay = MachineDelays.From<int>(10) })] }, _ => 0);
        var parent = new Actor<MachineSnapshot<int>>(machine, options: new() { Clock = clock }).Start();
        try
        {
            Equal(1, received.Count); Equal(true, ReferenceEquals(ev, received[0])); clock.Increment(10); Equal(2, received.Count); Equal(true, ReferenceEquals(ev, received[1]));
            var pure = ActorTransitions.Initial(machine); Equal(2, pure.Actions.Count); Equal(2, received.Count);
            foreach (var action in pure.Actions) { Equal("xstate.sendTo", action.Type); var parameters = action.Parameters as SendActionParameters ?? throw new InvalidOperationException("Send params missing."); Equal(true, ReferenceEquals(target, parameters.To)); Equal(true, ReferenceEquals(ev, parameters.Event)); }
        }
        finally { parent.Stop(); target.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("direct actor target overloads preserve event identity delay and pure metadata", () => { DirectReference(); return Task.CompletedTask; }));
        cases.Add(("send raise differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-send-raise.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
    }
}
