namespace XState.Graph;

public sealed record TestPath<TSnapshot> : StatePath<TSnapshot> where TSnapshot : class, IActorSnapshot
{
    private readonly Func<TestParameters<TSnapshot>, Task<TestPathResult<TSnapshot>>> test;
    internal TestPath(StatePath<TSnapshot> path, string description, Func<TestParameters<TSnapshot>, Task<TestPathResult<TSnapshot>>> test)
        : base(path.State, path.Steps, path.Weight) { Description = description; this.test = test; }
    public string Description { get; }
    public Task<TestPathResult<TSnapshot>> TestAsync(TestParameters<TSnapshot> parameters) => test(parameters);
}

/// <summary>Context for a failed model path. The original callback exception is retained as InnerException.</summary>
public sealed class PathTestException : Exception
{
    public PathTestException() { }
    public PathTestException(string message) : base(message) { }
    public PathTestException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class TestModel<TSnapshot> where TSnapshot : class, IActorSnapshot
{
    public IActorLogic<TSnapshot> TestLogic { get; }
    public TestModelOptions<TSnapshot> Options { get; set; }
    public TraversalOptions<TSnapshot>? DefaultTraversalOptions { get; set; }
    public TestModel(IActorLogic<TSnapshot> logic, TestModelOptions<TSnapshot>? options = null)
    {
        ArgumentNullException.ThrowIfNull(logic); TestLogic = logic;
        Options = TestModelOptions<TSnapshot>.Merge(GetDefaultOptions(), options);
    }
    public TestModelOptions<TSnapshot> GetDefaultOptions() => new()
    {
        SerializeState = (state, _, _) => SnapshotJson.Serialize(state), SerializeEvent = GraphSerialization.Event,
        SerializeTransition = (state, ev, _) => SnapshotJson.Serialize(state) + "|" + (ev?.Type ?? "undefined"),
        Events = Array.Empty<MachineEvent>(), StateMatcher = (_, key) => key == "*",
        Logger = new(Console.WriteLine, Console.Error.WriteLine)
    };
    private TestModelOptions<TSnapshot> ResolveOptions(TestModelOptions<TSnapshot>? options) =>
        TestModelOptions<TSnapshot>.Merge(DefaultTraversalOptions, Options, options);
    public IReadOnlyList<TestPath<TSnapshot>> GetPaths(PathGenerator<TSnapshot> generator, TestModelOptions<TSnapshot>? options = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var paths = generator(TestLogic, ResolveOptions(options));
        return Array.AsReadOnly((options?.AllowDuplicatePaths == true ? paths : Deduplicate(paths)).Select(ToTestPath).ToArray());
    }
    public IReadOnlyList<TestPath<TSnapshot>> GetShortestPaths(TestModelOptions<TSnapshot>? options = null) => GetPaths(StateGraph.CreateShortestPathsGen<TSnapshot>(), options);
    public IReadOnlyList<TestPath<TSnapshot>> GetSimplePaths(TestModelOptions<TSnapshot>? options = null) => GetPaths(StateGraph.CreateSimplePathsGen<TSnapshot>(), options);
    public IReadOnlyList<TestPath<TSnapshot>> GetShortestPathsFrom(IReadOnlyList<TestPath<TSnapshot>> paths, TestModelOptions<TSnapshot>? options = null) => Extend(paths, options, false);
    public IReadOnlyList<TestPath<TSnapshot>> GetSimplePathsFrom(IReadOnlyList<TestPath<TSnapshot>> paths, TestModelOptions<TSnapshot>? options = null) => Extend(paths, options, true);
    private System.Collections.ObjectModel.ReadOnlyCollection<TestPath<TSnapshot>> Extend(IReadOnlyList<TestPath<TSnapshot>> paths, TestModelOptions<TSnapshot>? options, bool simple)
    {
        ArgumentNullException.ThrowIfNull(paths); var result = new List<TestPath<TSnapshot>>();
        foreach (var head in paths)
        {
            var tailOptions = TestModelOptions<TSnapshot>.Merge(options, new TestModelOptions<TSnapshot> { FromState = head.State });
            foreach (var tail in simple ? GetSimplePaths(tailOptions) : GetShortestPaths(tailOptions)) result.Add(ToTestPath(StateGraph.JoinPaths(head, tail)));
        }
        return result.AsReadOnly();
    }
    public IReadOnlyList<TestPath<TSnapshot>> GetPathsFromEvents(IReadOnlyList<MachineEvent> events, TestModelOptions<TSnapshot>? options = null) =>
        Array.AsReadOnly(StateGraph.GetPathsFromEvents(TestLogic, events, options).Select(ToTestPath).ToArray());
    public IReadOnlyDictionary<string, AdjacencyValue<TSnapshot>> GetAdjacencyMap() => StateGraph.GetAdjacencyMap(TestLogic, Options);
    private TestPath<TSnapshot> ToTestPath(StatePath<TSnapshot> path)
    {
        var description = path.State is IGraphDescriptiveSnapshot machine
            ? "Reaches " + machine.DescribeForGraph().Trim() + ": " + string.Join(" → ", path.Steps.Select(step => TestModelFormatting.Event(step.Event)))
            : SnapshotJson.Serialize(path.State);
        return new(path, description, parameters => TestPathAsync(path, parameters));
    }
    private static StatePath<TSnapshot>[] Deduplicate(IReadOnlyList<StatePath<TSnapshot>> paths)
    {
        var sequences = paths.Select(path => (Path: path, Events: path.Steps.Select(step => GraphSerialization.Event(step.Event)).ToArray())).OrderByDescending(p => p.Events.Length);
        var kept = new List<(StatePath<TSnapshot> Path, string[] Events)>();
        foreach (var candidate in sequences)
        {
            if (kept.Any(longer => candidate.Events.AsSpan().SequenceEqual(longer.Events.AsSpan(0, candidate.Events.Length)))) continue;
            kept.Add(candidate);
        }
        return kept.Select(p => p.Path).ToArray();
    }
    public async Task<TestPathResult<TSnapshot>> TestPathAsync(StatePath<TSnapshot> path, TestParameters<TSnapshot> parameters, TestModelOptions<TSnapshot>? options = null)
    {
        ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(parameters);
        var steps = new List<TestStepResult<TSnapshot>>(); var result = new TestPathResult<TSnapshot>(steps.AsReadOnly(), new());
        try
        {
            foreach (var step in path.Steps)
            {
                var outcome = new TestStepResult<TSnapshot>(step, new(), new()); steps.Add(outcome);
                try { await TestTransitionAsync(parameters, step).ConfigureAwait(false); }
                catch (Exception error) { outcome.Event.Error = error; throw; }
                try { await TestStateAsync(parameters, step.State, options).ConfigureAwait(false); }
                catch (Exception error) { outcome.State.Error = error; throw; }
            }
        }
        catch (Exception error)
        {
            // Exception.Message is immutable in .NET. Preserve the original error as the cause instead of mutating runtime internals.
            throw new PathTestException(error.Message + TestModelFormatting.PathResult(path, result, Options), error);
        }
        return result;
    }
    public async Task TestStateAsync(TestParameters<TSnapshot> parameters, TSnapshot state, TestModelOptions<TSnapshot>? options = null)
    {
        ArgumentNullException.ThrowIfNull(parameters); ArgumentNullException.ThrowIfNull(state);
        var resolved = ResolveOptions(options);
        var keys = parameters.States?.Keys.OrderBy(JavaScriptPropertyOrder.Index).ToArray() ?? [];
        var matched = keys.Where(key => (resolved.StateMatcher ?? throw new InvalidOperationException("State matcher is null."))(state, key)).ToList();
        if (matched.Count == 0 && parameters.States?.ContainsKey("*") == true) matched.Add("*");
        foreach (var key in matched) if (parameters.States is { } tests) await tests[key](state).ConfigureAwait(false);
    }
    public async Task TestTransitionAsync(TestParameters<TSnapshot> parameters, GraphStep<TSnapshot> step)
    {
        ArgumentNullException.ThrowIfNull(parameters); ArgumentNullException.ThrowIfNull(step);
        if (parameters.Events?.TryGetValue(step.Event.Type, out var execute) == true) await execute(step).ConfigureAwait(false);
    }
}

public static partial class StateGraph
{
    public static PathGenerator<TSnapshot> CreateShortestPathsGen<TSnapshot>() where TSnapshot : class, IActorSnapshot => static (logic, options) => GetShortestPaths(logic, options);
    public static PathGenerator<TSnapshot> CreateSimplePathsGen<TSnapshot>() where TSnapshot : class, IActorSnapshot => static (logic, options) => GetSimplePaths(logic, options);
}
