namespace AgentHarness.Domain;

/// <summary>
/// One concrete worker execution. SUBORDINATE to an ACS work item.
///
/// Fork-A constraint (verified against acs-kernel-slice/src/kernel.ts):
///  - WorkItemId is externally minted by ACS; the harness NEVER mints it.
///  - Attempt terminal states are a strict subset of ACS's lifecycle vocabulary.
///  - "Completed" here means "worker finished and reported observations" — it is NOT a
///    success verdict. ACS rolls up success/failure. A harness that declared its own
///    Succeeded would break ACPX's no-self-attestation rule.
/// </summary>
public sealed record Attempt
{
    public Guid Id { get; init; }

    /// <summary>ACS work-item id. Required. The harness did not mint this.</summary>
    public Guid WorkItemId { get; init; }

    public Guid RunId { get; init; }
    public string AgentId { get; init; } = "";
    public AttemptState State { get; set; } = AttemptState.Created;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? WorkerId { get; set; }
    public string? OwnerToken { get; set; } // fencing token for durable claim

    public IReadOnlyList<AttemptEvent> Events => _events;
    private readonly List<AttemptEvent> _events = new();

    public void Lease(string ownerToken, DateTimeOffset expiresAt)
    {
        if (State != AttemptState.Created)
            throw new InvalidStateTransitionException($"Attempt {Id}: {State} -> Leased");
        State = AttemptState.Leased;
        OwnerToken = ownerToken;
        LeaseExpiresAt = expiresAt;
        Append(AttemptEventType.Leased, ownerToken);
    }

    public void Start(string workerId)
    {
        if (State != AttemptState.Leased)
            throw new InvalidStateTransitionException($"Attempt {Id}: {State} -> Started");
        State = AttemptState.Started;
        WorkerId = workerId;
        Append(AttemptEventType.Started, workerId);
    }

    public void MarkHeartbeating()
    {
        if (State != AttemptState.Started)
            throw new InvalidStateTransitionException($"Attempt {Id}: {State} -> Heartbeating");
        State = AttemptState.Heartbeating;
        Append(AttemptEventType.Heartbeating);
    }

    /// <summary>
    /// Worker finished and reported observations. NOT a success verdict.
    /// ACS consumes the reported RunEvents and transitions the work item.
    /// </summary>
    public void Complete()
    {
        if (State is not (AttemptState.Started or AttemptState.Heartbeating))
            throw new InvalidStateTransitionException($"Attempt {Id}: {State} -> Completed");
        State = AttemptState.Completed;
        Append(AttemptEventType.Completed);
    }

    public void ExpireLease()
    {
        if (State is AttemptState.Completed or AttemptState.LeaseExpired)
            throw new InvalidStateTransitionException($"Attempt {Id}: {State} -> LeaseExpired");
        State = AttemptState.LeaseExpired;
        Append(AttemptEventType.LeaseExpired);
    }

    /// <summary>Harness-reported observations. ACS rolls these up into the work-item verdict.</summary>
    public void ReportObservation(string detail) => Append(AttemptEventType.Observation, detail);

    private void Append(AttemptEventType type, string? detail = null) =>
        _events.Add(new AttemptEvent(type, DateTimeOffset.UtcNow, detail));
}

/// <summary>
/// Strict subset of ACS lifecycle states. No Succeeded — that verdict belongs to ACS.
/// </summary>
public enum AttemptState { Created, Leased, Started, Heartbeating, Completed, LeaseExpired }

public enum AttemptEventType { Leased, Started, Heartbeating, Completed, LeaseExpired, Observation }

public sealed record AttemptEvent(AttemptEventType Type, DateTimeOffset At, string? Detail);
