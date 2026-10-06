using XState;

namespace XStatePort.Examples;

public static partial class WorkflowExamples
{
    private static WorkflowExample Parallel(Action<string> log, Func<TimeSpan, Task> delay)
    {
        PromiseLogic<object?> DelayFunction(string name, int seconds) => new(async _ =>
        {
            await delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            log("Resolved " + name);
            return null;
        });
        static StateConfig<Dictionary<string, object?>> Branch(string source) => new()
        {
            Initial = "active",
            States = new Dictionary<string, StateConfig<Dictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["active"] = new() { Invoke = [new() { Source = ActorSource.Named(source), OnDone = [new() { Target = ["done"] }] }] },
                ["done"] = new() { Kind = StateKind.Final }
            }
        };
        var setup = new MachineSetup<Dictionary<string, object?>>(actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal)
        {
            ["shortDelay"] = ActorSource.From(DelayFunction("shortDelay", 1)),
            ["longDelay"] = ActorSource.From(DelayFunction("longDelay", 3))
        });
        return new(setup.CreateMachine(new()
        {
            Id = "parallel-execution", Initial = "ParallelExec",
            States = new Dictionary<string, StateConfig<Dictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["ParallelExec"] = new()
                {
                    Kind = StateKind.Parallel,
                    States = new Dictionary<string, StateConfig<Dictionary<string, object?>>>(StringComparer.Ordinal)
                    {
                        ["ShortDelayBranch"] = Branch("shortDelay"), ["LongDelayBranch"] = Branch("longDelay")
                    },
                    OnDone = [new() { Target = ["Success"] }]
                },
                ["Success"] = new() { Kind = StateKind.Final }
            }
        }, _ => new(StringComparer.Ordinal)));
    }
}
