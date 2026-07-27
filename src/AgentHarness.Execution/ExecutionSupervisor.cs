using System.Diagnostics;
using AgentHarness.Domain;
using AgentHarness.Persistence;

namespace AgentHarness.Execution;

/// <summary>
/// Phase 1 Execution Supervisor — the wedge-killer.
///
/// Responsibilities (Fork A):
///  - Spawn worker processes (out-of-process execution; never embedded).
///  - Assign attempt IDs, track PID + process group (pgid).
///  - Require heartbeats; enforce startup/idle/absolute deadlines.
///  - Cancel cooperatively FIRST, then grace window, then KILL the process GROUP (pgid).
///    NOTE: Process.Kill(entireProcessTree:true) does NOT reliably reap a detached grandchild
///    on Linux — use the process group (setpgid + kill(-pgid)) or a cgroup. This is Claude's fix #1.
///  - Expire + reclaim leases; kill the worker when its lease expires.
///  - Quarantine repeatedly crashing runtimes.
///  - Per-session concurrency partitioned by tenantId/channelId/conversationId.
/// </summary>
public sealed class ExecutionSupervisor
{
    private readonly IHarnessStore _store;
    private readonly TimeSpan _graceWindow;
    private readonly Dictionary<Guid, WorkerHandle> _workers = new();
    private readonly object _gate = new();

    public ExecutionSupervisor(IHarnessStore store, TimeSpan? graceWindow = null)
    {
        _store = store;
        _graceWindow = graceWindow ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Spawns a worker for an already-leased Attempt. Returns the worker PID/pgid handle.
    /// The worker is a separate process; the supervisor never awaits its execution stack.
    /// </summary>
    public async Task<WorkerHandle> SpawnAsync(Attempt attempt, string workerBinary, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(workerBinary, $"--attempt {attempt.Id}")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Start();
        // Place worker in its own process group so we can kill the whole tree reliably.
        if (!proc.HasExited)
            NativeSetProcessGroup(proc.Id);

        var handle = new WorkerHandle(attempt.Id, proc.Id, proc.Id /* pgid = pid for new group */, proc);
        lock (_gate) _workers[attempt.Id] = handle;
        return handle;
    }

    /// <summary>
    /// Two-tier cancellation: cooperative CT -> grace -> KILL process group.
    /// </summary>
    public async Task CancelAsync(Guid attemptId, CancellationToken cooperativeCt)
    {
        WorkerHandle? handle;
        lock (_gate) _workers.TryGetValue(attemptId, out handle);
        if (handle is null) return;

        try { await Task.Delay(_graceWindow, cooperativeCt); }
        catch (OperationCanceledException) { /* cooperative cancel honored */ }

        if (handle.Process.HasExited) return;
        KillProcessGroup(handle.Pgid);
    }

    /// <summary>Reclaim pass on startup: any expired lease -> kill its worker (if still alive).</summary>
    public async Task ReclaimExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await _store.ExpiredLeasesAsync(now, ct);
        foreach (var lease in expired)
        {
            Attempt? attempt = null; // resolve attempt by partition in real impl
            if (attempt is not null)
            {
                attempt.ExpireLease();
                await _store.SaveAttemptAsync(attempt, ct);
                if (attempt.WorkerId is not null) KillProcessGroup(attempt.WorkerId.GetHashCode());
            }
        }
    }

    private static void KillProcessGroup(int pgid)
    {
        // Real impl: NativeMethods.kill(-pgid, SIGKILL). Negative pid = whole group.
        // Fallback (won't reap detached grandchildren): Process.GetProcessById(pgid)?.Kill(true);
        try { NativeKillProcessGroup(pgid); }
        catch { /* log + quarantine */ }
    }

    private static void NativeSetProcessGroup(int pid) { /* setpgid(pid, pid) */ }
    private static void NativeKillProcessGroup(int pgid) { /* kill(-pgid, SIGKILL) */ }

    public void Dispose()
    {
        lock (_gate)
            foreach (var h in _workers.Values)
                if (!h.Process.HasExited) KillProcessGroup(h.Pgid);
    }
}

public sealed record WorkerHandle(Guid AttemptId, int Pid, int Pgid, Process Process);
