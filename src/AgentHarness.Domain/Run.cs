namespace AgentHarness.Domain;

/// <summary>
/// Logical execution requested for a turn. Durable. May span multiple Attempts
/// (each reclaim spawns a NEW Attempt; history is preserved).
/// </summary>
public sealed record Run
{
    public Guid Id { get; init; }
    public Guid TurnId { get; init; }
    public RunState State { get; set; } = RunState.Pending;
    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<RunEvent> Events => _events;
    private readonly List<RunEvent> _events = new();

    public void Dispatch() => Transition(RunState.Pending, RunState.Dispatched, RunEventType.Dispatched);
    public void MarkRunning() => Transition(RunState.Dispatched, RunState.Running, RunEventType.Running);

    /// <summary>Terminal states. A reclaimed run stays the same Run; a new Attempt is added.</summary>
    public void Succeed() => Transition(RunState.Running, RunState.Succeeded, RunEventType.Succeeded);
    public void Fail(string? reason) => Transition(RunState.Running, RunState.Failed, RunEventType.Failed, reason);
    public void Timeout() => Transition(RunState.Running, RunState.TimedOut, RunEventType.TimedOut);
    public void Cancel() => Transition(RunState.Running, RunState.Cancelled, RunEventType.Cancelled);
    public void Abandon() => Transition(RunState.Running, RunState.Abandoned, RunEventType.Abandoned);

    private void Transition(RunState from, RunState to, RunEventType ev, string? detail = null)
    {
        if (State != from)
            throw new InvalidStateTransitionException($"Run {Id}: {State} -> {to} (expected {from})");
        State = to;
        Append(ev, detail);
    }

    private void Append(RunEventType type, string? detail = null) =>
        _events.Add(new RunEvent(type, DateTimeOffset.UtcNow, detail));
}

public enum RunState { Pending, Dispatched, Running, Succeeded, Failed, TimedOut, Cancelled, Abandoned }

public enum RunEventType { Dispatched, Running, Succeeded, Failed, TimedOut, Cancelled, Abandoned }

public sealed record RunEvent(RunEventType Type, DateTimeOffset At, string? Detail);
