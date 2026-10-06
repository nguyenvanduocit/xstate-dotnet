using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
using static XStatePort.Tests.ObservableLogicTests;
namespace XStatePort.Tests;

internal static class ObservableInvocationTests
{
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    private static Dictionary<string, StateConfig<T>> States<T>(params (string Key, StateConfig<T> State)[] entries) => entries.ToDictionary(entry => entry.Key, entry => entry.State, StringComparer.Ordinal);
    private static Dictionary<string, IReadOnlyList<TransitionConfig<T>>> On<T>(string name, TransitionConfig<T> transition) => new(StringComparer.Ordinal) { [name] = [transition] };
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        foreach (var events in new[] { false, true })
        {
            var suite = events ? "with event observables" : "with observables"; var noun = events ? "event observable" : "observable";
            void Case(string name, Func<Task> run) => cases.Add(($"packages/core/test/invoke.test.ts::invoke > {suite} > {name}", run));
            Case("should work with an infinite " + noun, () => Stream(events, "infinite"));
            Case("should work with a finite " + noun, () => Stream(events, "finite"));
            Case("should receive an emitted error", () => Stream(events, "error"));
            Case("should work with input", () => Input(events));
        }
    }
    private static async Task Stream(bool events, string mode)
    {
        var limit = mode == "finite" ? (int?)5 : null;
        int Map(int value) => mode == "error" && value == 5 ? throw new InvalidOperationException("some error") : value;
        var source = events ? ActorSource.From(new EventObservableLogic(_ => Interval(value => new MachineEvent("COUNT", Map(value)), limit)))
            : ActorSource.From(new ObservableLogic<int>(_ => Interval(Map, limit)));
        var counts = new List<int?>(); var errors = new List<string>();
        var machine = new StateMachine<int?>(new() { Id = events || mode != "infinite" ? "obs" : "infiniteObs", Initial = "counting", States = States<int?>(
            ("counting", new()
            {
                Invoke = [new()
                {
                    Source = source,
                    OnSnapshot = events ? null : [new() { Actions = [MachineActions.Assign<int?>((_, ev) => ev.Payload is ActorSnapshotData { Snapshot: ObservableSnapshot<int> snapshot } ? snapshot.HasContext ? snapshot.Context : null : throw new InvalidOperationException("Snapshot missing."))] }],
                    OnDone = mode == "finite" ? [new() { Target = ["counted"], Guard = MachineGuards.Predicate<int?>((context, _) => context == 4) }] : [],
                    OnError = mode == "error" ? [new() { Target = ["success"], Guard = MachineGuards.Predicate<int?>((context, ev) =>
                    {
                        var error = ev.Payload is ActorErrorData data ? RequireException(data.Failure) : throw new InvalidOperationException("Error event missing.");
                        Equal("some error", error.Message); errors.Add(error.Message); return context == 4 && error.Message == "some error";
                    }) }] : []
                }],
                Always = mode == "infinite" ? [new() { Target = ["counted"], Guard = MachineGuards.Predicate<int?>((context, _) => context == 5) }] : [],
                On = events ? On<int?>("COUNT", new() { Actions = [MachineActions.Assign<int?>((_, ev) => ev.Payload is int value ? value : throw new InvalidOperationException("Count missing."))] }) : new Dictionary<string, IReadOnlyList<TransitionConfig<int?>>>(StringComparer.Ordinal)
            }),
            (mode == "error" ? "success" : "counted", new() { Kind = StateKind.Final })) }, _ => null);
        var actor = new Actor<MachineSnapshot<int?>>(machine); var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = actor.Subscribe(snapshot => counts.Add(snapshot.Context), error => completed.TrySetException(ActorErrors.ToException(error)), () => completed.TrySetResult());
        try { actor.Start(); await completed.Task.ConfigureAwait(false); Observations[$"stream:{events}:{mode}"] = new { state = actor.GetSnapshot().Value.AtomicValue, count = actor.GetSnapshot().Context, counts, errors }; }
        finally { actor.Stop(); }
    }
    private static async Task Input(bool events)
    {
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = events ? ActorSource.From(new EventObservableLogic(scope => Of(new MachineEvent("obs.event", scope.Input))))
            : ActorSource.From(new ObservableLogic<int>(scope => Of(scope.Input is int input ? input : throw new InvalidOperationException("Input missing."))));
        var machine = new StateMachine<int>(new()
        {
            Invoke = [new() { Source = events ? source : ActorSource.Named("childLogic"), Input = _ => 42, OnSnapshot = events ? null : [new() { Actions = [MachineActions.Effect<int>((_, ev) =>
            {
                if (ev.Payload is ActorSnapshotData { Snapshot: ObservableSnapshot<int> { Status: SnapshotStatus.Active, HasContext: true } snapshot } && snapshot.Context == 42) received.TrySetResult(42);
            })] }] }],
            On = events ? On<int>("obs.event", new() { Actions = [MachineActions.Effect<int>((_, ev) =>
            {
                if (ev.Payload is not int value) throw new InvalidOperationException("Input event missing."); Equal(42, value); received.TrySetResult(value);
            })] }) : new Dictionary<string, IReadOnlyList<TransitionConfig<int>>>(StringComparer.Ordinal)
        }, _ => 0, actors: events ? null : new Dictionary<string, ActorSource>(StringComparer.Ordinal) { ["childLogic"] = source });
        var actor = new Actor<MachineSnapshot<int>>(machine); using var subscription = actor.Subscribe(onError: error => received.TrySetException(ActorErrors.ToException(error)));
        try { actor.Start(); Observations[$"input:{events}"] = await received.Task.ConfigureAwait(false); }
        finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases) => cases.Add(("observable invocation differential observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-observable-invocation.json", JsonSerializer.Serialize(Observations)); return Task.CompletedTask; }));
}
