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
    Task SaveLeaseAsync(Lease lease, CancellationToken ct);
    Task<IReadOnlyList<Lease>> ExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct);
    Task<Conversation> GetOrCreateConversationAsync(string channel, string externalId, CancellationToken ct);
}
