using XState;
using static XStatePort.Tests.ActorTaskTests;

namespace XStatePort.Tests;

internal static class SystemResourceTests
{
    public static void Register(List<(string Id, Func<Task> Run)> cases)
    {
        void Case(string title, Action run) => cases.Add((title, () => { run(); return Task.CompletedTask; }));
        Case("registry unregisters running roots on stop and initial final roots before start", RegistryLifecycle);
        Case("system identifiers collide within a shared parent system", DuplicateSystemId);
        Case("parent stopChild validates ownership and unregisters a stopped child", ChildOwnership);
        Case("child final and failure relay actor identity to the parent", ChildNotifications);
        Case("callback cleanup and receiver state belong to each actor instance", CallbackInstances);
        Case("callback snapshot restore restarts the callback with persisted input", CallbackRestore);
        Case("callback cleanup releases receiver captures while logic and system remain alive", CallbackCaptureRelease);
        Case("emission ordering snapshots listeners and runs exact handlers before wildcard handlers", EmissionOrdering);
        Case("emission listener exceptions are reported without terminating the actor", EmissionError);
    }

    private static (Actor<TransitionSnapshot<int>> Actor, Action<IActor> Stop) Parent(List<MachineEvent>? events = null)
    {
        Action<IActor>? stop = null;
        var logic = new TransitionLogic<int>((context, ev, scope) =>
        {
            stop = scope.StopChild;
            if (ev.Type != "scope") events?.Add(ev);
            return context;
        }, 0);
        var actor = new Actor<TransitionSnapshot<int>>(logic).Start();
        actor.Send(new("scope"));
        return (actor, stop ?? throw new InvalidOperationException("Scope was not delivered."));
    }

    private static void RegistryLifecycle()
    {
        var root = new Actor<CallbackSnapshot>(new CallbackLogic(_ => null), options: new() { SystemId = "root" });
        Equal<IActor?>(root, root.System.Get("root"));
        var saved = root.System.GetAll();
        root.Start().Stop();
        Equal<IActor?>(null, root.System.Get("root"));
        Equal<IActor>(root, saved["root"]);
        var machine = new StateMachine<int>(new() { Kind = StateKind.Final }, _ => 0);
        var done = new Actor<MachineSnapshot<int>>(machine, options: new() { SystemId = "done" });
        Equal<IActor?>(null, done.System.Get("done"));
        done.Start();
        Equal(0, done.System.GetAll().Count);
    }

    private static void DuplicateSystemId()
    {
        var (parent, stop) = Parent();
        var logic = new CallbackLogic(_ => null);
        var first = new Actor<CallbackSnapshot>(logic, options: new() { Parent = parent, SystemId = "child" }).Start();
        Exception? failure = null;
        try { _ = new Actor<CallbackSnapshot>(logic, options: new() { Parent = parent, SystemId = "child" }); }
        catch (InvalidOperationException error) { failure = error; }
        Equal("Actor with system ID 'child' already exists.", failure?.Message);
        Equal<IActor?>(first, parent.System.Get("child"));
        stop(first);
        parent.Stop();
    }

    private static void ChildOwnership()
    {
        var (parent, stop) = Parent();
        var (other, otherStop) = Parent();
        var cleaned = 0;
        var child = new Actor<CallbackSnapshot>(new CallbackLogic(_ => () => cleaned++), options: new() { Parent = parent, Id = "child", SystemId = "registered" }).Start();
        Exception? failure = null;
        try { child.Stop(); } catch (InvalidOperationException error) { failure = error; }
        Equal("A non-root actor cannot be stopped directly.", failure?.Message);
        failure = null;
        try { otherStop(child); } catch (InvalidOperationException error) { failure = error; }
        Equal($"Cannot stop child actor child of {other.Id} because it is not a child", failure?.Message);
        Equal(0, cleaned);
        stop(child);
        stop(child);
        Equal(1, cleaned);
        Equal<IActor?>(null, parent.System.Get("registered"));
        Equal(SnapshotStatus.Stopped, child.GetSnapshot().Status);
        parent.Stop();
        other.Stop();
    }

    private static void ChildNotifications()
    {
        var events = new List<MachineEvent>();
        var (parent, _) = Parent(events);
        var done = new Actor<MachineSnapshot<int>>(new StateMachine<int>(new() { Kind = StateKind.Final, Output = _ => 42 }, _ => 0),
            options: new() { Parent = parent, Id = "done" }).Start();
        Equal(new MachineEvent("xstate.done.actor.done", new ActorDoneData("done", 42)), events[0]);
        var failed = new Actor<CallbackSnapshot>(new CallbackLogic(_ => throw new InvalidOperationException("child failed")),
            options: new() { Parent = parent, Id = "failed" }).Start();
        Equal("xstate.error.actor.failed", events[1].Type);
        var error = events[1].Payload as ActorErrorData ?? throw new InvalidOperationException("Expected actor error data.");
        Equal("failed", error.ActorId);
        Equal("child failed", ActorTaskTests.RequireException(error.Failure).Message);
        Equal(SnapshotStatus.Done, done.GetSnapshot().Status);
        Equal(SnapshotStatus.Error, failed.GetSnapshot().Status);
        Equal(SnapshotStatus.Active, parent.GetSnapshot().Status);
        parent.Stop();
    }

    private static void CallbackInstances()
    {
        var received = new List<string>();
        var cleaned = new List<string>();
        var logic = new CallbackLogic(scope =>
        {
            var id = scope.Self.Id;
            scope.Receive(ev => received.Add(id + ":" + ev.Type));
            return () => cleaned.Add(id);
        });
        var first = new Actor<CallbackSnapshot>(logic, options: new() { Id = "a" }).Start();
        var second = new Actor<CallbackSnapshot>(logic, options: new() { Id = "b" }).Start();
        first.Send(new("ONE"));
        second.Send(new("TWO"));
        first.Stop();
        first.Send(new("ignored"));
        second.Send(new("THREE"));
        second.Stop();
        Equal(true, received.SequenceEqual(["a:ONE", "b:TWO", "b:THREE"]));
        Equal(true, cleaned.SequenceEqual(["a", "b"]));
    }

    private static void CallbackRestore()
    {
        var inputs = new List<object?>();
        var logic = new CallbackLogic(scope => { inputs.Add(scope.Input); return null; });
        var actor = new Actor<CallbackSnapshot>(logic, 13);
        var restored = new Actor<CallbackSnapshot>(logic, options: new() { Snapshot = actor.GetPersistedSnapshot() }).Start();
        Equal(true, inputs.SequenceEqual([13]));
        restored.Stop();
    }

    private sealed class Capture
    {
        private readonly byte[] buffer = new byte[65536];
        public void Receive(MachineEvent ev) => buffer[0] = (byte)ev.Type.Length;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (CallbackLogic Logic, Actor<CallbackSnapshot> Actor, WeakReference Capture) StoppedCallback()
    {
        WeakReference? weak = null;
        var logic = new CallbackLogic(scope =>
        {
            var capture = new Capture();
            weak = new(capture);
            scope.Receive(capture.Receive);
            return () => capture.Receive(new("cleanup"));
        });
        var actor = new Actor<CallbackSnapshot>(logic).Start();
        actor.Stop();
        return (logic, actor, weak ?? throw new InvalidOperationException("Callback did not start."));
    }
    private static void CallbackCaptureRelease()
    {
        var saved = StoppedCallback();
        for (var i = 0; i < 3 && saved.Capture.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Equal(false, saved.Capture.IsAlive);
        GC.KeepAlive(saved.Logic);
        GC.KeepAlive(saved.Actor.System);
    }

    private static void EmissionOrdering()
    {
        var log = new List<string>();
        var actor = new Actor<CallbackSnapshot>(new CallbackLogic(scope => { scope.Receive(scope.Emit); return null; })).Start();
        IDisposable? second = null;
        actor.On("*", _ => log.Add("wildcard"));
        actor.On("event", _ => { log.Add("first"); second?.Dispose(); });
        second = actor.On("event", _ => log.Add("second"));
        actor.Send(new("event"));
        actor.Send(new("event"));
        Equal(true, log.SequenceEqual(["first", "second", "wildcard", "first", "wildcard"]));
        actor.Stop();
        second.Dispose();
    }
    private static void EmissionError()
    {
        var reporter = new ErrorReporter();
        var actor = new Actor<CallbackSnapshot>(new CallbackLogic(scope => { scope.Receive(scope.Emit); return null; }),
            options: new() { ErrorReporter = reporter }).Start();
        var calls = 0;
        actor.On("event", _ => throw new InvalidOperationException("listener failed"));
        actor.On("*", _ => calls++);
        actor.Send(new("event"));
        Equal(1, calls);
        Equal(1, reporter.Errors.Count);
        Equal("listener failed", reporter.Errors[0].Message);
        Equal(SnapshotStatus.Active, actor.GetSnapshot().Status);
        actor.Stop();
    }
    private sealed class ErrorReporter : IUnhandledErrorReporter
    {
        public List<Exception> Errors { get; } = [];
        public void Report(object? failure) => Errors.Add(ActorErrors.ToException(failure));
    }
}
