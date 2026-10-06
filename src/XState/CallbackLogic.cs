using System.Text.Json;
using System.Runtime.CompilerServices;

namespace XState;

public sealed record CallbackSnapshot : IActorSnapshot, IJsonSnapshot
{
    public SnapshotStatus Status { get; init; } = SnapshotStatus.Active;
    public object? Input { get; init; }
    public object? Output => null;
    public object? Failure { get; init; }
    void IJsonSnapshot.WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartObject(); SnapshotJson.WriteStatus(writer, Status, Failure);
        if (Input is not null) { writer.WritePropertyName("input"); SnapshotJson.WriteValue(writer, Input); }
        writer.WriteEndObject();
    }
    internal static CallbackSnapshot FromJson(JsonElement data)
    {
        return new() { Status = SnapshotJson.ReadStatus(data), Failure = SnapshotJson.ReadOptional(data, "error"), Input = SnapshotJson.ReadOptional(data, "input") };
    }

}

/// <summary>Per-start callback API. Receivers and cleanup belong to one actor invocation.</summary>
public sealed class CallbackScope
{
    private readonly ActorScope<CallbackSnapshot> scope;
    private readonly Action<Action<MachineEvent>> receive;
    internal CallbackScope(ActorScope<CallbackSnapshot> scope, object? input, Action<Action<MachineEvent>> receive)
    {
        this.scope = scope;
        Input = input;
        this.receive = receive;
    }
    public object? Input { get; }
    public IActor Self => scope.Self;
    public ActorSystem System => scope.System;
    public void Receive(Action<MachineEvent> listener) { ArgumentNullException.ThrowIfNull(listener); receive(listener); }
    public void Emit(MachineEvent ev) => scope.Emit(ev);
    public void SendBack(MachineEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (Self.GetSnapshot().Status != SnapshotStatus.Stopped && Self.Parent is { } parent)
            System.Relay(Self, parent, ev);
    }
}

public sealed class CallbackLogic : IActorLogic<CallbackSnapshot>
{
    private sealed class Instance
    {
        public List<Action<MachineEvent>> Receivers { get; } = [];
        public Action? Cleanup { get; set; }
    }
    private readonly ConditionalWeakTable<IActor, Instance> instances = new();
    private readonly Func<CallbackScope, Action?> callback;
    public CallbackLogic(Func<CallbackScope, Action?> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        this.callback = callback;
    }
    public CallbackSnapshot GetInitialSnapshot(ActorScope<CallbackSnapshot> scope, object? input) => new() { Input = input };
    public void Start(CallbackSnapshot snapshot, ActorScope<CallbackSnapshot> scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scope);
        var instance = new Instance();
        instances.Remove(scope.Self);
        instances.Add(scope.Self, instance);
        instance.Cleanup = callback(new(scope, snapshot.Input, listener =>
        {
            // Match JS Set identity, without C# delegate value equality collapsing distinct function objects.
            if (!instance.Receivers.Any(existing => ReferenceEquals(existing, listener))) instance.Receivers.Add(listener);
        }));
    }
    public CallbackSnapshot Transition(CallbackSnapshot snapshot, MachineEvent ev, ActorScope<CallbackSnapshot> scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(scope);
        if (!instances.TryGetValue(scope.Self, out var instance)) throw new InvalidOperationException("Callback actor has not been started.");
        if (ev.Type == "xstate.stop")
        {
            var stopped = snapshot with { Status = SnapshotStatus.Stopped, Failure = null };
            instances.Remove(scope.Self);
            instance.Receivers.Clear();
            var cleanup = instance.Cleanup;
            instance.Cleanup = null;
            cleanup?.Invoke();
            return stopped;
        }
        // Receivers added while delivering an event are visited in the same delivery, as in Set.forEach.
        for (var index = 0; index < instance.Receivers.Count; index++) instance.Receivers[index](ev);
        return snapshot;
    }
    public CallbackSnapshot GetErrorSnapshot(CallbackSnapshot? previous, Exception exception) =>
        (previous ?? new()) with { Status = SnapshotStatus.Error, Failure = ActorErrors.GetValue(exception) };
    public object GetPersistedSnapshot(CallbackSnapshot snapshot) => snapshot;
    public CallbackSnapshot RestoreSnapshot(object persistedSnapshot, ActorScope<CallbackSnapshot> scope) =>
        persistedSnapshot is JsonSnapshot json ? CallbackSnapshot.FromJson(json.Data) : persistedSnapshot as CallbackSnapshot ?? throw new ArgumentException("Persisted snapshot does not match callback logic.", nameof(persistedSnapshot));
}
