using System.Runtime.CompilerServices;
using System.Text.Json;
using XState;
using static XStatePort.Tests.ActorTaskTests;
namespace XStatePort.Tests;

internal static class ActionDiagnosticsTests
{
    private static readonly string[] BuiltinNames = ["assign", "raise", "sendTo", "emit"];
    private static readonly Dictionary<string, object> Results = new(StringComparer.Ordinal);
    public static void Register(List<(string Id, Func<Task> Run)> cases) => cases.Add(("packages/core/test/actions.test.ts::actions > should warn if called in custom action", () => { Builtins(); return Task.CompletedTask; }));
    private static string Expected(string name) => $"Custom actions should not call `{name}()` directly, as it is not imperative. See https://stately.ai/docs/actions#built-in-actions for more details.";
    private static void Builtins()
    {
        var warnings = new List<string>(); var machine = new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>((_, _) => { MachineActions.Assign<int>((context, _) => context); MachineActions.Raise<int>(new MachineEvent("")); MachineActions.SendTo<int>("", new MachineEvent("")); MachineActions.Emit<int>(new MachineEvent("")); })] }, _ => 0);
        var actor = new Actor<MachineSnapshot<int>>(machine, options: new() { Warning = warnings.Add }).Start();
        try { Equal(string.Join('\n', BuiltinNames.Select(Expected)), string.Join('\n', warnings)); Results["builtins"] = warnings; } finally { actor.Stop(); }
    }
    public static void RegisterResources(List<(string Id, Func<Task> Run)> cases)
    {
        cases.Add(("action warning scope restores after nested actors and exceptions and does not retain actors", () => { Scope(); return Task.CompletedTask; }));
        cases.Add(("action diagnostic observations export", () => { File.WriteAllText("tmp/xstate-parity/csharp-action-diagnostics.json", JsonSerializer.Serialize(Results)); return Task.CompletedTask; }));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Exercise(List<string> warnings)
    {
        var inner = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>((_, _) => { MachineActions.Emit<int>(new MachineEvent("x")); throw new InvalidOperationException("inner"); })] }, _ => 0), options: new() { Warning = message => warnings.Add("inner:" + message) });
        using var subscription = inner.Subscribe(onError: _ => { });
        var outer = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Entry = [MachineActions.Effect<int>((_, _) => { MachineActions.Assign<int>((context, _) => context); inner.Start(); MachineActions.Raise<int>(new MachineEvent("x")); })] }, _ => 0), options: new() { Warning = message => warnings.Add("outer:" + message) }).Start();
        inner.Stop(); outer.Stop(); return new WeakReference(outer);
    }
    private static void Scope()
    {
        var warnings = new List<string>(); var weak = Exercise(warnings); MachineActions.Assign<int>((context, _) => context);
        Equal(string.Join('\n', new[] { "outer:" + Expected("assign"), "inner:" + Expected("emit"), "outer:" + Expected("raise") }), string.Join('\n', warnings));
        for (var i = 0; i < 5 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Equal(false, weak.IsAlive); Results["scope"] = warnings;
    }
}
