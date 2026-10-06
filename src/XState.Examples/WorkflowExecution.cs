using System.Text.Json;
using XState;

namespace XStatePort.Examples;

public sealed record WorkflowObservation(JsonElement Value, string Status, JsonElement Context, bool HasOutput, object? Output);
public sealed record WorkflowTrace(IReadOnlyList<WorkflowObservation> Snapshots, IReadOnlyList<string> Logs, int Completions);

public static class WorkflowExecution
{
    public static async Task<WorkflowTrace> RunAsync(string name, Action<string>? output = null,
        Func<WorkflowExample, WorkflowExample>? configure = null, Func<TimeSpan, Task>? delay = null,
        Func<string, Task<string>>? prompt = null, CancellationToken cancellationToken = default)
    {
        var logs = new List<string>();
        var snapshots = new List<WorkflowObservation>();
        void Log(string text) => ActorRuntime.Run(() => { logs.Add(text); output?.Invoke(text); });
        async Task<string> Prompt(string question)
        {
            Log(question);
            if (prompt is null) throw new InvalidOperationException("This example requires a prompt transport.");
            return await prompt(question).ConfigureAwait(false);
        }
        var example = WorkflowExamples.Create(name, Log, delay, Prompt);
        if (configure is not null) example = configure(example);
        var actor = new Actor<MachineSnapshot<Dictionary<string, object?>>>(example.Machine, example.Input);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = 0;
        using var subscription = actor.Subscribe(snapshot =>
        {
            snapshots.Add(new(
                JsonSerializer.Deserialize<JsonElement>(snapshot.Value.ToJson()), snapshot.Status.ToString().ToLowerInvariant(),
                JsonSerializer.SerializeToElement(snapshot.Context), snapshot.HasOutput, snapshot.Output));
            example.Observe?.Invoke(snapshot);
        },
            failure => completion.TrySetException(ActorErrors.ToException(failure)),
            () =>
            {
                completions++;
                Log("workflow completed " + (actor.GetSnapshot().HasOutput ? JsonSerializer.Serialize(actor.GetSnapshot().Output) : "undefined"));
                completion.TrySetResult();
            });
        try
        {
            ActorRuntime.Run(() => { actor.Start(); if (example.Event is { } ev) actor.Send(ev); });
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(snapshots.ToArray(), logs.ToArray(), completions);
        }
        finally { actor.Stop(); }
    }
}
