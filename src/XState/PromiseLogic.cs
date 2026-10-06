using System.Text.Json;
using System.Runtime.CompilerServices;
namespace XState;

public sealed record PromiseSnapshot<TOutput> : IActorSnapshot, IJsonSnapshot
{
    private TOutput? result;
    public SnapshotStatus Status { get; init; } = SnapshotStatus.Active;
    public object? Input { get; init; }
    public bool HasOutput { get; private init; }
    public TOutput? Result => HasOutput ? result : throw new InvalidOperationException("The promise has no resolved output.");
    public object? Output => HasOutput ? result : null;
    public object? Failure { get; init; }
    void IJsonSnapshot.WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartObject(); SnapshotJson.WriteStatus(writer, Status, Failure);
        if (HasOutput) { writer.WritePropertyName("output"); SnapshotJson.WriteValue(writer, Output); }
        if (Input is not null) { writer.WritePropertyName("input"); SnapshotJson.WriteValue(writer, Input); }
        writer.WriteEndObject();
    }
    internal static PromiseSnapshot<TOutput> FromJson(JsonElement data)
    {
        return new() { Status = SnapshotJson.ReadStatus(data), Failure = SnapshotJson.ReadOptional(data, "error"), Input = SnapshotJson.ReadOptional(data, "input"),
            HasOutput = data.TryGetProperty("output", out _), result = data.TryGetProperty("output", out _) ? SnapshotJson.ReadOutput<TOutput>(data) : default };
    }

    internal PromiseSnapshot<TOutput> Resolved(TOutput? value) => this with { Status = SnapshotStatus.Done, Input = null, result = value, HasOutput = true };
    internal PromiseSnapshot<TOutput> Rejected(object? error) => this with { Status = SnapshotStatus.Error, Input = null, Failure = error };
}

public sealed class PromiseScope<TOutput>
{
    private readonly ActorScope<PromiseSnapshot<TOutput>> scope;
    internal PromiseScope(ActorScope<PromiseSnapshot<TOutput>> scope, object? input, CancellationToken signal)
    { this.scope = scope; Input = input; Signal = signal; }
    public object? Input { get; }
    public CancellationToken Signal { get; }
    public IActor Self => scope.Self;
    public ActorSystem System => scope.System;
    public void Emit(MachineEvent ev) => scope.Emit(ev);
}

/// <summary>Native Task counterpart of fromPromise; each actor invocation owns its cancellation source and observation.</summary>
public sealed class PromiseLogic<TOutput> : IActorLogic<PromiseSnapshot<TOutput>>
{
    private readonly Delegate creator;
    private readonly ConditionalWeakTable<IActor, Invocation> invocations = new();
    private sealed class Invocation(PromiseLogic<TOutput> owner, ActorScope<PromiseSnapshot<TOutput>> scope) : IDisposable
    {
        private PromiseLogic<TOutput>? logic = owner;
        private ActorScope<PromiseSnapshot<TOutput>>? scope = scope;
        private CancellationTokenSource? cancellation = new();
        private IDisposable? delivery;
        private Task<TOutput>? work;
        private bool cancelling;
        private bool pendingThenable;
        internal CancellationToken Signal => cancellation?.Token ?? throw new InvalidOperationException("Invocation has ended.");
        internal Task? Observation { get; private set; }
        internal void Observe(Task<TOutput> task) { work = task; Observation = ObserveAsync(task); }
        internal void Observe(IPromiseLike<TOutput> thenable)
        {
            pendingThenable = true;
            var current = scope ?? throw new InvalidOperationException("Invocation has ended.");
            new PromiseAssimilation<TOutput>(current.System.Microtasks,
                value => Settled(false, value),
                failure => Settled(true, failure)).Adopt(thenable);
        }
        private async Task ObserveAsync(Task<TOutput> task)
        {
            object? output = null;
            object? failure = null;
            var rejected = false;
            try { output = await task.ConfigureAwait(false); }
            catch (Exception error) { rejected = true; failure = ActorErrors.GetValue(error); }
            Settled(rejected, rejected ? failure : output);
        }
        private void Settled(bool rejected, object? value)
        {
            lock (ActorRuntime.Gate)
            {
                work = null;
                pendingThenable = false;
                if (scope is not { } current)
                {
                    if (!cancelling) DisposeController();
                    Observation = null;
                    return;
                }
                var ev = new MachineEvent(rejected ? "xstate.promise.reject" : "xstate.promise.resolve", value);
                delivery = current.System.Microtasks.Post(() => Complete(ev), current.ReportUnhandled);
            }
        }
        private void Complete(MachineEvent ev)
        {
            if (scope is not { } current) return;
            logic?.invocations.Remove(current.Self);
            Dispose();
            if (current.Self.GetSnapshot().Status == SnapshotStatus.Active) current.System.Relay(current.Self, current.Self, ev);
        }
        public void Dispose() => Release(cancel: false);
        internal void Cancel() => Release(cancel: true);
        private void Release(bool cancel)
        {
            var current = scope;
            scope = null;
            logic = null;
            delivery?.Dispose();
            delivery = null;
            var controller = cancellation;
            if (controller is null) return;
            try
            {
                if (cancel)
                {
                    cancelling = true;
                    try { controller.Cancel(); }
                    catch (AggregateException errors)
                    {
                        foreach (var error in errors.InnerExceptions) current?.ReportUnhandled(error);
                    }
                    finally { cancelling = false; }
                }
            }
            finally
            {
                // A cooperative operation may still be using its token's wait handle after Stop.
                // Detach the actor immediately, but dispose the controller only once that work ends.
                if (!pendingThenable && (work is null || work.IsCompleted)) { DisposeController(); Observation = null; }
            }
        }
        private void DisposeController()
        {
            cancellation?.Dispose();
            cancellation = null;
        }
    }
    public PromiseLogic(Func<PromiseScope<TOutput>, Task<TOutput>> creator)
    {
        ArgumentNullException.ThrowIfNull(creator);
        this.creator = creator;
    }
    internal PromiseLogic(Func<PromiseScope<TOutput>, IPromiseLike<TOutput>> creator)
    {
        ArgumentNullException.ThrowIfNull(creator);
        this.creator = creator;
    }
    public PromiseSnapshot<TOutput> GetInitialSnapshot(ActorScope<PromiseSnapshot<TOutput>> scope, object? input) => new() { Input = input };
    public void Start(PromiseSnapshot<TOutput> snapshot, ActorScope<PromiseSnapshot<TOutput>> scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scope);
        if (snapshot.Status != SnapshotStatus.Active) return;
        var invocation = new Invocation(this, scope);
        invocations.Remove(scope.Self);
        invocations.Add(scope.Self, invocation);
        try
        {
            var args = new PromiseScope<TOutput>(scope, snapshot.Input, invocation.Signal);
            if (creator is Func<PromiseScope<TOutput>, Task<TOutput>> taskCreator)
            {
                var task = taskCreator(args);
                if (task is null) throw new InvalidOperationException("Promise creator returned a null Task.");
                invocation.Observe(task);
            }
            else if (creator is Func<PromiseScope<TOutput>, IPromiseLike<TOutput>> thenableCreator)
            {
                var thenable = thenableCreator(args);
                if (thenable is null) throw new InvalidOperationException("Promise creator returned a null thenable.");
                invocation.Observe(thenable);
            }
            else throw new InvalidOperationException("Unknown promise creator contract.");
        }
        catch
        {
            invocations.Remove(scope.Self);
            invocation.Dispose();
            throw;
        }
    }
    public PromiseSnapshot<TOutput> Transition(PromiseSnapshot<TOutput> snapshot, MachineEvent ev, ActorScope<PromiseSnapshot<TOutput>> scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(scope);
        if (snapshot.Status != SnapshotStatus.Active) return snapshot;
        switch (ev.Type)
        {
            case "xstate.promise.resolve":
                if (ev.Payload is TOutput output) return snapshot.Resolved(output);
                if (ev.Payload is null && default(TOutput) is null) return snapshot.Resolved(default);
                throw new InvalidOperationException("Promise resolution value does not match the output type.");
            case "xstate.promise.reject":
                return snapshot.Rejected(ev.Payload);
            case "xstate.stop":
                if (invocations.TryGetValue(scope.Self, out var invocation))
                {
                    invocations.Remove(scope.Self);
                    invocation.Cancel();
                }
                return snapshot with { Status = SnapshotStatus.Stopped, Input = null };
            default: return snapshot;
        }
    }
    public PromiseSnapshot<TOutput> GetErrorSnapshot(PromiseSnapshot<TOutput>? previous, Exception exception) =>
        (previous ?? new()) with { Status = SnapshotStatus.Error, Failure = ActorErrors.GetValue(exception) };
    public object GetPersistedSnapshot(PromiseSnapshot<TOutput> snapshot) => snapshot;
    public PromiseSnapshot<TOutput> RestoreSnapshot(object persistedSnapshot, ActorScope<PromiseSnapshot<TOutput>> scope) =>
        persistedSnapshot is JsonSnapshot json ? PromiseSnapshot<TOutput>.FromJson(json.Data) : persistedSnapshot as PromiseSnapshot<TOutput> ?? throw new ArgumentException("Persisted snapshot does not match the promise logic.", nameof(persistedSnapshot));
}
