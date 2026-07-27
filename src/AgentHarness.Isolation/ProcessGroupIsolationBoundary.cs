using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentHarness.Isolation;

/// <summary>
/// Fallback boundary: launch under <c>setsid</c> so the workload gets its own process group,
/// then signal the negative pgid so every member receives it. This replaces the
/// ExecutionSupervisor's original stub native calls with a real implementation.
///
/// Honest limitation: a workload that calls <c>setsid()</c> itself leaves this group and
/// becomes unkillable by this boundary. Use <see cref="CgroupV2IsolationBoundary"/> in
/// production and treat this as the degraded path (see <see cref="IsolationBoundaryFactory"/>).
/// </summary>
public sealed class ProcessGroupIsolationBoundary : IIsolationBoundary
{
    private readonly List<Process> _started = new();
    private int _pgid;

    public ProcessGroupIsolationBoundary(string id) => Id = id;

    public string Id { get; }

    public IsolationKind Kind => IsolationKind.ProcessGroup;

    public async Task<IsolatedProcessHandle> StartAsync(IsolatedProcessSpec spec, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "setsid",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = spec.WorkingDirectory,
        };

        // setsid --wait keeps the intermediate alive so its pid is a usable pgid handle and the
        // exit code propagates.
        psi.ArgumentList.Add("--wait");
        psi.ArgumentList.Add(spec.FileName);
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);

        foreach (var (key, value) in spec.Environment)
            psi.Environment[key] = value;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start isolated process.");
        _started.Add(process);

        // Drain both pipes immediately. A full stdout buffer blocks the child forever, and the
        // resulting hang is indistinguishable from a model stall.
        var stdout = DrainAsync(process.StandardOutput, cancellationToken);
        var stderr = DrainAsync(process.StandardError, cancellationToken);
        var exited = WaitAsync(process, stdout, stderr);

        // `setsid --wait` FORKS: the new session leader is its child, so the parent's pid is not
        // the workload's pgid. Signalling -parentPid would hit nothing and every liveness probe
        // would report a clean box while the workload ran on. Ask the kernel instead of assuming
        // (this was bug #1 found while authoring the reference implementation this is ported from).
        _pgid = await ResolvePgidAsync(process.Id, cancellationToken).ConfigureAwait(false);

        return new IsolatedProcessHandle(process.Id, process.StandardInput, exited);
    }

    private static async Task<int> ResolvePgidAsync(int parentPid, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var childrenPath = $"/proc/{parentPid}/task/{parentPid}/children";
            if (File.Exists(childrenPath))
            {
                var text = await File.ReadAllTextAsync(childrenPath, cancellationToken).ConfigureAwait(false);
                var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

                if (int.TryParse(first, out var childPid))
                {
                    var pgid = ReadPgid(childPid);
                    if (pgid > 0) return pgid;
                }
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Field 5 of /proc/pid/stat. Parsed from the last ')' because comm is unescaped and can contain spaces and parentheses.</summary>
    private static int ReadPgid(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var commEnd = stat.LastIndexOf(')');
            if (commEnd < 0 || commEnd + 2 >= stat.Length) return 0;

            var fields = stat[(commEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // fields[0]=state, fields[1]=ppid, fields[2]=pgrp
            return fields.Length > 2 && int.TryParse(fields[2], out var pgid) ? pgid : 0;
        }
        catch (Exception)
        {
            return 0;
        }
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

        // Only after the pipes are done: WaitForExit alone can return before the redirected
        // streams are flushed.
        await Task.WhenAll(SwallowAsync(stdout), SwallowAsync(stderr)).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* draining stops with the boundary */ }
    }

    // Direct syscall rather than shelling out to /bin/kill: `kill -0 -1234` is ambiguous,
    // because util-linux parses the negative pid as a signal number and errors out. The probe
    // then reports "nothing alive" for a workload that is very much alive (bug #2 found while
    // authoring the reference implementation).
    [DllImport("libc", SetLastError = true)]
    private static extern int killpg(int pgrp, int sig);

    private const int Sigint = 2;
    private const int Sigkill = 9;
    private const int Sigterm = 15;
    private const int Esrch = 3;

    public Task SignalAsync(TerminationSignal signal, CancellationToken cancellationToken)
    {
        var sig = signal switch
        {
            TerminationSignal.Term => Sigterm,
            TerminationSignal.Int => Sigint,
            TerminationSignal.Kill => Sigkill,
            _ => Sigterm,
        };

        if (_pgid != 0) killpg(_pgid, sig);
        return Task.CompletedTask;
    }

    public Task KillAsync(CancellationToken cancellationToken)
    {
        if (_pgid != 0) killpg(_pgid, Sigkill);
        return Task.CompletedTask;
    }

    public Task<bool> HasLiveProcessesAsync(CancellationToken cancellationToken)
    {
        if (_pgid == 0) return Task.FromResult(false);

        // Caveat: a killed-but-unreaped child is a zombie and still answers signal 0, so this
        // reports "live" until the parent reaps it (bug #3 found while authoring the reference
        // implementation) — termination burns the full verify window even on a clean kill.
        if (killpg(_pgid, 0) == 0) return Task.FromResult(true);

        // ESRCH is the only errno that actually means "gone". EPERM means it exists and we
        // cannot touch it, which must not be reported as clean.
        var errno = Marshal.GetLastWin32Error();
        return Task.FromResult(errno != Esrch);
    }

    public async ValueTask DisposeAsync()
    {
        try { await KillAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* disposal must not throw over an already-dead group */ }

        foreach (var process in _started) process.Dispose();
        _started.Clear();
    }
}
