using System.Diagnostics;
using System.Text.Json;
using XState;
using XState.Graph;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static partial class TestModelTests
{
    private static readonly Dictionary<string, object> Observations = new(StringComparer.Ordinal);
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        void Sync(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        cases.Add(("test model awaits event before all matching state callbacks and returns successful outcomes", Ordering));
        cases.Add(("test model callback failures stop execution and retain original exception with formatted path", Failures));
        Sync("test model merges only provided traversal options including explicit null and Infinity", Options);
        Sync("test model factory expands only active event descriptors and per-call events replace that provider", EventProviders);
        Sync("test model validates numeric inline delays without executing initialization or resolving dynamic delays", Validation);
        cases.Add(("test model paths retain the model and observe later options changes", MutableModel));
        Sync("test model deduplicates by serialized event prefixes independently of configured serializer", Prefixes);
        Sync("test model formats metadata descriptions on final and parallel states", Metadata);
        Sync("test model exports native outcomes for pinned JS differential", () => File.WriteAllText("tmp/xstate-parity/csharp-test-model.json", JsonSerializer.Serialize(Observations)));
    }
    private static async Task Ordering()
    {
        var model = StateGraph.CreateTestModel(Machine(("a", Node(("GO", "b"))), ("b", Node())));
        var path = model.GetShortestPaths()[0]; var trace = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parameters = new TestParameters<MachineSnapshot<Empty>>
        {
            Events = new Dictionary<string, Func<GraphStep<MachineSnapshot<Empty>>, Task>>
            {
                ["xstate.init"] = async _ => { trace.Add("init:before"); await release.Task.ConfigureAwait(false); trace.Add("init:after"); },
                ["GO"] = async _ => { trace.Add("go:before"); await Task.Yield(); trace.Add("go:after"); }
            },
            States = new Dictionary<string, Func<MachineSnapshot<Empty>, Task>>
            {
                ["a"] = state => { trace.Add(state.Value.AtomicValue ?? "missing"); return Task.CompletedTask; },
                ["b"] = state => { trace.Add(state.Value.AtomicValue ?? "missing"); return Task.CompletedTask; },
                ["*"] = _ => { trace.Add("fallback"); return Task.CompletedTask; }
            }
        };
        var task = path.TestAsync(parameters); Equal(false, task.IsCompleted); Equal(true, trace.SequenceEqual(["init:before"])); release.SetResult();
        var result = await task.ConfigureAwait(false);
        Equal(true, trace.SequenceEqual(["init:before", "init:after", "a", "go:before", "go:after", "b"]));
        Equal(2, result.Steps.Count); Equal(true, result.State.Error is null && result.Steps.All(step => step.State.Error is null && step.Event.Error is null));
        Observations["ordering"] = trace;
    }
    private static async Task Failures()
    {
        var rows = new List<object>();
        foreach (var eventError in new[] { true, false })
        {
            var model = StateGraph.CreateTestModel(Machine(("a", Node(("GO", "b"))), ("b", Node(("NEXT", "c"))), ("c", Node())));
            var path = model.GetShortestPaths()[0]; var trace = new List<string>(); var original = new InvalidOperationException("callback failed");
            var parameters = new TestParameters<MachineSnapshot<Empty>>
            {
                Events = new Dictionary<string, Func<GraphStep<MachineSnapshot<Empty>>, Task>> { ["GO"] = _ => { trace.Add("GO"); return eventError ? Task.FromException(original) : Task.CompletedTask; } },
                States = new Dictionary<string, Func<MachineSnapshot<Empty>, Task>> { ["*"] = state => { trace.Add(state.Value.AtomicValue ?? "missing"); return state.Matches("b") ? Task.FromException(original) : Task.CompletedTask; } }
            };
            try { await path.TestAsync(parameters).ConfigureAwait(false); throw new InvalidOperationException("Missing callback failure."); }
            catch (PathTestException error)
            {
                Equal(true, ReferenceEquals(original, error.InnerException)); Equal("callback failed", original.Message);
                Equal(true, trace.SequenceEqual(eventError ? ["a", "GO"] : ["a", "GO", "b"]));
                rows.Add(new { eventError, trace, message = error.Message });
            }
        }
        Observations["failures"] = rows;
    }
    private static void Options()
    {
        var logic = new TransitionLogic<int>((value, _, _) => value + 1, 0);
        var model = new TestModel<TransitionSnapshot<int>>(logic, new() { Limit = 5, Input = "model", StopWhen = _ => true });
        model.DefaultTraversalOptions = new() { Limit = 2, Input = "defaults", ToState = _ => false };
        var seen = new List<object>();
        model.GetPaths((_, options) => { Equal(5d, options.Limit); Equal("model", options.Input); Equal(true, options.ToState is not null); seen.Add(new { limit = options.Limit, input = options.Input }); return []; });
        model.GetPaths((_, options) => { Equal(double.PositiveInfinity, options.Limit); Equal<object?>(null, options.Input); Equal(true, options.StopWhen is null); seen.Add(new { limit = "Infinity", input = options.Input }); return []; }, new() { Limit = double.PositiveInfinity, Input = null, StopWhen = null });
        Observations["options"] = seen;
    }
    private static void EventProviders()
    {
        var calls = new List<string>();
        var model = StateGraph.CreateTestModel(Machine(("a", Node(("GO", "b"))), ("b", Node())), new()
        {
            Events = new(state => { calls.Add(state.Value.AtomicValue ?? "missing"); return [new("GO", new ValueEvent(1)), new("GO", new ValueEvent(2)), new("UNKNOWN")]; })
        });
        var generated = model.GetShortestPaths(); Equal(2, generated.Count);
        var overridden = model.GetShortestPaths(new() { Events = Array.Empty<MachineEvent>() }); Equal(1, overridden.Count);
        model.Options.FilterEvents = (_, _) => false;
        var explicitPath = model.GetPathsFromEvents(new MachineEvent[] { new("GO") }); Equal(true, explicitPath[0].State.Matches("b"));
        Observations["providers"] = new { calls, generated = generated.Select(path => path.Description).ToArray(), overridden = overridden.Select(path => path.Description).ToArray(), explicitPath = explicitPath[0].Description };
    }
    private static void Validation()
    {
        var contextCalls = 0; var delayCalls = 0; var rejected = 0; var accepted = 0;
        foreach (var delay in new[] { MachineDelays.From<Empty>(0), MachineDelays.From<Empty>(double.NaN), MachineDelays.Named<Empty>("later"), MachineDelays.From<Empty>(_ => { delayCalls++; return 1; }) })
        foreach (var action in new[] { MachineActions.Raise<Empty>(_ => new("EV"), new() { Delay = delay }), MachineActions.Raise<Empty>((_, _) => new("EV"), new() { Delay = delay }), MachineActions.SendTo<Empty>("child", _ => new("EV"), new() { Delay = delay }), MachineActions.SendTo<Empty>(args => args.Self, _ => new("EV"), new() { Delay = delay }), MachineActions.SendParent<Empty>(_ => new("EV"), new() { Delay = delay }) })
        foreach (var placement in new[] { "entry", "exit", "event" })
        {
            var child = new StateConfig<Empty>
            {
                Entry = placement == "entry" ? [action] : [], Exit = placement == "exit" ? [action] : [],
                On = placement == "event" ? new Dictionary<string, IReadOnlyList<TransitionConfig<Empty>>> { ["EV"] = [new() { Actions = [action] }] } : new Dictionary<string, IReadOnlyList<TransitionConfig<Empty>>>()
            };
            var machine = new StateMachine<Empty>(new() { Initial = "a", States = States(("a", child)) }, _ => { contextCalls++; return new(); });
            try { StateGraph.CreateTestModel(machine); accepted++; }
            catch (InvalidOperationException error) { Equal("Delayed actions on test machines are not supported", error.Message); rejected++; }
        }
        Equal(30, rejected); Equal(30, accepted); Equal(0, contextCalls); Equal(0, delayCalls);
        Observations["validation"] = new { rejected, accepted, contextCalls, delayCalls };
    }
    private static async Task MutableModel()
    {
        var model = Multi(); var path = model.GetShortestPaths()[0]; var seen = new List<string>();
        model.Options.StateMatcher = (_, key) => key == "custom";
        await path.TestAsync(new() { States = new Dictionary<string, Func<MachineSnapshot<Empty>, Task>> { ["custom"] = state => { seen.Add(state.Value.AtomicValue ?? "missing"); return Task.CompletedTask; }, ["*"] = _ => throw new InvalidOperationException("Unexpected fallback") } }).ConfigureAwait(false);
        Equal(true, seen.SequenceEqual(["a", "b", "c", "d"])); Observations["mutable"] = seen;
    }
    private static void Prefixes()
    {
        var model = Multi(); model.Options.SerializeEvent = _ => "same"; model.Options.AllowDuplicatePaths = true;
        var snapshot = ActorTransitions.GetInitialSnapshot(model.TestLogic);
        var paths = new StatePath<MachineSnapshot<Empty>>[] { new(snapshot, [new(snapshot, new("A"))], 1), new(snapshot, [new(snapshot, new("A")), new(snapshot, new("B"))], 2), new(snapshot, [new(snapshot, new("C"))], 1) };
        var deduplicated = model.GetPaths((_, _) => paths); Equal(2, deduplicated.Count);
        Equal(3, model.GetPaths((_, _) => paths, new() { AllowDuplicatePaths = true }).Count);
        Observations["prefixes"] = deduplicated.Select(path => path.Steps.Select(step => step.Event.Type).ToArray()).ToArray();
    }
    private static void Metadata()
    {
        var machine = new StateMachine<Empty>(new() { Kind = StateKind.Parallel, States = States<Empty>(
            ("left", new() { Meta = new { Description = "ready" } }),
            ("right", new() { Kind = StateKind.Final, Meta = new Dictionary<string, object> { ["description"] = (Func<MachineSnapshot<Empty>, string>)(_ => "dynamic") } })) }, _ => new());
        var description = StateGraph.CreateTestModel(machine).GetShortestPaths()[0].Description;
        Equal("Reaches states \"ready\", dynamic: xstate.init", description); Observations["metadata"] = description;
    }
    public static int Benchmark(string destination)
    {
        var rows = new List<object>();
        foreach (var count in new[] { 16, 64 })
        {
            var states = Enumerable.Range(0, count).Select(i => ($"s{i}", i + 1 == count ? Node() : Node(("NEXT", $"s{i + 1}")))).ToArray();
            var model = StateGraph.CreateTestModel(Machine(states));
            for (var i = 0; i < 10; i++) model.GetShortestPaths();
            const int samples = 100; var elapsed = new double[samples]; var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < samples; i++) { var timer = Stopwatch.StartNew(); model.GetShortestPaths(); timer.Stop(); elapsed[i] = timer.Elapsed.TotalMicroseconds; }
            var allocated = (GC.GetAllocatedBytesForCurrentThread() - start) / (double)samples; Array.Sort(elapsed);
            rows.Add(new { states = count, samples, allocatedBytesPerQuery = allocated, p95Microseconds = elapsed[94], p99Microseconds = elapsed[98] });
        }
        File.WriteAllText(destination, JsonSerializer.Serialize(rows)); return 0;
    }
}
