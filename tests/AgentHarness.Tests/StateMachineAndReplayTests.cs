using AgentHarness.Domain;
using AgentHarness.Persistence;
using Xunit;

namespace AgentHarness.Domain.Tests
{
    public class StateMachineTests
    {
        [Fact]
        public void Turn_accept_then_reject_is_invalid()
        {
            var turn = new Turn { Id = Guid.NewGuid(), Channel = "telegram", InboundKey = "k1", CreatedAt = DateTimeOffset.UtcNow };
            turn.Accept();
            Assert.Throws<InvalidStateTransitionException>(() => turn.Reject("nope"));
        }

        [Fact]
        public void Attempt_cannot_self_attest_succeeded()
        {
            // There is NO Succeeded state on Attempt — ACS owns the verdict.
            var attempt = new Attempt { Id = Guid.NewGuid(), WorkItemId = Guid.NewGuid(), RunId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
            attempt.Lease("owner1", DateTimeOffset.UtcNow.AddMinutes(5));
            attempt.Start("worker-1");
            attempt.MarkHeartbeating();
            // Only Completed (observations reported) is reachable — never Succeeded.
            attempt.Complete();
            Assert.Equal(AttemptState.Completed, attempt.State);
            Assert.False(Enum.GetNames<AttemptState>().Contains("Succeeded"));
        }

        [Fact]
        public void Attempt_requires_external_work_item_id()
        {
            var externalWorkItemId = Guid.NewGuid();
            var attempt = new Attempt { Id = Guid.NewGuid(), WorkItemId = externalWorkItemId, RunId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };

            // The work-item identity is minted by ACS and must pass through unchanged.
            Assert.NotEqual(Guid.Empty, externalWorkItemId);
            Assert.Equal(externalWorkItemId, attempt.WorkItemId);
        }
    }
}

namespace AgentHarness.IntegrationTests
{
    /// <summary>
    /// Failure test #1 from the architecture spec:
    /// "Host crashes after accepting a message but before dispatching it."
    /// The durable inbox must let a restarted process REPLAY the accepted Turn and dispatch it
    /// exactly once (idempotency), with no loss and no duplication.
    /// </summary>
    public class InboxReplayTests
    {
        [Fact]
        public async Task Crashed_before_dispatch_is_replayed_once()
        {
            var db = $"Data Source=replay_{Guid.NewGuid():N}.db";
            // Simulate "accept" on the first (crashing) instance.
            await using (var store1 = new SqliteHarnessStore(db))
            {
                var turn = await store1.AcceptAsync("telegram", "update-42", "hello", CancellationToken.None);
                turn.MarkRunning();
                // CRASH happens here — before any Run/Attempt is created.
            }

            // Restart: a fresh store (simulating process restart) must see the accepted Turn.
            await using var store2 = new SqliteHarnessStore(db);
            var pending = new List<Turn>();
            await foreach (var t in store2.PendingAsync(CancellationToken.None))
                pending.Add(t);

            Assert.Single(pending);
            Assert.Equal("update-42", pending[0].InboundKey);

            // Dispatch exactly once.
            var run = new Run { Id = Guid.NewGuid(), TurnId = pending[0].Id, CreatedAt = DateTimeOffset.UtcNow };
            run.Dispatch();
            await store2.SaveRunAsync(run, CancellationToken.None);

            // A second replay pass must NOT find it again (it's no longer 'Accepted').
            var second = new List<Turn>();
            await foreach (var t in store2.PendingAsync(CancellationToken.None))
                second.Add(t);
            Assert.Empty(second);
        }

        [Fact]
        public async Task Duplicate_inbound_is_idempotent()
        {
            var db = $"Data Source=dup_{Guid.NewGuid():N}.db";
            await using var store = new SqliteHarnessStore(db);
            var a = await store.AcceptAsync("telegram", "update-7", "hi", CancellationToken.None);
            var b = await store.AcceptAsync("telegram", "update-7", "hi", CancellationToken.None);
            Assert.Equal(a.Id, b.Id); // same Turn, no duplicate
        }
    }
}
