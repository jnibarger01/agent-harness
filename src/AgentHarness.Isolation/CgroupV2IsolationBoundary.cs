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
        // cgroup.controllers only exists on a v2 hierarchy; systemd-run must be on PATH.
        if (!File.Exists("/sys/fs/cgroup/cgroup.controllers")) return false;
        return TryWhich("systemd-run");
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

        var stdoutCapture = OutputCapture.CaptureAsync(process.StandardOutput, cancellationToken);
        var stderrCapture = OutputCapture.CaptureAsync(process.StandardError, cancellationToken);
        var exited = WaitAsync(process, stdoutCapture, stderrCapture);
        var output = OutputCapture.BuildResultAsync(stdoutCapture, stderrCapture);

        return Task.FromResult(new IsolatedProcessHandle(process.Id, process.StandardInput, exited, output));
    }

    private static async Task<int> WaitAsync(
        Process process, Task<(string Text, bool Truncated)> stdout, Task<(string Text, bool Truncated)> stderr)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    private string ScopePath =>
        Path.Combine(_cgroupRoot, "user.slice", $"user-{GetUid()}.slice", $"user@{GetUid()}.service", "app.slice", _scopeUnit);

    private static string GetUid() =>
        Environment.GetEnvironmentVariable("UID")
        ?? Environment.GetEnvironmentVariable("SUDO_UID")
        ?? "1000";

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
        var killFile = Path.Combine(ScopePath, "cgroup.kill");
        if (File.Exists(killFile))
        {
            await File.WriteAllTextAsync(killFile, "1", cancellationToken).ConfigureAwait(false);
            return;
        }

        await SignalAsync(TerminationSignal.Kill, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasLiveProcessesAsync(CancellationToken cancellationToken)
    {
        var procsFile = Path.Combine(ScopePath, "cgroup.procs");
        if (!File.Exists(procsFile)) return false; // scope collected => nothing left, confirmed not assumed.

        var content = await File.ReadAllTextAsync(procsFile, cancellationToken).ConfigureAwait(false);
        return content.AsSpan().Trim().Length > 0;
    }

    public async ValueTask DisposeAsync()
    {
        try { await KillAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* best effort; the scope may already be collected */ }

        _process?.Dispose();
        _process = null;
    }
}
