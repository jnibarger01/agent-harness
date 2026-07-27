namespace AgentHarness.Domain;

/// <summary>
/// One accepted user interaction. Durable. Idempotency key = Id.
/// States are a strict subset of the ACS work-item lifecycle vocabulary.
/// </summary>
public sealed record Turn
{
    public Guid Id { get; init; }
    public string Channel { get; init; } = "";
    public string InboundKey { get; init; } = ""; // idempotency key from channel (e.g. Telegram update_id)
    public string Payload { get; init; } = "";
    public TurnState State { get; set; } = TurnState.Received;
    public DateTimeOffset CreatedAt { get; init; }
    public string? RejectReason { get; set; }

    public IReadOnlyList<TurnEvent> Events => _events;
    private readonly List<TurnEvent> _events = new();

    public void Accept()
    {
        if (State != TurnState.Received)
            throw new InvalidStateTransitionException($"Turn {Id}: {State} -> Accepted");
        State = TurnState.Accepted;
        Append(TurnEventType.Accepted);
    }

    public void Reject(string reason)
    {
        if (State != TurnState.Received)
            throw new InvalidStateTransitionException($"Turn {Id}: {State} -> Rejected");
        State = TurnState.Rejected;
        RejectReason = reason;
        Append(TurnEventType.Rejected, reason);
    }

    public void MarkRunning() => Transition(TurnState.Accepted, TurnState.Running, TurnEventType.Running);
    public void MarkResponded() => Transition(TurnState.Running, TurnState.Responded, TurnEventType.Responded);

    private void Transition(TurnState from, TurnState to, TurnEventType ev)
    {
        if (State != from)
            throw new InvalidStateTransitionException($"Turn {Id}: {State} -> {to} (expected {from})");
        State = to;
        Append(ev);
    }

    private void Append(TurnEventType type, string? detail = null) =>
        _events.Add(new TurnEvent(type, DateTimeOffset.UtcNow, detail));
}

public enum TurnState { Received, Accepted, Running, Responded, Rejected }

public enum TurnEventType { Accepted, Rejected, Running, Responded }

public sealed record TurnEvent(TurnEventType Type, DateTimeOffset At, string? Detail);

public sealed class InvalidStateTransitionException : Exception
{
    public InvalidStateTransitionException(string message) : base(message) { }
}
