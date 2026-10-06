namespace XState;

public static partial class MachineActions
{
    /// <summary>Retains a parameterized action object, including its extra properties. Execution resolves its type through the action registry.</summary>
    public static MachineAction<TContext> FromProperties<TContext>(IReadOnlyDictionary<string, object?> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        _ = ActionDefinition.ObjectActionType(properties);
        return new ObjectAction<TContext>(properties);
    }
    internal static MachineAction<TContext> SerializedNull<TContext>() => NullAction<TContext>.Instance;
    private sealed class NullAction<TContext> : MachineAction<TContext>
    {
        internal static readonly NullAction<TContext> Instance = new();
        public override string Type => throw new InvalidOperationException("Cannot read properties of null (reading 'type')");
        internal override ActionDefinition? DefinitionValue => null;
        internal override void Resolve(Execution<TContext> execution, MachineParameters parameters = default, string? referencedName = null) =>
            throw new InvalidOperationException("Cannot read properties of null (reading 'type')");
    }
    private sealed class ObjectAction<TContext>(IReadOnlyDictionary<string, object?> properties) : MachineAction<TContext>
    {
        public override string Type => ActionDefinition.ObjectActionType(properties);
        public override bool HasParameters => properties.ContainsKey("params");
        public override object? Parameters => properties.GetValueOrDefault("params");
        public override ActionDefinition Definition => new(properties);
        internal override object? JsonValue => properties;
        internal override void Resolve(Execution<TContext> execution, MachineParameters parameters = default, string? referencedName = null)
        {
            // An `exec` property is metadata in v5; only the implementation registered for `type` executes.
            var name = Type;
            var value = Parameters;
            var resolved = value switch
            {
                Func<TContext, MachineEvent, object?> expression => new MachineParameters(expression(execution.Context, execution.MachineEvent)),
                Func<MachineActionArgs<TContext>, object?> expression => new MachineParameters(expression(execution.Args())),
                Delegate => throw new ArgumentException("Action params expression must accept context/event or MachineActionArgs."),
                _ => HasParameters ? new MachineParameters(value) : default
            };
            execution.ResolveNamedAction(name, resolved);
        }
    }
}
