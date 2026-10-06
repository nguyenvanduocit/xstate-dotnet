using XState;

namespace XStatePort.Examples;

public sealed record PromptInput(string Question);
public sealed record PromptResponse(string Response);

public static partial class WorkflowExamples
{
    private static WorkflowExample Onboarding(Func<string, Task<string>> prompt)
    {
        var promptLogic = new PromiseLogic<PromptResponse>(async scope =>
            new(await prompt(Required<PromptInput>(scope.Input).Question).ConfigureAwait(false)));
        var onboarding = new MachineSetup<Dictionary<string, object?>>(actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal)
        { ["prompt"] = ActorSource.From(promptLogic) }).CreateMachine(new()
        {
            Id = "onboarding", Initial = "Welcome",
            States = new Dictionary<string, StateConfig<Dictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["Welcome"] = new() { Invoke = [new()
                {
                    Source = ActorSource.Named("prompt"), InputValue = new PromptInput("What is your name?"),
                    OnDone = [new()
                    {
                        Target = ["Personalize"], Actions = [MachineActions.Assign<Dictionary<string, object?>>((context, ev) =>
                            new(context, StringComparer.Ordinal) { ["name"] = Required<PromptResponse>(Required<ActorDoneData>(ev.Payload).Output).Response })]
                    }]
                }] },
                ["Personalize"] = new() { Invoke = [new()
                {
                    Source = ActorSource.Named("prompt"),
                    Input = args => new PromptInput($"Welcome {args.Context["name"]}, press enter to finish the onboarding process"),
                    OnDone = [new() { Target = ["Completed"] }]
                }] },
                ["Completed"] = new() { Kind = StateKind.Final }
            }
        }, _ => new(StringComparer.Ordinal));
        var parent = new MachineSetup<Dictionary<string, object?>>(actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal)
        { ["onboarding"] = ActorSource.From(onboarding) }).CreateMachine(new()
        {
            Id = "async-function-invocation", Initial = "Onboard",
            States = new Dictionary<string, StateConfig<Dictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["Onboard"] = new() { Invoke = [new() { Source = ActorSource.Named("onboarding"), OnDone = [new() { Target = ["Onboarded"] }] }] },
                ["Onboarded"] = new() { Kind = StateKind.Final }
            }
        }, _ => new(StringComparer.Ordinal));
        return new(parent);
    }
}
