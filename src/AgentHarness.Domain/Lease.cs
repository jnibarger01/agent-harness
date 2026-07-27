namespace AgentHarness.Domain;

/// <summary>
/// Durable ownership of a session lane / attempt. Survives host restart.
/// Reclaim pass on startup claims any lease whose ExpiresAt &lt; now and whose
/// OwnerToken no longer matches (fencing-token check).
/// </summary>
public sealed record Lease
{
    public Guid Id { get; init; }
    public string PartitionKey { get; init; } = ""; // tenantId/channelId/conversationId
    public string OwnerToken { get; set; } = "";     // fencing token
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset AcquiredAt { get; init; }
    public LeaseKind Kind { get; init; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

public enum LeaseKind { Session, Attempt }
