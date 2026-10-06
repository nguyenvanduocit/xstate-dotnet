using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class PromiseInvocationTests
{
    // Upstream wraps a native Promise in a thenable. Keep the same producer/reaction separation.
    private sealed class Thenable(Task<object?> task) : IPromiseLike<object?>
    {
        internal Task? Observation { get; private set; }
        public PromiseThen<object?> ThenHandler => resolver => Observation = Observe(resolver);
        private async Task Observe(PromiseResolver<object?> resolver)
        {
            object? value;
            try { value = await task.ConfigureAwait(false); }
            catch (Exception error) { resolver.Reject(ActorErrors.GetValue(error)); return; }
            resolver.Resolve(value);
        }
    }
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    private static PromiseLogic<object?> Logic(bool thenable, Func<PromiseScope<object?>, Task<object?>> creator) => thenable
        ? PromiseActors.FromPromiseLike<object?>(scope => new Thenable(creator(scope))) : new(creator);
    private static Task<object?> Promise(Action<Action<object?>, Action<object?>> executor)
    {
        var completion = new TaskCompletionSource<object?>();
        try { executor(value => completion.TrySetResult(value), reason => completion.TrySetException(ActorErrors.ToException(reason))); }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] entries) => entries.ToDictionary(entry => entry.Key, entry => entry.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(string name, TransitionConfig<T> transition) => new(StringComparer.Ordinal) { [name] = [transition] };
    private static Dictionary<string, ActorSource> Sources(ActorSource source) => new(StringComparer.Ordinal) { ["somePromise"] = source };
    private static async Task Complete<T>(Actor<MachineSnapshot<T>> actor, Action? afterStart = null, Action? onComplete = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = actor.Subscribe(onError: error => completion.TrySetException(ActorErrors.ToException(error)), onComplete: () =>
        {
            try { onComplete?.Invoke(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        try { ActorRuntime.Run(() => { actor.Start(); afterStart?.Invoke(); }); await completion.Task.ConfigureAwait(false); }
        finally { actor.Stop(); }
    }
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        foreach (var thenable in new[] { false, true })
        {
            var kind = thenable ? "PromiseLike" : "Promise";
            void Case(string name, Func<Task> run) => cases.Add(($"packages/core/test/invoke.test.ts::invoke > with promises ({kind}) > {name}", run));
            Case("should be invoked with a promise factory and resolve through onDone", () => Resolve(thenable, false, false));
            Case("should be invoked with a promise factory and reject with ErrorExecution", () => Handled(thenable));
            Case("should be invoked with a promise factory and surface any unhandled errors", () => Unhandled(thenable, false));
            Case("should be invoked with a promise factory and stop on unhandled onError target", () => Unhandled(thenable, true));
            Case("should be invoked with a promise factory and resolve through onDone for compound state nodes", () => Resolve(thenable, true, false));
            Case("should be invoked with a promise service and resolve through onDone for compound state nodes", () => Resolve(thenable, true, true));
            Case("should assign the resolved data when invoked with a promise factory", () => Output(thenable, false, true));
            Case("should assign the resolved data when invoked with a promise service", () => Output(thenable, true, true));
            Case("should provide the resolved data when invoked with a promise factory", () => Output(thenable, false, false));
            Case("should provide the resolved data when invoked with a promise service", () => Output(thenable, true, false));
            Case("should be able to specify a Promise as a service", () => Input(thenable));
            Case("should be able to reuse the same promise logic multiple times and create unique promise for each created actor", () => Reuse(thenable));
            Case("should not emit onSnapshot if stopped", () => Stopped(thenable));
        }
    }
    private static async Task Resolve(bool thenable, bool compound, bool named)
    {
        var source = ActorSource.From(Logic(thenable, _ => Promise((resolve, _) => resolve(null))));
        var config = new StateConfig<int> { Initial = "pending", States = States<int>(
            ("pending", new() { Invoke = [new() { Source = named ? ActorSource.Named("somePromise") : source, OnDone = [new() { Target = ["success"] }] }] }),
            ("success", new() { Kind = StateKind.Final })) };
        if (compound) config = new() { Id = "promise", Initial = "parent", States = States<int>(("parent", new() { Initial = "pending", States = config.States, OnDone = [new() { Target = ["success"] }] }), ("success", new() { Kind = StateKind.Final })) };
        var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(config, _ => 0, actors: named ? Sources(source) : null));
        await Complete(actor).ConfigureAwait(false); Equal("success", actor.GetSnapshot().Value.AtomicValue);
        Observations[$"resolve:{thenable}:{compound}:{named}"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private sealed record Request(int Id = 42, bool Succeed = true);
    private static async Task Handled(bool thenable)
    {
        var source = ActorSource.From(Logic(thenable, scope => Promise((resolve, _) =>
        {
            var input = scope.Input as Request ?? throw new InvalidOperationException("Input missing.");
            if (input.Succeed) resolve(input.Id); else throw new InvalidOperationException($"failed on purpose for: {input.Id}");
        })));
        var machine = new StateMachine<Request>(new() { Id = "invokePromise", Initial = "pending", States = States<Request>(
            ("pending", new() { Invoke = [new() { Source = source, Input = args => args.Context, OnDone = [new() { Target = ["success"], Guard = MachineGuards.Predicate<Request>((context, ev) => ev.Payload is ActorDoneData data && Equals(data.Output, context.Id)) }], OnError = [new() { Target = ["failure"] }] }] }),
            ("success", new() { Kind = StateKind.Final }), ("failure", new() { Kind = StateKind.Final })) }, args => args.Input as Request ?? new());
        var actor = new Actor<MachineSnapshot<Request>>(machine, new Request(31, false)); await Complete(actor).ConfigureAwait(false);
        Equal("failure", actor.GetSnapshot().Value.AtomicValue); Observations[$"handled:{thenable}"] = "failure";
    }
    private static async Task Unhandled(bool thenable, bool checkCompletion)
    {
        var source = ActorSource.From(Logic(thenable, _ => Promise((_, _) => throw new InvalidOperationException("test"))));
        var machine = new StateMachine<int>(new() { Id = "invokePromise", Initial = "pending", States = States<int>(
            ("pending", new() { Invoke = [new() { Source = source, OnDone = [new() { Target = ["success"] }] }] }), ("success", new() { Kind = StateKind.Final })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); var errorReceived = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously); var completed = 0;
        using var subscription = actor.Subscribe(onError: error => errorReceived.TrySetResult(error), onComplete: () => completed++);
        try
        {
            actor.Start(); var error = RequireException(await errorReceived.Task.ConfigureAwait(false));
            if (checkCompletion) { Equal("test", error.Message); Equal(0, completed); } else Equal(true, error.Message.Contains("test", StringComparison.Ordinal));
            Observations[$"unhandled:{thenable}:{checkCompletion}"] = new { error = error.Message, completed };
        }
        finally { actor.Stop(); }
    }
    private sealed record Count(int Value);
    private static async Task Output(bool thenable, bool named, bool assign)
    {
        var source = ActorSource.From(Logic(thenable, _ => Promise((resolve, _) => resolve(new Count(1))))); var observed = 0;
        static Count Read(MachineEvent ev) => ev.Payload is ActorDoneData { Output: Count count } ? count : throw new InvalidOperationException("Output missing.");
        var action = assign ? MachineActions.Assign<Count>((_, ev) => Read(ev)) : MachineActions.Effect<Count>((_, ev) => observed = Read(ev).Value);
        var actor = new Actor<MachineSnapshot<Count>>(new StateMachine<Count>(new() { Id = "promise", Initial = "pending", States = States<Count>(
            ("pending", new() { Invoke = [new() { Source = named ? ActorSource.Named("somePromise") : source, OnDone = [new() { Target = ["success"], Actions = [action] }] }] }),
            ("success", new() { Kind = StateKind.Final })) }, _ => new(0), actors: named ? Sources(source) : null));
        await Complete(actor, onComplete: () => Equal(1, assign ? actor.GetSnapshot().Context.Value : observed)).ConfigureAwait(false);
        Observations[$"output:{thenable}:{named}:{assign}"] = new { context = actor.GetSnapshot().Context.Value, observed };
    }
    private sealed record PromiseInput(bool Foo, MachineEvent Event);
    private static async Task Input(bool thenable)
    {
        var source = ActorSource.From(Logic(thenable, scope => Promise((resolve, reject) =>
        {
            var input = scope.Input as PromiseInput ?? throw new InvalidOperationException("Input missing.");
            if (input.Foo && input.Event.Payload is true) resolve(null); else reject(null);
        })));
        var machine = new StateMachine<bool>(new() { Id = "promise", Initial = "pending", States = States<bool>(
            ("pending", new() { On = On<bool>("BEGIN", new() { Target = ["first"] }) }),
            ("first", new() { Invoke = [new() { Source = ActorSource.Named("somePromise"), Input = args => new PromiseInput(args.Context, args.Event), OnDone = [new() { Target = ["last"] }] }] }),
            ("last", new() { Kind = StateKind.Final })) }, _ => true, actors: Sources(source));
        var actor = new Actor<MachineSnapshot<bool>>(machine); await Complete(actor, () => actor.Send(new("BEGIN", true))).ConfigureAwait(false);
        Observations[$"input:{thenable}"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private sealed record Results(double? First, double? Second);
    private sealed record RandomResult(double Value);
    private static async Task Reuse(bool thenable)
    {
        var source = ActorSource.From(Logic(thenable, _ => Promise((resolve, _) => resolve(new RandomResult(Random.Shared.NextDouble())))));
        StateConfig<Results> Region(bool first) => new() { Initial = "active", States = States<Results>(
            ("active", new() { Invoke = [new() { Source = ActorSource.Named("getRandomNumber"), OnDone = [new() { Target = ["success"], Actions = [MachineActions.Assign<Results>((context, ev) =>
            {
                var value = ev.Payload is ActorDoneData { Output: RandomResult result } ? result.Value : throw new InvalidOperationException("Output missing.");
                return first ? context with { First = value } : context with { Second = value };
            })] }] }] }), ("success", new() { Kind = StateKind.Final })) };
        var machine = new StateMachine<Results>(new() { Initial = "pending", States = States<Results>(
            ("pending", new() { Kind = StateKind.Parallel, States = States(("state1", Region(true)), ("state2", Region(false))), OnDone = [new() { Target = ["done"] }] }),
            ("done", new() { Kind = StateKind.Final })) }, _ => new(null, null), actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["getRandomNumber"] = source });
        var actor = new Actor<MachineSnapshot<Results>>(machine);
        await Complete(actor, onComplete: () => { var snapshot = actor.GetSnapshot(); Equal(true, snapshot.Context.First.HasValue); Equal(true, snapshot.Context.Second.HasValue); Equal(false, snapshot.Context.First == snapshot.Context.Second); }).ConfigureAwait(false);
        Observations[$"reuse:{thenable}"] = actor.GetSnapshot().Value.AtomicValue ?? throw new InvalidOperationException("Final state missing.");
    }
    private static async Task Stopped(bool thenable)
    {
        var producer = new TaskCompletionSource<object?>();
        var source = ActorSource.From(Logic(thenable, _ => Promise((resolve, _) => new RealClock().SetTimeout(() => { resolve(42); producer.TrySetResult(null); }, 5))));
        var received = new List<string>(); var errors = new List<object?>();
        var machine = new StateMachine<int>(new() { Initial = "active", States = States<int>(
            ("active", new() { Invoke = [new() { Source = source, OnSnapshot = [new()] }], On = On<int>("deactivate", new() { Target = ["inactive"] }) }),
            ("inactive", new() { On = On<int>("*", new() { Actions = [MachineActions.Effect<int>((_, ev) => { received.Add(ev.Type); if (ev.Payload is ActorSnapshotData) throw new InvalidOperationException("Unexpected snapshot event."); })] }) })) }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine); using var subscription = actor.Subscribe(onError: errors.Add);
        try
        {
            ActorRuntime.Run(() => { actor.Start(); actor.Send(new("deactivate")); }); await Task.Delay(10).ConfigureAwait(false); await producer.Task.ConfigureAwait(false); await ActorRuntime.YieldAsync().ConfigureAwait(false);
            Equal(0, errors.Count); Equal(0, received.Count); Equal("inactive", actor.GetSnapshot().Value.AtomicValue); Observations[$"stopped:{thenable}"] = received;
        }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("promise invocation differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-promise-invocation.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask; }));
}
