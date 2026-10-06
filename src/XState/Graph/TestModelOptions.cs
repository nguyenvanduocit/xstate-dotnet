namespace XState.Graph;

public sealed record TestModelLogger(Action<string> Log, Action<string> Error);
public sealed class TestModelOptions<TSnapshot> : TraversalOptions<TSnapshot> where TSnapshot : class, IActorSnapshot
{
    private int specified;
    private Func<TSnapshot, string, bool>? stateMatcher;
    private Func<TSnapshot, MachineEvent?, TSnapshot?, string>? serializeTransition;
    private TestModelLogger? logger;
    private bool allowDuplicatePaths;
    public Func<TSnapshot, string, bool>? StateMatcher { get => stateMatcher; set { stateMatcher = value; specified |= 1; } }
    public Func<TSnapshot, MachineEvent?, TSnapshot?, string>? SerializeTransition { get => serializeTransition; set { serializeTransition = value; specified |= 2; } }
    public TestModelLogger? Logger { get => logger; set { logger = value; specified |= 4; } }
    public bool AllowDuplicatePaths { get => allowDuplicatePaths; set { allowDuplicatePaths = value; specified |= 8; } }
    internal void CopyModelTo(TestModelOptions<TSnapshot> target, bool includeEvents = true)
    {
        CopyTo(target, includeEvents);
        if ((specified & 1) != 0) target.StateMatcher = stateMatcher;
        if ((specified & 2) != 0) target.SerializeTransition = serializeTransition;
        if ((specified & 4) != 0) target.Logger = logger;
        if ((specified & 8) != 0) target.AllowDuplicatePaths = allowDuplicatePaths;
    }
    internal static TestModelOptions<TSnapshot> Merge(params TraversalOptions<TSnapshot>?[] layers)
    {
        var result = new TestModelOptions<TSnapshot>();
        foreach (var layer in layers)
        {
            if (layer is TestModelOptions<TSnapshot> model) model.CopyModelTo(result);
            else layer?.CopyTo(result);
        }
        return result;
    }
}

public delegate IReadOnlyList<StatePath<TSnapshot>> PathGenerator<TSnapshot>(IActorLogic<TSnapshot> logic, TraversalOptions<TSnapshot> options) where TSnapshot : class, IActorSnapshot;
public sealed class TestParameters<TSnapshot> where TSnapshot : class, IActorSnapshot
{
    public IReadOnlyDictionary<string, Func<TSnapshot, Task>>? States { get; set; }
    public IReadOnlyDictionary<string, Func<GraphStep<TSnapshot>, Task>>? Events { get; set; }
}
public sealed class TestOutcome
{
    public Exception? Error { get; internal set; }
}
public sealed record TestStepResult<TSnapshot>(GraphStep<TSnapshot> Step, TestOutcome State, TestOutcome Event) where TSnapshot : class, IActorSnapshot;
public sealed record TestPathResult<TSnapshot>(IReadOnlyList<TestStepResult<TSnapshot>> Steps, TestOutcome State) where TSnapshot : class, IActorSnapshot;
