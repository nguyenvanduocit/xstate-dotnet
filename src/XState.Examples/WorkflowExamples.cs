using XState;

namespace XStatePort.Examples;

public sealed record Person(string Name);
public sealed record GreetingInput(Person Person);
public sealed record MathInput(IReadOnlyList<string> Expressions);
public sealed record CustomerInput(string Customer);
public sealed record GreetingResult(string Greeting);
public sealed record MathResult(string Problem, string Result);
public sealed record WorkflowExample(StateMachine<Dictionary<string, object?>> Machine, object? Input = null, MachineEvent? Event = null,
    Action<MachineSnapshot<Dictionary<string, object?>>>? Observe = null);

public static partial class WorkflowExamples
{
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[]
    {
        "workflow-hello", "workflow-greeting", "workflow-event-greeting", "workflow-math-problem", "workflow-async-function", "workflow-parallel", "workflow-async-subflow", "workflow-filling-water"
    });

    public static WorkflowExample Create(string name, Action<string> log, Func<TimeSpan, Task>? delay = null, Func<string, Task<string>>? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        delay ??= static duration => Task.Delay(duration);
        return name switch
        {
            "workflow-hello" => Hello(),
            "workflow-filling-water" => FillingWater(log),
            "workflow-greeting" => Greeting(delay, eventBased: false),
            "workflow-event-greeting" => Greeting(delay, eventBased: true),
            "workflow-math-problem" => MathProblem(log, delay),
            "workflow-async-function" => AsyncFunction(log, delay),
            "workflow-parallel" => Parallel(log, delay),
            "workflow-async-subflow" => Onboarding(prompt ?? throw new ArgumentNullException(nameof(prompt))),
            _ => throw new ArgumentException("Unported or unknown example: " + name, nameof(name))
        };
    }

    private static WorkflowExample Hello() => new(new(new()
    {
        Id = "helloworld", Initial = "Hello State",
        States = new Dictionary<string, StateConfig<Dictionary<string, object?>>>(StringComparer.Ordinal)
        {
            ["Hello State"] = new() { Kind = StateKind.Final, OutputValue = new { result = "Hello World!" } }
        }
    }, _ => new(StringComparer.Ordinal)));

    private static WorkflowExample Greeting(Func<TimeSpan, Task> delay, bool eventBased)
    {
        var function = new PromiseLogic<GreetingResult>(async scope =>
        {
            var input = Required<Person>(scope.Input);
            await delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return new GreetingResult($"Hello, {input.Name}!");
        });
        var states = new Dictionary<string, StateConfig<Dictionary<string, object?>>>(StringComparer.Ordinal)
        {
            ["Greet"] = new()
            {
                Invoke = [new()
                {
                    Source = ActorSource.Named("greetingFunction"),
                    Input = args => eventBased ? Required<Person>(args.Event.Payload) : Required<GreetingInput>(args.Event.Payload).Person,
                    OnDone = [new()
                    {
                        Target = ["Greeted"],
                        Actions = [MachineActions.Assign<Dictionary<string, object?>>((context, ev) =>
                            new(context, StringComparer.Ordinal) { ["greeting"] = Required<GreetingResult>(Required<ActorDoneData>(ev.Payload).Output).Greeting })]
                    }]
                }]
            },
            ["Greeted"] = new() { Kind = StateKind.Final, Output = args => new { greeting = args.Context["greeting"] } }
        };
        if (eventBased) states["Waiting"] = new()
        {
            On = new Dictionary<string, IReadOnlyList<TransitionConfig<Dictionary<string, object?>>>>(StringComparer.Ordinal)
            { ["greet"] = [new() { Target = ["Greet"] }] }
        };
        var machine = new MachineSetup<Dictionary<string, object?>>(actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal)
        { ["greetingFunction"] = ActorSource.From(function) }).CreateMachine(new()
        {
            Id = eventBased ? "event-greeting" : "greeting", Initial = eventBased ? "Waiting" : "Greet", States = states
        }, _ => new(StringComparer.Ordinal));
        return new(machine, eventBased ? null : new GreetingInput(new Person("Jenny")), eventBased ? new MachineEvent("greet", new Person("Jenny")) : null);
    }

    private static WorkflowExample MathProblem(Action<string> log, Func<TimeSpan, Task> delay)
    {
        async Task<MathResult> Solve(string problem)
        {
            log("solving " + problem);
            await delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return new(problem, "Solved " + problem);
        }
        var function = new PromiseLogic<MathResult[]>(scope => Task.WhenAll(Required<MathInput>(scope.Input).Expressions.Select(Solve)));
        var machine = new MachineSetup<Dictionary<string, object?>>(actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal)
        { ["batchMathFunction"] = ActorSource.From(function) }).CreateMachine(new()
        {
            Id = "math-problem", Initial = "Solve",
            States = new Dictionary<string, StateConfig<Dictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["Solve"] = new() { Invoke = [new()
                {
                    Source = ActorSource.Named("batchMathFunction"), Input = args => Required<MathInput>(args.Event.Payload),
                    OnDone = [new() { Target = ["Solved"], Actions = [MachineActions.Assign<Dictionary<string, object?>>((context, ev) =>
                        new(context, StringComparer.Ordinal) { ["results"] = Required<MathResult[]>(Required<ActorDoneData>(ev.Payload).Output).Select(result => result.Result).ToArray() })] }]
                }] },
                ["Solved"] = new() { Kind = StateKind.Final, Output = args => new { results = args.Context["results"] } }
            }
        }, _ => new(StringComparer.Ordinal));
        return new(machine, new MathInput(["2+2", "4-1", "10x3", "20/2"]));
    }

    private static WorkflowExample AsyncFunction(Action<string> log, Func<TimeSpan, Task> delay)
    {
        var function = new PromiseLogic<object?>(async scope =>
        {
            var input = Required<CustomerInput>(scope.Input);
            log("Sending email to " + input.Customer);
            // Upstream's timer ignores AbortSignal. Stopping the actor must not
            // silently change that producer behavior in this port.
            await delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            log("Email sent to " + input.Customer);
            return null;
        });
        var machine = new MachineSetup<Dictionary<string, object?>>(actors: new Dictionary<string, ActorSource>(StringComparer.Ordinal)
        { ["sendEmail"] = ActorSource.From(function) }).CreateMachine(new()
        {
            Id = "async-function-invocation", Initial = "Send email",
            States = new Dictionary<string, StateConfig<Dictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["Send email"] = new() { Invoke = [new()
                {
                    Source = ActorSource.Named("sendEmail"), Input = args => new CustomerInput(Required<string>(args.Context["customer"])),
                    OnDone = [new() { Target = ["Email sent"] }]
                }] },
                ["Email sent"] = new() { Kind = StateKind.Final }
            }
        }, args => new(StringComparer.Ordinal) { ["customer"] = Required<CustomerInput>(args.Input).Customer });
        return new(machine, new CustomerInput("david@example.com"));
    }

    private static T Required<T>(object? value) => value is T typed ? typed : throw new ArgumentException("Expected " + typeof(T).Name + ".");
}
