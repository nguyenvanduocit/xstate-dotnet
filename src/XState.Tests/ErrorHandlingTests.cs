using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ErrorHandlingTests
{
    internal sealed class Reports : IUnhandledErrorReporter
    {
        internal List<object?> Values { get; } = [];
        internal TaskCompletionSource<object?> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(object? failure) { Values.Add(failure); First.TrySetResult(failure); }
    }
    private static Dictionary<string, IReadOnlyList<TransitionConfig<int>>> On(string key, TransitionConfig<int> transition) => new(StringComparer.Ordinal) { [key] = [transition] };
    private static StateMachine<int> Parent(ActorSource child, bool handled = false, bool done = true, MachineAction<int>? action = null)
    {
        var states = new Dictionary<string, StateConfig<int>>(StringComparer.Ordinal)
        {
            ["pending"] = new() { Invoke = [new() { Source = child, OnError = handled ? [new() { Target = ["failed"] }] : [], OnDone = !handled && done ? [new() { Target = ["success"] }] : [] }] }
        };
        if (handled) states["failed"] = action is null ? new() { Kind = StateKind.Final } : new() { On = On("do", new() { Actions = [action] }) };
        else if (done) states["success"] = new() { Kind = StateKind.Final };
        return new(new() { Initial = "pending", States = states }, _ => 0);
    }
    private static ActorSource Failing(string message) => ActorSource.From(new CallbackLogic(_ => throw new InvalidOperationException(message)));
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string name, Func<Task> run) => cases.Add(("packages/core/test/errors.test.ts::error handling > " + name, run));
        Case("unhandled sync errors thrown when starting a child actor should be reported globally", () => GlobalSync(false));
        Case("unhandled rejection of a promise actor should be reported globally in absence of error listener", () => PromisePropagation(0));
        Case("unhandled rejection of a promise actor should be reported to the existing error listener of its parent", () => PromisePropagation(1));
        Case("unhandled rejection of a promise actor should be reported to the existing error listener of its grandparent", () => PromisePropagation(2));
        Case("handled sync errors thrown when starting a child actor should not be reported globally", () => ChildObservers(0, true));
        Case("handled sync errors thrown when starting a child actor should be reported globally when not all of its own observers come with an error listener", () => ChildObservers(1, true));
        Case("handled sync errors thrown when starting a child actor should not be reported globally when all of its own observers come with an error listener", () => ChildObservers(2, true));
        Case("unhandled sync errors thrown when starting a child actor should be reported twice globally when not all of its own observers come with an error listener and when the root has no error listener of its own", () => ChildObservers(1, false));
        Case("handled sync errors shouldn't notify the error listener", () => RootListener(true, false));
        Case("unhandled sync errors should notify the root error listener", () => RootListener(false, false));
        Case("unhandled sync errors should not notify the global listener when the root error listener is present", () => RootListener(false, true));
        Case("handled sync errors thrown when starting an actor shouldn't crash the parent", HandledParent);
        Case("unhandled sync errors thrown when starting an actor should crash the parent", () => GlobalSync(true));
        Case("error thrown by the error listener should be reported globally", () => ObserverFailure(true, false));
        Case("error should be reported globally if not every observer comes with an error listener", () => ObserverFailure(false, true));
        Case("uncaught error and an error thrown by the error listener should both be reported globally when not every observer comes with an error listener", () => ObserverFailure(true, true));
        Case("should error the parent on errored initial state of a child", InitialError);
        Case("actor continues to work normally after emit callback errors", EmitError);
    }
    private static async Task GlobalSync(bool status)
    {
        var reports = new Reports(); var actor = new Actor<MachineSnapshot<int>>(Parent(Failing("unhandled_sync_error_in_actor_start"), done: !status), options: new() { ErrorReporter = reports }).Start();
        if (status) Equal(SnapshotStatus.Error, actor.GetSnapshot().Status);
        var error = await reports.First.Task.ConfigureAwait(false); Equal("unhandled_sync_error_in_actor_start", RequireException(error).Message); actor.Stop();
    }
    private static async Task PromisePropagation(int depth)
    {
        var suffix = depth switch { 0 => "without_error_listener", 1 => "with_parent_listener", _ => "with_grandparent_listener" };
        var original = new InvalidOperationException("unhandled_rejection_in_promise_actor_" + suffix); var reports = new Reports(); var errors = new List<object?>(); var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = Parent(ActorSource.From(new PromiseLogic<int>(_ => Task.FromException<int>(original)))); if (depth == 2) machine = Parent(ActorSource.From(machine));
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { ErrorReporter = reports });
        using var subscription = depth == 0 ? null : actor.Subscribe(onError: error => { errors.Add(error); received.TrySetResult(); }); actor.Start();
        if (depth == 0) Equal(true, ReferenceEquals(original, await reports.First.Task.ConfigureAwait(false)));
        else { await received.Task.ConfigureAwait(false); await Task.Delay(1).ConfigureAwait(false); Equal(1, errors.Count); Equal(true, ReferenceEquals(original, errors[0])); Equal(original.Message, RequireException(errors[0]).Message); }
        Observations["promise" + depth] = new { reported = reports.Values.Select(value => RequireException(value).Message).ToArray(), observed = errors.Select(value => RequireException(value).Message).ToArray() }; actor.Stop();
    }
    private static async Task ChildObservers(int listeners, bool handled)
    {
        const string message = "handled_sync_error_in_actor_start"; var reports = new Reports();
        var actor = new Actor<MachineSnapshot<int>>(Parent(Failing(message), handled, done: false), options: new() { ErrorReporter = reports });
        var child = actor.GetSnapshot().Children.Values.Single() ?? throw new InvalidOperationException("Child missing.");
        using var first = listeners == 0 ? null : child.Subscribe(onError: _ => { });
        using var second = listeners == 0 ? null : listeners == 2 ? child.Subscribe(onError: _ => { }) : child.Subscribe(_ => { });
        actor.Start(); await Task.Delay(10).ConfigureAwait(false);
        var expected = !handled ? 2 : listeners == 1 ? 1 : 0; Equal(expected, reports.Values.Count); Equal(true, reports.Values.All(value => RequireException(value).Message == message));
        Observations[$"child{listeners}:{handled}"] = reports.Values.Select(value => RequireException(value).Message).ToArray(); actor.Stop();
    }
    private static async Task RootListener(bool handled, bool wait)
    {
        var message = handled ? "handled_sync_error_in_actor_start" : "unhandled_sync_error_in_actor_start_with_root_error_listener"; var reports = new Reports(); var errors = new List<object?>();
        var actor = new Actor<MachineSnapshot<int>>(Parent(Failing(message), handled), options: new() { ErrorReporter = reports }); using var subscription = actor.Subscribe(onError: errors.Add); actor.Start();
        Equal(handled ? 0 : 1, errors.Count); if (!handled) Equal(message, RequireException(errors[0]).Message);
        if (wait) { await Task.Delay(10).ConfigureAwait(false); Equal(0, reports.Values.Count); } actor.Stop();
    }
    private static Task HandledParent()
    {
        var calls = 0; var reports = new Reports(); var actor = new Actor<MachineSnapshot<int>>(Parent(Failing("handled_sync_error_in_actor_start"), true, action: MachineActions.Effect<int>((_, _) => calls++)), options: new() { ErrorReporter = reports }).Start();
        Equal(SnapshotStatus.Active, actor.GetSnapshot().Status); actor.Send(new("do")); Equal(1, calls); Equal(0, reports.Values.Count); actor.Stop(); return Task.CompletedTask;
    }
    private static Task ObserverFailure(bool throws, bool missing)
    {
        var originalMessage = missing ? "error_thrown_when_not_every_observer_comes_with_an_error_listener" : "handled_sync_error_in_actor_start";
        var reports = new Reports(); var actor = new Actor<MachineSnapshot<int>>(Parent(Failing(originalMessage), done: false), options: new() { ErrorReporter = reports });
        using var first = actor.Subscribe(onError: _ => { if (throws) throw new InvalidOperationException("error_thrown_by_error_listener"); }); using var second = missing ? actor.Subscribe(_ => { }) : null;
        actor.Start(); var expected = new List<string>(); if (throws) expected.Add("error_thrown_by_error_listener"); if (missing) expected.Add(originalMessage);
        Equal(string.Join(',', expected), string.Join(',', reports.Values.Select(value => RequireException(value).Message)));
        Observations[$"observer{throws}:{missing}"] = reports.Values.Select(value => RequireException(value).Message).ToArray(); actor.Stop(); return Task.CompletedTask;
    }
    private sealed record FailedSnapshot(object? Failure) : IActorSnapshot { public SnapshotStatus Status => SnapshotStatus.Error; public object? Output => null; }
    private sealed class InitialFailure : IActorLogic<FailedSnapshot>
    {
        public FailedSnapshot GetInitialSnapshot(ActorScope<FailedSnapshot> scope, object? input) => new("immediate error!");
        public FailedSnapshot Transition(FailedSnapshot snapshot, MachineEvent ev, ActorScope<FailedSnapshot> scope) => snapshot;
        public FailedSnapshot GetErrorSnapshot(FailedSnapshot? previous, Exception exception) => new(ActorErrors.GetValue(exception));
        public object GetPersistedSnapshot(FailedSnapshot snapshot) => snapshot;
    }
    private static Task InitialError()
    {
        var machine = new StateMachine<int>(new() { Invoke = [new() { Source = ActorSource.Named("failure") }] }, _ => 0, actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["failure"] = ActorSource.From(new InitialFailure()) });
        var actor = new Actor<MachineSnapshot<int>>(machine); using var subscription = actor.Subscribe(onError: _ => { }); actor.Start(); Equal(SnapshotStatus.Error, actor.GetSnapshot().Status); Equal<object?>("immediate error!", actor.GetSnapshot().Failure); actor.Stop(); return Task.CompletedTask;
    }
    private static async Task EmitError()
    {
        var reports = new Reports(); var actor = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { On = On("someEvent", new() { Actions = [MachineActions.Emit<int>(new MachineEvent("emitted", "bar"))] }) }, _ => 0), options: new() { ErrorReporter = reports }).Start();
        var thrown = false; using var first = actor.On("emitted", _ => { thrown = true; throw new InvalidOperationException("oops"); }); actor.Send(new("someEvent")); await Task.Delay(10).ConfigureAwait(false);
        Equal(true, thrown); Equal(SnapshotStatus.Active, actor.GetSnapshot().Status); var received = new TaskCompletionSource<MachineEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var second = actor.On("emitted", ev => received.TrySetResult(ev)); actor.Send(new("someEvent")); Equal<object?>("bar", (await received.Task.ConfigureAwait(false)).Payload); Equal(SnapshotStatus.Active, actor.GetSnapshot().Status); actor.Stop();
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("error reporting routing observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-error-routing.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask; }));
}
