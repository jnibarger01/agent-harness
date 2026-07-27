using System.Diagnostics;
using System.Globalization;

namespace AgentHarness.Isolation;

/// <summary>
/// Production boundary: one transient systemd scope (and therefore one cgroup) per attempt.
///
/// Kill semantics come from <c>cgroup.kill</c>, which terminates every process in the cgroup
/// and its descendants in one write. No tree walking, no reparenting escape, no race between
/// enumerating children and killing them — the failure mode <see cref="ProcessGroupIsolationBoundary"/>
/// cannot fully close.
///
/// Requires a systemd user session (<c>systemd-run --user</c>) and cgroup v2 with delegation.
/// Verify with the isolation smoke tool on the target host before trusting it: containers and
/// some desktop sessions do not provide it, and this class must fail loudly via
/// <see cref="IsolationBoundaryFactory"/> rather than silently degrade to something weaker.
/// </summary>
public sealed class CgroupV2IsolationBoundary : IIsolationBoundary
{
    private readonly string _scopeUnit;
    private readonly string _cgroupRoot;
    private Process? _process;
    private string? _spawnedProcessCgroupPath;

    public CgroupV2IsolationBoundary(string id, string cgroupRoot = "/sys/fs/cgroup")
    {
        Id = id;
        _scopeUnit = $"agent-harness-attempt-{id}.scope";
        _cgroupRoot = cgroupRoot;
    }

    public string Id { get; }

    public IsolationKind Kind => IsolationKind.CgroupV2;

    public static bool IsAvailable()
    {
        // cgroup.controllers only exists on a v2 hierarchy; systemd-run must be on PATH and
        // usable. A container can have the binary and hierarchy mounted while no user systemd
        // manager is running; selecting cgroups in that state makes the smoke probe falsely
        // claim the workload was contained.
        if (!File.Exists("/sys/fs/cgroup/cgroup.controllers")) return false;
        return TryWhich("systemd-run") && CanStartUserScope();
    }

    private static bool TryWhich(string tool)
    {
        try
        {
            var psi = new ProcessStartInfo("which") { UseShellExecute = false, RedirectStandardOutput = true };
            psi.ArgumentList.Add(tool);
            using var probe = Process.Start(psi);
            if (probe is null) return false;

            probe.WaitForExit(2000);
            return probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CanStartUserScope()
    {
        try
        {
            var psi = new ProcessStartInfo("systemd-run")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--user");
            psi.ArgumentList.Add("--scope");
            psi.ArgumentList.Add("--quiet");
            psi.ArgumentList.Add("--wait");
            psi.ArgumentList.Add("true");

            using var probe = Process.Start(psi);
            if (probe is null) return false;
            probe.WaitForExit(2000);
            return probe.HasExited && probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<IsolatedProcessHandle> StartAsync(IsolatedProcessSpec spec, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("systemd-run")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = spec.WorkingDirectory,
        };

        psi.ArgumentList.Add("--user");
        psi.ArgumentList.Add("--scope");
        psi.ArgumentList.Add("--quiet");
        psi.ArgumentList.Add("--collect");
        psi.ArgumentList.Add("--unit=" + _scopeUnit);

        if (spec.MemoryLimitBytes is { } memory)
            psi.ArgumentList.Add("--property=MemoryMax=" + memory.ToString(CultureInfo.InvariantCulture));

        if (spec.CpuQuota is { } cpu)
            psi.ArgumentList.Add("--property=CPUQuota=" + ((int)(cpu * 100)).ToString(CultureInfo.InvariantCulture) + "%");

        psi.ArgumentList.Add(spec.FileName);
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);

        foreach (var (key, value) in spec.Environment)
            psi.Environment[key] = value;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start systemd scope.");
        _process = process;
        _spawnedProcessCgroupPath = TryGetCgroupPath(process.Id, _cgroupRoot);

        var stdout = DrainAsync(process.StandardOutput, cancellationToken);
        var stderr = DrainAsync(process.StandardError, cancellationToken);
        var exited = WaitAsync(process, stdout, stderr);

        return Task.FromResult(new IsolatedProcessHandle(process.Id, process.StandardInput, exited));
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
        }
    }

    private static async Task<int> WaitAsync(Process process, Task stdout, Task stderr)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* boundary torn down while draining */ }

        return process.ExitCode;
    }

    /// <summary>
    /// Resolve the actual cgroup assigned to the spawned process. Do not reconstruct a systemd
    /// path from UID: shell UID is commonly not exported, and a fallback such as 1000 can point
    /// at a nonexistent cgroup while the workload is still running.
    /// </summary>
    public string? GetSpawnedProcessCgroupPath()
    {
        return _spawnedProcessCgroupPath ??
            (_process is null ? null : TryGetCgroupPath(_process.Id, _cgroupRoot));
    }

    public static string? TryGetCgroupPath(int pid, string cgroupRoot = "/sys/fs/cgroup")
    {
        try
        {
            var procPath = $"/proc/{pid}/cgroup";
            if (!File.Exists(procPath)) return null;

            var unified = File.ReadLines(procPath)
                .Select(line => line.Split(new[] { ':' }, 3))
                .Where(parts => parts.Length == 3 && parts[0] == "0" && parts[1].Length == 0)
                .Select(parts => parts[2].Trim())
                .FirstOrDefault(path => path.Length > 0);

            if (string.IsNullOrWhiteSpace(unified) || !unified.StartsWith('/'))
                return null;

            return Path.Combine(cgroupRoot, unified.TrimStart('/'));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task SignalAsync(TerminationSignal signal, CancellationToken cancellationToken)
    {
        var name = signal switch
        {
            TerminationSignal.Term => "SIGTERM",
            TerminationSignal.Int => "SIGINT",
            TerminationSignal.Kill => "SIGKILL",
            _ => "SIGTERM",
        };

        var psi = new ProcessStartInfo("systemctl") { UseShellExecute = false };
        psi.ArgumentList.Add("--user");
        psi.ArgumentList.Add("kill");
        psi.ArgumentList.Add("--signal=" + name);
        psi.ArgumentList.Add("--kill-whom=all");
        psi.ArgumentList.Add(_scopeUnit);

        using var kill = Process.Start(psi);
        if (kill is not null) await kill.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task KillAsync(CancellationToken cancellationToken)
    {
        // One write, whole subtree. This is the entire reason to prefer cgroups.
        var cgroupPath = GetSpawnedProcessCgroupPath();
        if (cgroupPath is not null)
        {
            var killFile = Path.Combine(cgroupPath, "cgroup.kill");
            if (File.Exists(killFile))
            {
                await File.WriteAllTextAsync(killFile, "1", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Keep the boundary-level fallback for hosts where systemd has already removed the file;
        // liveness verification remains fail-closed if the cgroup path cannot be resolved.
        await SignalAsync(TerminationSignal.Kill, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasLiveProcessesAsync(CancellationToken cancellationToken)
    {
        var cgroupPath = GetSpawnedProcessCgroupPath();
        if (cgroupPath is null) return true;

        try
        {
            var procsFile = Path.Combine(cgroupPath, "cgroup.procs");
            if (!File.Exists(procsFile)) return true;

            var content = await File.ReadAllTextAsync(procsFile, cancellationToken).ConfigureAwait(false);
            return content.AsSpan().Trim().Length > 0;
        }
        catch (Exception)
        {
            // Cannot determine is live. A false clean result would release the lane while the
            // workload still owns it.
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await KillAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* best effort; the scope may already be collected */ }

        _process?.Dispose();
        _process = null;
        _spawnedProcessCgroupPath = null;
    }
}
