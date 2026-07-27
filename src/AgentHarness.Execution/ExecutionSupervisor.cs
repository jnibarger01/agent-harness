using AgentHarness.Domain;
using AgentHarness.Isolation;
using AgentHarness.Persistence;

namespace AgentHarness.Execution;

/// <summary>
/// The wedge-killer.
///
/// Responsibilities (Fork A):
///  - Spawn worker processes (out-of-process execution; never embedded).
///  - Assign attempt IDs, track a killable <see cref="IIsolationBoundary"/> (cgroup or process
///    group — never a bare Process handle).
///  - Require heartbeats; enforce startup/idle/absolute deadlines.
///  - Cancel via the staged termination ladder (<see cref="StagedTermination"/>): cooperative
///    CancellationToken -> protocol cancel -> grace -> SIGTERM -> grace -> KILL the boundary ->
///    verify no processes remain. This replaces the earlier placeholder whose
///    `NativeKillProcessGroup`/`NativeSetProcessGroup` methods were empty comments; those, like
///    `Process.Kill(entireProcessTree:true)`, do NOT reliably reap a detached grandchild on
///    Linux — exactly the wedge this class exists to prevent.
///  - Expire + reclaim leases; kill the worker when its lease expires.
/// </summary>
public sealed class ExecutionSupervisor : IAsyncDisposable
{
    private readonly IHarnessStore _store;
    private readonly IsolationBoundaryFactory _isolationFactory;
    private readonly StagedTermination _termination;
    private readonly Dictionary<Guid, WorkerHandle> _workers = new();
    private readonly object _gate = new();

    /// <param name="isolationFactory">
    /// Defaults to allowing the process-group fallback so this scaffold runs on a plain dev box
    /// with no cgroup v2 delegation. Pass an <see cref="IsolationBoundaryFactory"/> constructed
    /// with <c>allowProcessGroupFallback: false</c> on a host where cgroups are expected — a
    /// silent downgrade there is worse than refusing to start.
    /// </param>
    public ExecutionSupervisor(
        IHarnessStore store,
        IsolationBoundaryFactory? isolationFactory = null,
        TimeSpan? protocolGrace = null,
        TimeSpan? sigtermGrace = null,
        TimeSpan? verifyTimeout = null)
    {
        _store = store;
        _isolationFactory = isolationFactory ?? new IsolationBoundaryFactory(allowProcessGroupFallback: true);
        _termination = new StagedTermination(protocolGrace, sigtermGrace, verifyTimeout);
    }

    public IsolationKind SelectedIsolationKind => _isolationFactory.SelectedKind;

    /// <summary>
    /// Spawns a worker for an already-leased Attempt inside a fresh isolation boundary. The
    /// worker is a separate process; the supervisor never awaits its execution stack.
    /// </summary>
    public async Task<WorkerHandle> SpawnAsync(Attempt attempt, string workerBinary, CancellationToken ct)
    {
        var boundary = _isolationFactory.Create(attempt.Id.ToString("N"));
        var spec = new IsolatedProcessSpec(
            FileName: workerBinary,
            Arguments: new[] { "--attempt", attempt.Id.ToString() },
            WorkingDirectory: Environment.CurrentDirectory,
            Environment: new Dictionary<string, string>());

        IsolatedProcessHandle process;
        try
        {
            process = await boundary.StartAsync(spec, ct).ConfigureAwait(false);
        }
        catch
        {
            await boundary.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var handle = new WorkerHandle(attempt.Id, boundary, process);
        lock (_gate) _workers[attempt.Id] = handle;
        return handle;
    }

    /// <summary>
    /// Runs the staged termination ladder against a live attempt's boundary and removes it from
    /// tracking. Returns which rung was reached — journal this; if production is routinely
    /// reaching "Killed", cooperative cancellation is decorative and you should know that.
    /// </summary>
    public async Task<TerminationOutcome> CancelAsync(
        Guid attemptId,
        Func<CancellationToken, Task>? sendProtocolCancel,
        CancellationToken ct)
    {
        WorkerHandle? handle;
        lock (_gate) _workers.Remove(attemptId, out handle);
        if (handle is null)
            return new TerminationOutcome(true, TimeSpan.Zero, new[] { "AlreadyGone" });

        try
        {
            return await _termination
                .TerminateAsync(handle.Boundary, handle.Process.Exited, sendProtocolCancel, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            await handle.Boundary.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reclaim pass on startup: any expired lease -> kill its worker's boundary (if still alive).</summary>
    public async Task ReclaimExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await _store.ExpiredLeasesAsync(now, ct);
        foreach (var lease in expired)
        {
            if (lease.Kind != LeaseKind.Attempt) continue;
            if (!Guid.TryParse(lease.PartitionKey, out var attemptId)) continue;

            var attempt = await _store.GetAttemptAsync(attemptId, ct);
            if (attempt is null) continue;

            await CancelAsync(attemptId, sendProtocolCancel: null, ct).ConfigureAwait(false);
            attempt.ExpireLease();
            await _store.SaveAttemptAsync(attempt, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        WorkerHandle[] handles;
        lock (_gate) handles = _workers.Values.ToArray();

        foreach (var handle in handles)
        {
            try { await handle.Boundary.KillAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort teardown; disposal below still runs */ }
            await handle.Boundary.DisposeAsync().ConfigureAwait(false);
        }

        lock (_gate) _workers.Clear();
    }
}

public sealed record WorkerHandle(Guid AttemptId, IIsolationBoundary Boundary, IsolatedProcessHandle Process);
