namespace AgentHarness.Isolation;

/// <summary>
/// A killable container for one attempt's workload.
///
/// The whole point: <c>Process.Kill(entireProcessTree: true)</c> walks a tree it reconstructs
/// from parent pids, and the .NET docs themselves note the parent can be reported exited while
/// descendants have not. A double-forked or reparented child escapes the walk. That silent
/// escape is the wedge this project exists to prevent (a live Telegram lane froze with
/// aborted=false drained=false forceCleared=true released=0 because execution fell back to an
/// embedded, non-killable runtime). A boundary implementation must not have that failure mode:
/// membership is a property of the process (group or cgroup), not a relationship the killer
/// has to infer.
/// </summary>
public interface IIsolationBoundary : IAsyncDisposable
{
    string Id { get; }

    IsolationKind Kind { get; }

    Task<IsolatedProcessHandle> StartAsync(IsolatedProcessSpec spec, CancellationToken cancellationToken);

    Task SignalAsync(TerminationSignal signal, CancellationToken cancellationToken);

    /// <summary>Unconditional. cgroup.kill for cgroup boundaries, SIGKILL to the process group for the fallback.</summary>
    Task KillAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Verification, not optimism. The staged termination ladder's last rung exists because
    /// every earlier rung can "succeed" while a process survives.
    /// </summary>
    Task<bool> HasLiveProcessesAsync(CancellationToken cancellationToken);
}

public enum IsolationKind
{
    /// <summary>cgroup v2 scope. The production boundary.</summary>
    CgroupV2,

    /// <summary>Process group via setsid. Acceptable fallback; cannot contain a process that calls setsid itself.</summary>
    ProcessGroup,
}

public enum TerminationSignal { Term, Int, Kill }

public sealed record IsolatedProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    long? MemoryLimitBytes = null,
    double? CpuQuota = null);

public sealed record IsolatedProcessHandle(int Pid, StreamWriter StandardInput, Task<int> Exited);

public sealed record TerminationOutcome(
    bool ProcessesConfirmedGone,
    TimeSpan Duration,
    IReadOnlyList<string> StagesEntered);
