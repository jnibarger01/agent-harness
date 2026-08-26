namespace AgentHarness.Execution;

/// <summary>
/// Standardized terminal envelope for one worker execution: what happened, not whether it
/// succeeded. Mirrors <see cref="AgentHarness.Domain.Attempt"/>'s no-self-attestation rule —
/// ACS rolls <see cref="ExitCode"/>/<see cref="Stdout"/>/<see cref="Stderr"/> up into a verdict;
/// the harness only reports observations.
/// </summary>
/// <param name="ExitCode">
/// Null when the process's exit could not be confirmed (e.g. <see cref="Reason"/> is
/// <see cref="AttemptResultReason.Terminated"/> and the staged termination ladder reached
/// <c>KillVerificationFailed</c> — see <see cref="ProcessesConfirmedGone"/>).
/// </param>
/// <param name="StdoutTruncated">True if stdout exceeded the bounded capture size and was cut off.</param>
/// <param name="StderrTruncated">True if stderr exceeded the bounded capture size and was cut off.</param>
/// <param name="TerminationStages">
/// Empty for a graceful exit. For a forced termination, the staged-termination rungs entered
/// (see <see cref="AgentHarness.Isolation.StagedTermination"/>) — journal this: routinely
/// reaching "Killed" means cooperative cancellation is decorative.
/// </param>
/// <param name="ProcessesConfirmedGone">
/// False means the boundary could not verify the workload is gone. Not the same as "still
/// running" — it means unverifiable, which must not be reported as clean.
/// </param>
public sealed record AttemptResult(
    Guid AttemptId,
    AttemptResultReason Reason,
    int? ExitCode,
    string Stdout,
    bool StdoutTruncated,
    string Stderr,
    bool StderrTruncated,
    TimeSpan Duration,
    IReadOnlyList<string> TerminationStages,
    bool ProcessesConfirmedGone);

/// <summary>Why the attempt stopped running. Not a verdict — see <see cref="AgentHarness.Domain.Attempt"/>.</summary>
public enum AttemptResultReason
{
    /// <summary>Worker process exited on its own (any exit code).</summary>
    Exited,

    /// <summary>Stopped via the staged termination ladder (deadline, explicit cancel, or lease reclaim).</summary>
    Terminated,
}
