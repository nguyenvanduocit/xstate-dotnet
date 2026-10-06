using System.Text.Json;
using System.Text.Json.Serialization;
using XState;

namespace XStatePort.Examples;

public sealed record WaterCounts([property: JsonPropertyName("current")] double Current, [property: JsonPropertyName("max")] double Max);

public static partial class WorkflowExamples
{
    private static WorkflowExample FillingWater(Action<string> log)
    {
        var machine = new StateMachine<Dictionary<string, object?>>(new()
        {
            Id = "fillglassofwater", Initial = "CheckIfFull",
            States = new Dictionary<string, StateConfig<Dictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["CheckIfFull"] = new() { Always = [
                    new() { Target = ["AddWater"], Guard = MachineGuards.Predicate<Dictionary<string, object?>>((context, _) =>
                        Required<WaterCounts>(context["counts"]).Current < Required<WaterCounts>(context["counts"]).Max) },
                    new() { Target = ["GlassFull"] }] },
                ["AddWater"] = new() { After = new Dictionary<string, IReadOnlyList<TransitionConfig<Dictionary<string, object?>>>>(StringComparer.Ordinal)
                {
                    ["500"] = [new() { Target = ["CheckIfFull"], Actions = [MachineActions.Assign<Dictionary<string, object?>>((context, _) =>
                    {
                        var counts = Required<WaterCounts>(context["counts"]);
                        return new(context, StringComparer.Ordinal) { ["counts"] = counts with { Current = counts.Current + 1 } };
                    })] }]
                } },
                ["GlassFull"] = new() { Kind = StateKind.Final }
            }
        }, args => new(StringComparer.Ordinal) { ["counts"] = Required<WaterCounts>(args.Input) });
        return new(machine, new WaterCounts(0, 10), Observe: snapshot =>
        {
            log("workflow state " + (snapshot.Value.AtomicValue ?? throw new InvalidOperationException("Water workflow must have an atomic state value.")));
            log("workflow context " + JsonSerializer.Serialize(snapshot.Context));
        });
    }
}
