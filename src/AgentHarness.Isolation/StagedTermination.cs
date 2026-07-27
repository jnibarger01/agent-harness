using System.Diagnostics;

namespace AgentHarness.Isolation;

/// <summary>
/// The termination ladder. Each rung assumes the previous one failed. This is the concrete,
/// verifiable version of the "two-tier cancel" principle: cooperative first, kill the boundary
/// (never a lone <c>Process.Kill</c>) second, and — critically — a rung that verifies rather
/// than assumes the workload is gone.
///
///   1. deadline expires                (caller)
///   2. cancel the attempt token        cooperative, in-process
///   3. protocol-level cancel           cooperative, cross-process
///   4. bounded grace
///   5. SIGTERM to the boundary
///   6. bounded grace
///   7. kill the boundary               cgroup.kill / SIGKILL to the group
///   8. verify no processes remain
///   9. expire lease + record evidence  (caller)
///
/// A CancellationToken is a request, not a guarantee. Steps 2-3 are the polite ask; everything
/// below is why the wedge (aborted=false drained=false forceCleared=true released=0) cannot
/// recur.
/// </summary>
public sealed class StagedTermination
{
    private readonly TimeSpan _protocolGrace;
    private readonly TimeSpan _sigtermGrace;
    private readonly TimeSpan _verifyTimeout;

    public StagedTermination(TimeSpan? protocolGrace = null, TimeSpan? sigtermGrace = null, TimeSpan? verifyTimeout = null)
    {
        _protocolGrace = protocolGrace ?? TimeSpan.FromSeconds(5);
        _sigtermGrace = sigtermGrace ?? TimeSpan.FromSeconds(5);
        _verifyTimeout = verifyTimeout ?? TimeSpan.FromSeconds(2);
    }

    /// <param name="sendProtocolCancel">
    /// Step 3. May legitimately fail — the worker's stdin is often the first thing to die — so
    /// failure is recorded and the ladder continues rather than aborting.
    /// </param>
    public async Task<TerminationOutcome> TerminateAsync(
        IIsolationBoundary boundary,
        Task<int> workloadExited,
        Func<CancellationToken, Task>? sendProtocolCancel,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var stages = new List<string> { nameof(TerminationStageName.TokenCancelled) };

        if (sendProtocolCancel is not null)
        {
            try
            {
                await sendProtocolCancel(cancellationToken).ConfigureAwait(false);
                stages.Add(nameof(TerminationStageName.ProtocolCancelSent));
            }
            catch (Exception)
            {
                stages.Add("ProtocolCancelFailed");
            }

            if (await ExitedWithinAsync(workloadExited, _protocolGrace).ConfigureAwait(false))
                return Done(stopwatch, stages, confirmedGone: true);
        }

        await boundary.SignalAsync(TerminationSignal.Term, cancellationToken).ConfigureAwait(false);
        stages.Add(nameof(TerminationStageName.SigTermSent));

        if (await ExitedWithinAsync(workloadExited, _sigtermGrace).ConfigureAwait(false))
        {
            // Exit alone is not proof: the leader can exit while children survive.
            if (!await HasLiveAsync(boundary).ConfigureAwait(false))
                return Done(stopwatch, stages, confirmedGone: true);
        }

        await boundary.KillAsync(cancellationToken).ConfigureAwait(false);
        stages.Add(nameof(TerminationStageName.Killed));

        var gone = await WaitForNoProcessesAsync(boundary).ConfigureAwait(false);
        if (!gone) stages.Add(nameof(TerminationStageName.KillVerificationFailed));

        return Done(stopwatch, stages, gone);
    }

    private async Task<bool> WaitForNoProcessesAsync(IIsolationBoundary boundary)
    {
        var deadline = DateTimeOffset.UtcNow + _verifyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await HasLiveAsync(boundary).ConfigureAwait(false)) return true;
            await Task.Delay(50).ConfigureAwait(false);
        }

        return !await HasLiveAsync(boundary).ConfigureAwait(false);
    }

    private static async Task<bool> HasLiveAsync(IIsolationBoundary boundary)
    {
        try
        {
            return await boundary.HasLiveProcessesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Unverifiable is not the same as clean. Report live so the caller journals
            // KillVerificationFailed instead of a false all-clear.
            return true;
        }
    }

    private static async Task<bool> ExitedWithinAsync(Task<int> exited, TimeSpan grace)
    {
        var completed = await Task.WhenAny(exited, Task.Delay(grace)).ConfigureAwait(false);
        return completed == exited;
    }

    private static TerminationOutcome Done(Stopwatch stopwatch, List<string> stages, bool confirmedGone)
    {
        stopwatch.Stop();
        return new TerminationOutcome(confirmedGone, stopwatch.Elapsed, stages);
    }

    private enum TerminationStageName
    {
        TokenCancelled,
        ProtocolCancelSent,
        SigTermSent,
        Killed,
        KillVerificationFailed,
    }
}
