using AgentHarness.Domain;

namespace AgentHarness.Persistence;

/// <summary>Durable inbox. NOT Channel&lt;T&gt; as authority. Survives host crash.</summary>
public interface IInbox
{
    /// <summary>Idempotent accept. Returns existing Turn if inboundKey already seen.</summary>
    Task<Turn> AcceptAsync(string channel, string inboundKey, string payload, CancellationToken ct);
    Task<Turn?> GetAsync(Guid turnId, CancellationToken ct);
    IAsyncEnumerable<Turn> PendingAsync(CancellationToken ct);
}

/// <summary>Durable outbox for deliveries. Triggered by Run state transition.</summary>
public interface IOutbox
{
    Task<Delivery> EnqueueAsync(Guid runId, string channel, string outboundKey, string payload, CancellationToken ct);
    IAsyncEnumerable<Delivery> PendingAsync(CancellationToken ct);
    Task MarkSentAsync(Guid deliveryId, CancellationToken ct);
}

/// <summary>Durable store for runs/attempts/leases/conversations.</summary>
public interface IHarnessStore
{
    Task SaveRunAsync(Run run, CancellationToken ct);
    Task<Run?> GetRunAsync(Guid runId, CancellationToken ct);
    Task SaveAttemptAsync(Attempt attempt, CancellationToken ct);
    Task<Attempt?> GetAttemptAsync(Guid attemptId, CancellationToken ct);
    Task<IReadOnlyList<Attempt>> AttemptsForRunAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// Acquire the lease for <paramref name="partitionKey"/>. Fails (returns null) if another
    /// owner's lease over the same key has not yet expired — an unexpired lease must never be
    /// stolen. The returned <see cref="Lease.OwnerToken"/> is a monotonically increasing fencing
    /// token: present it to <see cref="TryRenewLeaseAsync"/>/<see cref="TryReleaseLeaseAsync"/>.
    /// </summary>
    Task<Lease?> TryAcquireLeaseAsync(string partitionKey, LeaseKind kind, TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Extend a held lease. Conditioned on the fencing token in the SQL WHERE clause, not on the
    /// caller's belief that it still owns the lease — a wedged holder waking after its lease
    /// expired and being renewed anyway is exactly the "released=0" class of bug. Zero affected
    /// rows (null result) means ownership was already lost to a later acquirer.
    /// </summary>
    Task<Lease?> TryRenewLeaseAsync(string partitionKey, string ownerToken, TimeSpan ttl, CancellationToken ct);

    /// <summary>Release a held lease. Conditioned on the fencing token; a no-op if already superseded.</summary>
    Task<bool> TryReleaseLeaseAsync(string partitionKey, string ownerToken, CancellationToken ct);

    /// <summary>Reclaim pass: leases whose ExpiresAt has passed, regardless of holder.</summary>
    Task<IReadOnlyList<Lease>> ExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct);
    Task<Conversation> GetOrCreateConversationAsync(string channel, string externalId, CancellationToken ct);
}
