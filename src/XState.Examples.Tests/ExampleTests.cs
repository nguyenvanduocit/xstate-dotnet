using System.Text.Json;
using XState;
using XStatePort.Examples;

namespace XStatePort.Examples.Tests;

internal static class ExampleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal static async Task<int> RunAsync(string destination)
    {
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync("src/XState.Tests/examples-inventory.json").ConfigureAwait(false));
        var examples = new List<object>();
        var failures = 0;
        var total = 0;
        foreach (var name in WorkflowExamples.Names)
        {
            var checks = new List<object>();
            foreach (var scenario in Scenarios(name))
            {
                total++;
                var assertions = 0;
                void Assert(bool condition, string description)
                {
                    assertions++;
                    if (!condition) throw new InvalidOperationException(description);
                }
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var alternate = scenario == "alternate-input";
                    var longFirst = scenario == "long-first";
                    var responses = new Queue<string>([alternate ? "Ada" : "Jenny", ""]);
                    Func<string, Task<string>>? prompt = name == "workflow-async-subflow" ? _ => Task.FromResult(responses.Dequeue()) : null;
                    Func<TimeSpan, Task>? delay = longFirst ? duration => Task.Delay(TimeSpan.FromSeconds(4) - duration) : null;
                    var trace = await WorkflowExecution.RunAsync(name, configure: example => name == "workflow-filling-water" ? example with { Input = WaterInput(scenario) } : alternate ? Alternate(name, example) : example,
                        cancellationToken: timeout.Token, delay: delay, prompt: prompt).ConfigureAwait(false);
                    Assert(trace.Completions == 1, "Expected one completion callback.");
                    Assert(trace.Snapshots.Count > 0 && trace.Snapshots[^1].Status == "done", "Example did not reach final state.");
                    Assert(trace.Snapshots.All(snapshot => !snapshot.HasOutput && snapshot.Output is null), "Root output must remain undefined.");
                    if (name == "workflow-parallel")
                    {
                        Assert(trace.Snapshots.Count == 3, "Parallel parent should publish two active snapshots and one done snapshot.");
                        Assert(trace.Snapshots[0].Value.GetProperty("ParallelExec").EnumerateObject().All(branch => branch.Value.GetString() == "active"), "Both branches must start active.");
                        var intermediate = trace.Snapshots[1];
                        Assert(intermediate.Status == "active", "Parent must wait for both branches.");
                        Assert(intermediate.Value.GetProperty("ParallelExec").GetProperty(longFirst ? "LongDelayBranch" : "ShortDelayBranch").GetString() == "done", "First branch did not finish.");
                        Assert(intermediate.Value.GetProperty("ParallelExec").GetProperty(longFirst ? "ShortDelayBranch" : "LongDelayBranch").GetString() == "active", "Second branch finished too early.");
                        Assert(trace.Snapshots[^1].Value.GetString() == "Success", "Parallel completion did not reach Success.");
                    }
                    else Assert(JsonSerializer.Serialize(trace.Snapshots.Select(snapshot => snapshot.Value.GetString())) == JsonSerializer.Serialize(ExpectedStates(name, scenario)), "State sequence differs.");
                    if (name == "workflow-filling-water")
                    {
                        var input = WaterInput(scenario);
                        var expectedCounts = Enumerable.Range(0, WaterSteps(input) + 1).Select(step => input.Current + step).ToArray();
                        Assert(trace.Snapshots.Select(snapshot => snapshot.Context.GetProperty("counts").GetProperty("current").GetDouble()).SequenceEqual(expectedCounts), "Each timer must increment exactly once and stop at full.");
                        Assert(trace.Snapshots.All(snapshot => snapshot.Context.GetProperty("counts").GetProperty("max").GetDouble() == input.Max), "Capacity must remain unchanged.");
                        Assert(trace.Snapshots.Take(trace.Snapshots.Count - 1).All(snapshot => snapshot.Status == "active"), "Water workflow completed prematurely.");
                    }
                    if (name == "workflow-async-subflow") Assert(responses.Count == 0, "Both onboarding questions must consume an answer.");
                    var context = trace.Snapshots[^1].Context;
                    if (name is "workflow-greeting" or "workflow-event-greeting")
                        Assert(context.GetProperty("greeting").GetString() == (alternate ? "Hello, Ada!" : "Hello, Jenny!"), "Greeting input or assignment differs.");
                    if (name == "workflow-math-problem")
                    {
                        var results = context.GetProperty("results").EnumerateArray().Select(value => value.GetString()).ToArray();
                        Assert(JsonSerializer.Serialize(results) == JsonSerializer.Serialize(alternate ? Array.Empty<string>() : ["Solved 2+2", "Solved 4-1", "Solved 10x3", "Solved 20/2"]), "Math results or order differ.");
                    }
                    if (name == "workflow-async-function")
                        Assert(context.GetProperty("customer").GetString() == (alternate ? "ada@example.com" : "david@example.com"), "Customer input differs.");
                    Assert(JsonSerializer.Serialize(trace.Logs) == JsonSerializer.Serialize(ExpectedLogs(name, alternate, longFirst, scenario)), "Entry point log sequence differs.");
                    checks.Add(new { name = scenario, status = "passed", executed = true, assertions, observation = trace });
                }
                catch (Exception error)
                {
                    failures++;
                    checks.Add(new { name = scenario, status = "failed", executed = true, assertions, error = error.ToString() });
                    Console.Error.WriteLine($"FAIL {name}/{scenario}: {error}");
                }
            }
            var source = inventory.RootElement.GetProperty("examples").EnumerateArray().Single(example => example.GetProperty("name").GetString() == name);
            examples.Add(new { name, sourceSha256 = source.GetProperty("sourceSha256").GetString(), checks });
        }
        var report = new { commit = inventory.RootElement.GetProperty("commit").GetString(), runtime = "csharp", runId = Guid.NewGuid().ToString("N"), examples };
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);
        Console.WriteLine($"Example checks: {total - failures}/{total} passed across {examples.Count} examples.");
        return failures == 0 ? 0 : 1;
    }

    private static string[] Scenarios(string name) => name switch
    {
        "workflow-filling-water" => ["entrypoint", "already-full", "over-capacity", "fractional"],
        "workflow-hello" => ["entrypoint"], "workflow-parallel" => ["entrypoint", "long-first"],
        _ => ["entrypoint", "alternate-input"]
    };
    private static WorkflowExample Alternate(string name, WorkflowExample example) => name switch
    {
        "workflow-greeting" => example with { Input = new GreetingInput(new Person("Ada")) },
        "workflow-event-greeting" => example with { Event = new MachineEvent("greet", new Person("Ada")) },
        "workflow-math-problem" => example with { Input = new MathInput([]) },
        "workflow-async-subflow" => example,
        "workflow-async-function" => example with { Input = new CustomerInput("ada@example.com") },
        _ => throw new ArgumentException("No alternate scenario for " + name, nameof(name))
    };
    private static string[] ExpectedStates(string name, string scenario) => name switch
    {
        "workflow-filling-water" => [.. Enumerable.Repeat("AddWater", WaterSteps(WaterInput(scenario))), "GlassFull"],
        "workflow-hello" => ["Hello State"],
        "workflow-async-subflow" => ["Onboard", "Onboarded"],
        "workflow-greeting" => ["Greet", "Greeted"],
        "workflow-event-greeting" => ["Waiting", "Greet", "Greeted"],
        "workflow-math-problem" => ["Solve", "Solved"],
        "workflow-async-function" => ["Send email", "Email sent"],
        _ => throw new ArgumentException("Unknown example " + name, nameof(name))
    };
    private static WaterCounts WaterInput(string scenario) => scenario switch
    {
        "already-full" => new(10, 10), "over-capacity" => new(12, 10), "fractional" => new(0.5, 2), _ => new(0, 10)
    };
    private static int WaterSteps(WaterCounts input) => (int)Math.Max(0, Math.Ceiling(input.Max - input.Current));
    private static string[] WaterLogs(string scenario)
    {
        var input = WaterInput(scenario);
        return [.. Enumerable.Range(0, WaterSteps(input) + 1).Select(step => input.Current + step).SelectMany(current => new[]
        {
            "workflow state " + (current < input.Max ? "AddWater" : "GlassFull"),
            "workflow context " + JsonSerializer.Serialize(new { counts = new WaterCounts(current, input.Max) })
        }), "workflow completed undefined"];
    }
    private static string[] ExpectedLogs(string name, bool alternate, bool longFirst, string scenario) => name switch
    {
        "workflow-filling-water" => WaterLogs(scenario),
        "workflow-parallel" => [longFirst ? "Resolved longDelay" : "Resolved shortDelay", longFirst ? "Resolved shortDelay" : "Resolved longDelay", "workflow completed undefined"],
        "workflow-async-subflow" => ["What is your name?", $"Welcome {(alternate ? "Ada" : "Jenny")}, press enter to finish the onboarding process", "workflow completed undefined"],
        "workflow-math-problem" when !alternate => ["solving 2+2", "solving 4-1", "solving 10x3", "solving 20/2", "workflow completed undefined"],
        "workflow-async-function" => [$"Sending email to {(alternate ? "ada" : "david")}@example.com", $"Email sent to {(alternate ? "ada" : "david")}@example.com", "workflow completed undefined"],
        _ => ["workflow completed undefined"]
    };
}
