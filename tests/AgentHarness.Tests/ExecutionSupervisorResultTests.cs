using AgentHarness.Domain;
using AgentHarness.Execution;
using AgentHarness.Persistence;
using Xunit;

namespace AgentHarness.ExecutionTests;

/// <summary>
/// Exercises the standardized <see cref="AttemptResult"/> envelope end-to-end through a real
/// isolation boundary (whichever <see cref="ExecutionSupervisor"/> selects on this box) and a
/// real child process — not a fake. The point of this feature is "does stdout/stderr/exit status
/// actually come back bounded and correct," which a mocked boundary cannot prove.
/// </summary>
public class ExecutionSupervisorResultTests
{
    [Fact]
    public async Task WaitForExitAsync_reports_exit_code_and_captured_output()
    {
        var script = WriteScript("echo out-line\necho err-line 1>&2\nexit 7\n");
        var db = $"Data Source={Path.GetTempPath()}exec_result_{Guid.NewGuid():N}.db";
        try
        {
            await using var store = new SqliteHarnessStore(db);
            await using var supervisor = new ExecutionSupervisor(store);
            var attempt = NewAttempt();

            await supervisor.SpawnAsync(attempt, script, CancellationToken.None);
            var result = await supervisor.WaitForExitAsync(attempt.Id, CancellationToken.None);

            Assert.Equal(AttemptResultReason.Exited, result.Reason);
            Assert.Equal(7, result.ExitCode);
            Assert.Contains("out-line", result.Stdout);
            Assert.Contains("err-line", result.Stderr);
            Assert.False(result.StdoutTruncated);
            Assert.False(result.StderrTruncated);
            Assert.Empty(result.TerminationStages);
            Assert.True(result.ProcessesConfirmedGone);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task WaitForExitAsync_truncates_output_past_the_bound_without_hanging()
    {
        // 5 bytes/line * 20,000 lines = ~100KiB, comfortably past the 64KiB capture cap. A
        // child that fills its pipe with an un-drained reader on the other end would hang here
        // forever instead of completing this test.
        var script = WriteScript("yes AAAA | head -n 20000\nexit 0\n");
        var db = $"Data Source={Path.GetTempPath()}exec_result_{Guid.NewGuid():N}.db";
        try
        {
            await using var store = new SqliteHarnessStore(db);
            await using var supervisor = new ExecutionSupervisor(store);
            var attempt = NewAttempt();

            await supervisor.SpawnAsync(attempt, script, CancellationToken.None);
            var result = await supervisor
                .WaitForExitAsync(attempt.Id, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(0, result.ExitCode);
            Assert.True(result.StdoutTruncated);
            Assert.True(result.Stdout.Length <= 64 * 1024);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task CancelAsync_kills_a_sigterm_ignoring_process_and_reports_terminated()
    {
        var script = WriteScript("trap '' TERM\necho ready\nsleep 30\n");
        var db = $"Data Source={Path.GetTempPath()}exec_result_{Guid.NewGuid():N}.db";
        try
        {
            await using var store = new SqliteHarnessStore(db);
            await using var supervisor = new ExecutionSupervisor(
                store,
                protocolGrace: TimeSpan.FromMilliseconds(50),
                sigtermGrace: TimeSpan.FromMilliseconds(300),
                verifyTimeout: TimeSpan.FromSeconds(2));
            var attempt = NewAttempt();

            await supervisor.SpawnAsync(attempt, script, CancellationToken.None);
            await Task.Delay(500); // let the trap install and the child start sleeping

            var result = await supervisor.CancelAsync(attempt.Id, sendProtocolCancel: null, CancellationToken.None);

            Assert.Equal(AttemptResultReason.Terminated, result.Reason);
            Assert.Contains("Killed", result.TerminationStages);
            Assert.True(result.ProcessesConfirmedGone);
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static Attempt NewAttempt() => new()
    {
        Id = Guid.NewGuid(),
        WorkItemId = Guid.NewGuid(),
        RunId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static string WriteScript(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-harness-test-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/usr/bin/env bash\nset -u\n" + body);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
