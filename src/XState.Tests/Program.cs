using System.Text.Json;
using XState;

if (args.Length == 2 && args[0] == "--benchmark-initial-error") return XStatePort.Tests.InitialErrorTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-machine-creation") return XStatePort.Tests.MachineCreationBenchmarks.Run(args[1]);

if (args.Length == 2 && args[0] == "--benchmark") return XStatePort.Tests.RuntimeBenchmarks.Run(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-invoke") return XStatePort.Tests.RuntimeBenchmarks.Run(args[1], invocation: true);

if (args.Length == 2 && args[0] == "--benchmark-inspection") return XStatePort.Tests.RuntimeBenchmarks.Run(args[1], invocation: true, inspection: true);

if (args.Length == 2 && args[0] == "--benchmark-enqueue") return XStatePort.Tests.EnqueueBenchmarks.Run(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-observable") return XStatePort.Tests.ObservableBenchmarks.Run(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-persistence") return XStatePort.Tests.PersistenceBenchmarks.Run(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-persistence-json") return XStatePort.Tests.PersistenceBenchmarks.Run(args[1], json: true);

if (args.Length == 2 && args[0] == "--benchmark-next-transitions") return XStatePort.Tests.NextTransitionTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-log") return XStatePort.Tests.LoggerTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-microsteps") return XStatePort.Tests.PureMicrostepTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-state-nodes") return XStatePort.Tests.StateNodeTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-directed-graph") return XStatePort.Tests.DirectedGraphTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-empty-actor") return XStatePort.Tests.EmptyActorTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-resolve-state") return XStatePort.Tests.ResolveStateTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-node-order") return XStatePort.Tests.NodeOrderTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-graph-traversal") return XStatePort.Tests.GraphTraversalTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-graph-paths") return XStatePort.Tests.GraphPathsTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-test-model") return XStatePort.Tests.TestModelTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-selection") return XStatePort.Tests.SelectionTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-map-state") return XStatePort.Tests.MapStateTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-promise-task") return XStatePort.Tests.ThenableTests.Benchmark(args[1], taskOnly: true);

if (args.Length == 2 && args[0] == "--benchmark-thenable") return XStatePort.Tests.ThenableTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-parent-send") return XStatePort.Tests.ParentSendBenchmarks.Run(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-forward") return XStatePort.Tests.ForwardingTests.Benchmark(args[1]);

if (args.Length == 2 && args[0] == "--benchmark-assign") return XStatePort.Tests.AssignmentTests.Benchmark(args[1]);

var cases = new List<(string Id, Action Run)>();
void MatchCase(string title, params (string Parent, string Child, bool Expected)[] assertions)
{
    cases.Add(("packages/core/test/match.test.ts::matchesState() > " + title, () =>
    {
        foreach (var (parent, child, expected) in assertions)
        {
            var actual = StateValue.Matches(StateValue.Parse(parent), StateValue.Parse(child));
            if (actual != expected) throw new InvalidOperationException($"Matches({parent}, {child}): expected {expected}, got {actual}");
        }
    }));
}
MatchCase("should return true if two states are equivalent", ("\"a\"", "\"a\"", true), ("\"b.b1\"", "\"b.b1\"", true), ("\"B.bar\"", "{\"A\":\"foo\"}", false));
MatchCase("should return true if two state values are equivalent", ("{\"a\":\"b\"}", "{\"a\":\"b\"}", true), ("{\"a\":{\"b\":\"c\"}}", "{\"a\":{\"b\":\"c\"}}", true));
MatchCase("should return true if two parallel states are equivalent", ("{\"a\":{\"b1\":\"foo\",\"b2\":\"bar\"}}", "{\"a\":{\"b1\":\"foo\",\"b2\":\"bar\"}}", true), ("{\"a\":{\"b1\":\"foo\",\"b2\":\"bar\"},\"b\":{\"b3\":\"baz\",\"b4\":\"quo\"}}", "{\"a\":{\"b1\":\"foo\",\"b2\":\"bar\"},\"b\":{\"b3\":\"baz\",\"b4\":\"quo\"}}", true), ("{\"a\":\"foo\",\"b\":\"bar\"}", "{\"a\":\"foo\",\"b\":\"bar\"}", true));
MatchCase("should return true if a state is a substate of a superstate", ("\"b\"", "\"b.b1\"", true), ("\"foo.bar\"", "\"foo.bar.baz.quo\"", true));
MatchCase("should return true if a state value is a substate of a superstate value", ("\"b\"", "{\"b\":\"b1\"}", true), ("{\"foo\":\"bar\"}", "{\"foo\":{\"bar\":{\"baz\":\"quo\"}}}", true));
MatchCase("should return true if a parallel state value is a substate of a superstate value", ("\"b\"", "{\"b\":\"b1\",\"c\":\"c1\"}", true), ("{\"foo\":\"bar\",\"fooAgain\":\"barAgain\"}", "{\"foo\":{\"bar\":{\"baz\":\"quo\"}},\"fooAgain\":{\"barAgain\":\"baz\"}}", true));
MatchCase("should return false if two states are not equivalent", ("\"a\"", "\"b\"", false), ("\"a.a1\"", "\"b.b1\"", false));
MatchCase("should return false if parent state is more specific than child state", ("\"a.b.c\"", "\"a.b\"", false), ("{\"a\":{\"b\":{\"c\":\"d\"}}}", "{\"a\":\"b\"}", false));
MatchCase("should return false if two state values are not equivalent", ("{\"a\":\"a1\"}", "{\"b\":\"b1\"}", false));
MatchCase("should return false if a state is not a substate of a superstate", ("\"a\"", "\"b.b1\"", false), ("\"foo.false.baz\"", "\"foo.bar.baz.quo\"", false));
MatchCase("should return false if a state value is not a substate of a superstate value", ("\"a\"", "{\"b\":\"b1\"}", false), ("{\"foo\":{\"false\":\"baz\"}}", "{\"foo\":{\"bar\":{\"baz\":\"quo\"}}}", false));
MatchCase("should mix/match string state values and object state values", ("\"a.b.c\"", "{\"a\":{\"b\":\"c\"}}", true));

XStatePort.Tests.DataTests.Register(cases, Path.Combine(AppContext.BaseDirectory, "data-tests.json"));
XStatePort.Tests.TransitionTableTests.Register(cases);
XStatePort.Tests.MachineConstructionTests.Register(cases);
XStatePort.Tests.DefinitionTests.Register(cases);
XStatePort.Tests.JsonDefinitionTests.Register(cases);
XStatePort.Tests.HistoryTransitionTests.Register(cases);
XStatePort.Tests.SnapshotQueryTests.Register(cases);
XStatePort.Tests.LifecycleTests.Register(cases);
XStatePort.Tests.ActorLogicTests.Register(cases);
XStatePort.Tests.SystemCallbackTests.Register(cases);
XStatePort.Tests.InvocationTests.Register(cases);
XStatePort.Tests.SpawnTests.Register(cases);
XStatePort.Tests.InitialSnapshotTests.Register(cases);
XStatePort.Tests.NamedActionTests.Register(cases);
XStatePort.Tests.GuardTests.Register(cases);
XStatePort.Tests.GuardConditionTests.Register(cases);
XStatePort.Tests.ActivityTests.Register(cases);
XStatePort.Tests.EnqueueActionTests.Register(cases);
XStatePort.Tests.SchedulerTests.Register(cases);
XStatePort.Tests.MicrostepInspectionTests.Register(cases);
XStatePort.Tests.ActionInspectionTests.Register(cases);
XStatePort.Tests.NextTransitionTests.Register(cases);
XStatePort.Tests.LoggerTests.Register(cases);
XStatePort.Tests.PureMicrostepTests.Register(cases);
XStatePort.Tests.StateNodeTests.Register(cases);
XStatePort.Tests.DirectedGraphTests.Register(cases);
XStatePort.Tests.EmptyActorTests.Register(cases);
XStatePort.Tests.ResolveStateTests.Register(cases);
XStatePort.Tests.NodeOrderTests.Register(cases);
XStatePort.Tests.ParallelStateTests.Register(cases);
XStatePort.Tests.GraphTraversalTests.Register(cases);
XStatePort.Tests.GraphPathsTests.Register(cases);
XStatePort.Tests.SelectionTests.Register(cases);
XStatePort.Tests.MapStateTests.Register(cases);
XStatePort.Tests.FinalStateTests.Register(cases);
XStatePort.Tests.SystemInteropTests.Register(cases);
XStatePort.Tests.ThenableTests.Register(cases);
var asyncCases = cases.Select(test => (test.Id, Run: (Func<Task>)(() => { test.Run(); return Task.CompletedTask; }))).ToList();
XStatePort.Tests.ActorTaskTests.Register(asyncCases);
XStatePort.Tests.ParallelStateTests.RegisterAsync(asyncCases);
XStatePort.Tests.EnqueueActionTests.RegisterAsync(asyncCases);
XStatePort.Tests.RealTimerParityTests.Register(asyncCases);
XStatePort.Tests.PromiseLogicTests.Register(asyncCases);
XStatePort.Tests.ObservableLogicTests.Register(asyncCases);
XStatePort.Tests.MachinePersistenceTests.Register(asyncCases);
XStatePort.Tests.JsonSnapshotTests.Register(asyncCases);
XStatePort.Tests.TestModelTests.Register(asyncCases);
XStatePort.Tests.TestModelTests.RegisterIntegration(asyncCases);
XStatePort.Tests.DieHardTests.Register(asyncCases);
XStatePort.Tests.FinalStateTests.RegisterAsync(asyncCases);
XStatePort.Tests.ActorInteropTests.Register(asyncCases);
XStatePort.Tests.ErrorHandlingTests.Register(asyncCases);
XStatePort.Tests.PromiseInvocationTests.Register(asyncCases);
XStatePort.Tests.CallbackInvocationTests.Register(asyncCases);
XStatePort.Tests.ObservableInvocationTests.Register(asyncCases);
XStatePort.Tests.LogicInvocationTests.Register(asyncCases);
XStatePort.Tests.ForwardingTests.Register(asyncCases);
XStatePort.Tests.ConcurrentInvocationTests.Register(asyncCases);
XStatePort.Tests.RemainingInvocationTests.Register(asyncCases);
XStatePort.Tests.ExitActionTests.Register(asyncCases);
XStatePort.Tests.InitialActionTests.Register(asyncCases);
XStatePort.Tests.AssignmentTests.Register(asyncCases);
XStatePort.Tests.InputFactoryTests.Register(asyncCases);
XStatePort.Tests.TransientStateTests.Register(asyncCases);
XStatePort.Tests.ActionExecutionTests.Register(asyncCases);
XStatePort.Tests.SendRaiseTests.Register(asyncCases);
XStatePort.Tests.ActionDiagnosticsTests.Register(asyncCases);
XStatePort.Tests.InterpreterLifecycleTests.Register(asyncCases);
XStatePort.Tests.InterpreterActorTests.Register(asyncCases);
XStatePort.Tests.PredictableExecutionTests.Register(asyncCases);
var results = new List<object>();
var failed = 0;
foreach (var (id, run) in asyncCases)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); results.Add(new { id, status = "passed", error = (string?)null }); }
    catch (Exception ex) { failed++; results.Add(new { id, status = "failed", error = ex.ToString() }); Console.Error.WriteLine($"FAIL {id}: {ex.Message}"); }
}
var report = JsonSerializer.Serialize(new { total = asyncCases.Count, passed = asyncCases.Count - failed, failed, tests = results });
var destination = args.Length > 0 ? args[0] : "tmp/xstate-parity/csharp-results.json";
var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
if (directory is not null) Directory.CreateDirectory(directory);
File.WriteAllText(destination, report);
Console.WriteLine($"C# translated upstream cases: {asyncCases.Count - failed}/{asyncCases.Count} pass. Full parity is NOT implied.");
var resourceCases = new List<(string Id, Func<Task> Run)>();
XStatePort.Tests.ActorTaskResourceTests.Register(resourceCases);
XStatePort.Tests.ActorLogicResourceTests.Register(resourceCases);
XStatePort.Tests.SystemResourceTests.Register(resourceCases);
XStatePort.Tests.InvocationResourceTests.Register(resourceCases);
XStatePort.Tests.SpawnResourceTests.Register(resourceCases);
XStatePort.Tests.InitialSnapshotTests.RegisterResources(resourceCases);
XStatePort.Tests.NamedActionTests.RegisterResources(resourceCases);
XStatePort.Tests.GuardTests.RegisterResources(resourceCases);
XStatePort.Tests.GuardConditionTests.RegisterResources(resourceCases);
XStatePort.Tests.ActivityTests.RegisterResources(resourceCases);
XStatePort.Tests.EnqueueActionTests.RegisterResources(resourceCases);
XStatePort.Tests.SchedulerTests.RegisterResources(resourceCases);
XStatePort.Tests.MicrostepInspectionTests.RegisterResources(resourceCases);
XStatePort.Tests.ActionInspectionTests.RegisterResources(resourceCases);
XStatePort.Tests.NextTransitionTests.RegisterResources(resourceCases);
XStatePort.Tests.LoggerTests.RegisterResources(resourceCases);
XStatePort.Tests.PureMicrostepTests.RegisterResources(resourceCases);
XStatePort.Tests.StateNodeTests.RegisterResources(resourceCases);
XStatePort.Tests.DirectedGraphTests.RegisterResources(resourceCases);
XStatePort.Tests.EmptyActorTests.RegisterResources(resourceCases);
XStatePort.Tests.ResolveStateTests.RegisterResources(resourceCases);
XStatePort.Tests.NodeOrderTests.RegisterResources(resourceCases);
XStatePort.Tests.ParallelStateTests.RegisterResources(resourceCases);
XStatePort.Tests.GraphTraversalTests.RegisterResources(resourceCases);
XStatePort.Tests.GraphPathsTests.RegisterResources(resourceCases);
XStatePort.Tests.GraphPathResourceTests.Register(resourceCases);
XStatePort.Tests.RealClockTests.RegisterResources(resourceCases);
XStatePort.Tests.PromiseLogicTests.RegisterResources(resourceCases);
XStatePort.Tests.ObservableResourceTests.Register(resourceCases);
XStatePort.Tests.ActorSourceTests.Register(resourceCases);
XStatePort.Tests.PersistenceResourceTests.Register(resourceCases);
XStatePort.Tests.JsonSnapshotTests.RegisterResources(resourceCases);
XStatePort.Tests.TestModelTests.RegisterResources(resourceCases);
XStatePort.Tests.FunctionJsonTests.Register(resourceCases);
XStatePort.Tests.DieHardTests.RegisterResources(resourceCases);
XStatePort.Tests.SelectionTests.RegisterResources(resourceCases);
XStatePort.Tests.MapStateTests.RegisterResources(resourceCases);
XStatePort.Tests.FinalStateTests.RegisterResources(resourceCases);
XStatePort.Tests.SystemInteropTests.RegisterResources(resourceCases);
XStatePort.Tests.ThenableTests.RegisterResources(resourceCases);
XStatePort.Tests.ErrorHandlingTests.RegisterResources(resourceCases);
XStatePort.Tests.InitialErrorTests.RegisterResources(resourceCases);
XStatePort.Tests.ErrorValueTests.RegisterResources(resourceCases);
XStatePort.Tests.PromiseInvocationTests.RegisterResources(resourceCases);
XStatePort.Tests.CallbackInvocationTests.RegisterResources(resourceCases);
XStatePort.Tests.ObservableInvocationTests.RegisterResources(resourceCases);
XStatePort.Tests.LogicInvocationTests.RegisterResources(resourceCases);
XStatePort.Tests.ForwardingTests.RegisterResources(resourceCases);
XStatePort.Tests.ConcurrentInvocationTests.RegisterResources(resourceCases);
XStatePort.Tests.RemainingInvocationTests.RegisterResources(resourceCases);
XStatePort.Tests.ExitActionTests.RegisterResources(resourceCases);
XStatePort.Tests.InitialActionTests.RegisterResources(resourceCases);
XStatePort.Tests.AssignmentTests.RegisterResources(resourceCases);
XStatePort.Tests.InputFactoryTests.RegisterResources(resourceCases);
XStatePort.Tests.TransientStateTests.RegisterResources(resourceCases);
XStatePort.Tests.ActionExecutionTests.RegisterResources(resourceCases);
XStatePort.Tests.SendRaiseTests.RegisterResources(resourceCases);
XStatePort.Tests.ActionDiagnosticsTests.RegisterResources(resourceCases);
XStatePort.Tests.InterpreterLifecycleTests.RegisterResources(resourceCases);
XStatePort.Tests.InterpreterActorTests.RegisterResources(resourceCases);
XStatePort.Tests.PredictableExecutionTests.RegisterResources(resourceCases);
XStatePort.Tests.TransitionTableTests.RegisterResources(resourceCases);
XStatePort.Tests.MachineSetupTests.RegisterResources(resourceCases);
XStatePort.Tests.MachineConstructionTests.RegisterResources(resourceCases);
XStatePort.Tests.DefinitionTests.RegisterResources(resourceCases);
XStatePort.Tests.JsonDefinitionTests.RegisterResources(resourceCases);
XStatePort.Tests.HistoryTransitionTests.RegisterResources(resourceCases);
XStatePort.Tests.SnapshotQueryTests.RegisterResources(resourceCases);
var resourceResults = new List<object>();
var resourceFailures = 0;
foreach (var (id, run) in resourceCases)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); resourceResults.Add(new { id, status = "passed", error = (string?)null }); }
    catch (Exception ex) { resourceFailures++; resourceResults.Add(new { id, status = "failed", error = ex.ToString() }); Console.Error.WriteLine($"FAIL {id}: {ex.Message}"); }
}
File.WriteAllText(Path.ChangeExtension(destination, ".resources.json"), JsonSerializer.Serialize(new { total = resourceCases.Count, passed = resourceCases.Count - resourceFailures, failed = resourceFailures, tests = resourceResults }));
Console.WriteLine($"Supplemental .NET resource checks: {resourceCases.Count - resourceFailures}/{resourceCases.Count} pass.");
return failed == 0 && resourceFailures == 0 ? 0 : 1;









