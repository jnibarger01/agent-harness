using AgentHarness.Domain;
using AgentHarness.Persistence;
using Xunit;

namespace AgentHarness.PersistenceTests;

/// <summary>
/// Lease and reclaim behaviour against a real SQLite file, not a fake — these are the tests
/// that close the "released=0" class of bug the wedge produced, so they exercise the actual
/// conditioned SQL rather than an in-memory stand-in.
/// </summary>
public class LeaseFencingTests
{
    private static string TempDb([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        $"Data Source=lease_{name}_{Guid.NewGuid():N}.db";

    [Fact]
    public async Task Acquire_WhileHeldAndUnexpired_ReturnsNull()
    {
        using var store = new SqliteHarnessStore(TempDb());

        var first = await store.TryAcquireLeaseAsync("lane-1", LeaseKind.Session, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(first);

        var second = await store.TryAcquireLeaseAsync("lane-1", LeaseKind.Session, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Null(second); // an unexpired lease must not be stolen
    }

    [Fact]
    public async Task SecondAcquire_AfterExpiry_IssuesHigherFencingToken()
    {
        using var store = new SqliteHarnessStore(TempDb());

        var first = await store.TryAcquireLeaseAsync("lane-2", LeaseKind.Session, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.NotNull(first);

        await Task.Delay(20); // let the first lease expire

        var second = await store.TryAcquireLeaseAsync("lane-2", LeaseKind.Session, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(second);

        var firstToken = long.Parse(first!.OwnerToken);
        var secondToken = long.Parse(second!.OwnerToken);
        Assert.True(secondToken > firstToken, "reacquisition after expiry must issue a strictly higher fencing token");
    }

    [Fact]
    public async Task Renew_WithCurrentToken_Succeeds()
    {
        using var store = new SqliteHarnessStore(TempDb());

        var lease = await store.TryAcquireLeaseAsync("lane-3", LeaseKind.Attempt, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(lease);

        var renewed = await store.TryRenewLeaseAsync("lane-3", lease!.OwnerToken, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(renewed);
        Assert.True(renewed!.ExpiresAt > lease.ExpiresAt);
    }

    [Fact]
    public async Task Renew_WithSupersededToken_Fails()
    {
        using var store = new SqliteHarnessStore(TempDb());

        // Worker A acquires, then wedges (its token is never renewed).
        var ownerA = await store.TryAcquireLeaseAsync("lane-4", LeaseKind.Attempt, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.NotNull(ownerA);

        await Task.Delay(20); // A's lease expires

        // Worker B claims the now-expired lease with a fresh, higher fencing token.
        var ownerB = await store.TryAcquireLeaseAsync("lane-4", LeaseKind.Attempt, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(ownerB);

        // A wakes up and tries to renew with its stale token. This must fail — a renewal that
        // silently resurrected A's lease would let a wedged worker overwrite B's work.
        var staleRenew = await store.TryRenewLeaseAsync("lane-4", ownerA!.OwnerToken, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Null(staleRenew);
    }

    [Fact]
    public async Task Renew_AfterExpiry_FailsEvenBeforeReclaim()
    {
        using var store = new SqliteHarnessStore(TempDb());

        var lease = await store.TryAcquireLeaseAsync("lane-lapsed", LeaseKind.Attempt, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.NotNull(lease);
        await Task.Delay(20);

        var lapsedRenew = await store.TryRenewLeaseAsync("lane-lapsed", lease!.OwnerToken, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Null(lapsedRenew);
    }

    [Fact]
    public async Task Renew_AfterAttemptTerminalState_Fails()
    {
        using var store = new SqliteHarnessStore(TempDb());
        var attemptId = Guid.NewGuid();
        var attempt = new Attempt
        {
            Id = attemptId,
            WorkItemId = Guid.NewGuid(),
            RunId = Guid.NewGuid(),
            AgentId = "test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        attempt.Lease("pending", DateTimeOffset.UtcNow.AddMinutes(5));
        attempt.Start("worker");
        attempt.Complete();
        await store.SaveAttemptAsync(attempt, CancellationToken.None);

        var lease = await store.TryAcquireLeaseAsync(attemptId.ToString(), LeaseKind.Attempt, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(lease);

        var renewal = await store.TryRenewLeaseAsync(attemptId.ToString(), lease!.OwnerToken, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Null(renewal);
    }

    [Fact]
    public async Task Release_WithSupersededToken_Fails()
    {
        using var store = new SqliteHarnessStore(TempDb());

        var lease = await store.TryAcquireLeaseAsync("lane-5", LeaseKind.Session, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(lease);

        var releasedWithWrongToken = await store.TryReleaseLeaseAsync("lane-5", "not-the-real-token", CancellationToken.None);
        Assert.False(releasedWithWrongToken);

        var releasedForReal = await store.TryReleaseLeaseAsync("lane-5", lease!.OwnerToken, CancellationToken.None);
        Assert.True(releasedForReal);
    }

    [Fact]
    public async Task ExpiredLeasesAsync_ReturnsOnlyExpiredLeases()
    {
        using var store = new SqliteHarnessStore(TempDb());

        await store.TryAcquireLeaseAsync("expired-lane", LeaseKind.Attempt, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        await store.TryAcquireLeaseAsync("live-lane", LeaseKind.Attempt, TimeSpan.FromMinutes(5), CancellationToken.None);

        await Task.Delay(20);

        var expired = await store.ExpiredLeasesAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Contains(expired, l => l.PartitionKey == "expired-lane");
        Assert.DoesNotContain(expired, l => l.PartitionKey == "live-lane");
    }
}
