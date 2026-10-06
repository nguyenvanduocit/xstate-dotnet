using System.Collections.ObjectModel;
using ObjectContext = System.Collections.Generic.IReadOnlyDictionary<string, object?>;

namespace XState;

/// <summary>A constant or computed property in a dictionary context assignment.</summary>
public readonly struct ContextPropertyAssignment
{
    private readonly object? value;
    private readonly Func<MachineAssignArgs<ObjectContext>, object?>? expression;

    private ContextPropertyAssignment(object? value, Func<MachineAssignArgs<ObjectContext>, object?>? expression)
    {
        this.value = value;
        this.expression = expression;
    }

    public static ContextPropertyAssignment Value(object? value) => new(value, null);
    public static ContextPropertyAssignment Expression(Func<MachineAssignArgs<ObjectContext>, object?> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new(null, expression);
    }

    internal object? Resolve(MachineAssignArgs<ObjectContext> args) => expression is null ? value : expression(args);
}

public static partial class MachineActions
{
    /// <summary>Shallow-merges a partial result into a dictionary context, preserving omitted properties.</summary>
    public static MachineAction<ObjectContext> AssignPartial(Func<MachineAssignArgs<ObjectContext>, ObjectContext> assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return Assign<ObjectContext>(args =>
        {
            ValidateContext(args.Context);
            var partial = assignment(args);
            var updated = CopyContext(args.Context);
            foreach (var (key, value) in partial) updated[key] = value;
            return new ReadOnlyDictionary<string, object?>(updated);
        });
    }

    /// <summary>Evaluates all properties against the same pre-assignment context and shallow-merges the results.</summary>
    public static MachineAction<ObjectContext> Assign(IReadOnlyDictionary<string, ContextPropertyAssignment> assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return Assign<ObjectContext>(args =>
        {
            ValidateContext(args.Context);
            var partial = assignment.Count == 0 ? [] : new KeyValuePair<string, object?>[assignment.Count];
            var index = 0;
            // Resolve against the original context, then copy it, matching upstream even when a callback mutates it.
            foreach (var (key, property) in assignment) partial[index++] = new(key, property.Resolve(args));
            var updated = CopyContext(args.Context);
            foreach (var (key, value) in partial) updated[key] = value;
            return new ReadOnlyDictionary<string, object?>(updated);
        });
    }

    private static Dictionary<string, object?> CopyContext(ObjectContext context)
    {
        return new(context, StringComparer.Ordinal);
    }

    private static void ValidateContext(ObjectContext context)
    {
        if (context is null) throw new InvalidOperationException("Cannot assign to undefined `context`. Ensure that `context` is defined in the machine config.");
    }
}
