using System.Diagnostics;
using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ForwardingTests
{
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] entries) => entries.ToDictionary(entry => entry.Key, entry => entry.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(params (string Key, TransitionConfig<T> Transition)[] entries) => entries.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<TransitionConfig<T>>)[entry.Transition], StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Func<Task> run) => cases.Add(("packages/core/test/actions.test.ts::forwardTo() > " + name, run));
        Case("should forward an event to a service", () => Forward(false));
        Case("should forward an event to a service (dynamic)", () => Forward(true));
        Case("should not cause an infinite loop when forwarding to undefined", () => { Undefined(null); return Task.CompletedTask; });
    }
    private static async Task Forward(bool dynamic)
    {
        var child = new StateMachine<int>(new() { Id = "child", Initial = "active", States = States<int>(("active", new() { On = On<int>(("EVENT", new() { Guard = MachineGuards.Predicate<int>((_, ev) => ev.Payload is 42), Actions = [MachineActions.SendParent<int>(_ => new("SUCCESS"))] })) })) }, _ => 0);
        var machine = new StateMachine<IActor?>(new() { Id = "parent", Initial = "first", States = States<IActor?>(
            ("first", new() { Entry = dynamic ? [MachineActions.Assign<IActor?>(args => args.Spawn(child, id: "x"))] : [], Invoke = dynamic ? [] : [new() { Id = "myChild", Source = ActorSource.From(child) }], On = On<IActor?>(("EVENT", new() { Actions = [dynamic ? MachineActions.ForwardTo<IActor?>(args => args.Context) : MachineActions.ForwardTo<IActor?>("myChild")] }), ("SUCCESS", new() { Target = ["last"] })) }),
            ("last", new() { Kind = StateKind.Final })) }, _ => null);
        var actor = new Actor<MachineSnapshot<IActor?>>(machine); var complete = ActorTasks.ToPromiseAsync(actor);
        try { actor.Start(); actor.Send(new("EVENT", 42)); await complete.ConfigureAwait(false); Observations[$"forward:{dynamic}"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing."); }
        finally { actor.Stop(); }
    }
    private static void Undefined(string? target)
    {
        var machine = new StateMachine<int>(new() { On = On<int>(("*", new() { Guard = MachineGuards.Predicate<int>((_, _) => true), Actions = [MachineActions.ForwardTo<int>(target)] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); var errors = new List<object?>(); using var subscription = actor.Subscribe(onError: errors.Add); actor.Start(); actor.Send(new("TEST"));
        Equal(1, errors.Count); Equal("Attempted to forward event to undefined actor. This risks an infinite loop in the sender.", RequireException(errors[0]).Message); Observations[target is null ? "undefined" : "empty"] = RequireException(errors[0]).Message; actor.Stop();
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("string forwarding retains event identity delay cancellation and executable metadata", () => { Delayed(); return Task.CompletedTask; }));
        cases.Add(("empty forwarding target fails at event resolution without a self loop", () => { Undefined(""); return Task.CompletedTask; }));
        cases.Add(("forwarding differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-forwarding.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask; }));
    }
    private static void Delayed()
    {
        var delivered = new List<MachineEvent>(); var inspected = new List<SendActionParameters>(); var clock = new SimulatedClock();
        var machine = new StateMachine<int>(new() { Invoke = [new() { Id = "child", Source = ActorSource.From(new CallbackLogic(scope => { scope.Receive(delivered.Add); return null; })) }],
            On = On<int>(("GO", new() { Actions = [MachineActions.ForwardTo<int>("child", new() { Id = "forwarded", Delay = MachineDelays.From<int>(10) })] }), ("CANCEL", new() { Actions = [MachineActions.Cancel<int>("forwarded")] })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Clock = clock, Inspect = ev => { if (ev.Action is { Type: "xstate.sendTo", Parameters: SendActionParameters parameters }) inspected.Add(parameters); } }).Start();
        var payload = new object(); var original = new MachineEvent("GO", payload); actor.Send(original); Equal(0, delivered.Count); clock.Increment(9); Equal(0, delivered.Count); clock.Increment(1); Equal(1, delivered.Count); Equal(true, ReferenceEquals(original, delivered[0]));
        var send = inspected.Single(); Equal(true, ReferenceEquals(original, send.Event)); Equal("forwarded", send.Id); Equal<double?>(10, send.Delay); Equal(true, ReferenceEquals(actor.GetSnapshot().Children["child"], send.To));
        actor.Send(original); actor.Send(new("CANCEL")); clock.Increment(10); Equal(1, delivered.Count);
        var initial = ActorTransitions.Initial(machine); var result = ActorTransitions.Next(machine, initial.Snapshot, original); var pure = result.Actions.Single(); Equal("xstate.sendTo", pure.Type);
        var parameters = pure.Parameters as SendActionParameters ?? throw new InvalidOperationException("Send parameters missing."); Equal(true, ReferenceEquals(original, parameters.Event)); Equal("child", parameters.TargetId); Equal<double?>(10, parameters.Delay); Equal("forwarded", parameters.Id);
        Observations["delayed"] = new { delivered = delivered.Count, delay = send.Delay, id = send.Id, sameEvent = ReferenceEquals(original, delivered[0]), actionType = pure.Type }; actor.Stop();
    }
    public static int Benchmark(string destination)
    {
        const int samples = 20000; var output = new List<object>();
        foreach (var forward in new[] { false, true })
        {
            var received = 0; var ev = new MachineEvent("GO", 42);
            var action = forward ? MachineActions.ForwardTo<int>("child") : MachineActions.SendTo<int>("child", args => args.Event);
            var machine = new StateMachine<int>(new() { Invoke = [new() { Id = "child", Source = ActorSource.From(new CallbackLogic(scope => { scope.Receive(value => { if (!ReferenceEquals(value, ev)) throw new InvalidOperationException("Event changed."); received++; }); return null; })) }], On = On<int>(("GO", new() { Actions = [action] })) }, _ => 0);
            var actor = new Actor<MachineSnapshot<int>>(machine).Start(); for (var i = 0; i < 3000; i++) actor.Send(ev); received = 0; var timings = new long[samples];
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray(); var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < samples; i++) { var start = Stopwatch.GetTimestamp(); actor.Send(ev); timings[i] = Stopwatch.GetTimestamp() - start; }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; Equal(samples, received); Array.Sort(timings);
            output.Add(new { forward, samples, allocatedBytesPerEvent = (double)allocated / samples, p95Microseconds = timings[(int)(samples * .95)] * 1_000_000.0 / Stopwatch.Frequency, p99Microseconds = timings[(int)(samples * .99)] * 1_000_000.0 / Stopwatch.Frequency, gc = collections.Select((count, gen) => GC.CollectionCount(gen) - count).ToArray() }); actor.Stop();
        }
        var json = JsonSerializer.Serialize(output); File.WriteAllText(destination, json); Console.WriteLine(json); return 0;
    }
}
