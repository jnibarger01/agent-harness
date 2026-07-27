namespace AgentHarness.Isolation;

/// <summary>
/// Selects a boundary and refuses to downgrade silently.
///
/// <paramref name="allowProcessGroupFallback"/> lets a host without cgroup v2 delegation start
/// anyway. Set it false when the environment is expected to have cgroups: quietly running with
/// a weaker boundary is worse than failing to start, because a silent downgrade is only
/// discovered during the incident this boundary exists to prevent.
/// </summary>
public sealed class IsolationBoundaryFactory
{
    private readonly bool _allowProcessGroupFallback;
    private readonly bool _cgroupsAvailable;

    public IsolationBoundaryFactory(bool allowProcessGroupFallback = true)
    {
        _allowProcessGroupFallback = allowProcessGroupFallback;
        _cgroupsAvailable = CgroupV2IsolationBoundary.IsAvailable();
    }

    public IsolationKind SelectedKind => _cgroupsAvailable ? IsolationKind.CgroupV2 : IsolationKind.ProcessGroup;

    public IIsolationBoundary Create(string attemptId)
    {
        if (_cgroupsAvailable)
            return new CgroupV2IsolationBoundary(attemptId);

        if (!_allowProcessGroupFallback)
        {
            throw new InvalidOperationException(
                "cgroup v2 isolation unavailable and process-group fallback is disabled. " +
                "Only allow the fallback if you accept that a workload calling setsid() cannot be contained.");
        }

        return new ProcessGroupIsolationBoundary(attemptId);
    }
}
