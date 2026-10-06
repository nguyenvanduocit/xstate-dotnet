namespace XState;

/// <summary>Preserves the distinction between an omitted parameter and explicit null.</summary>
internal readonly record struct MachineParameters
{
    public MachineParameters(object? value) { Value = value; HasValue = true; }
    public object? Value { get; }
    public bool HasValue { get; }
}
