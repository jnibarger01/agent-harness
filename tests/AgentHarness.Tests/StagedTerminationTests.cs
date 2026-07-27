using AgentHarness.Isolation;
using Xunit;

namespace AgentHarness.IsolationTests;

public class StagedTerminationTests
{
    [Fact]
    public async Task UnknownCgroupPath_IsLiveNotClean()
    {
        await using var boundary = new CgroupV2IsolationBoundary("test", "/does/not/exist");

        Assert.True(await boundary.HasLiveProcessesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IgnoresSigterm_EscalatesToKill()
    {
        var boundary = new FakeBoundary(respondsToSigterm: false);
        var never = new TaskCompletionSource<int>();

        var ladder = new StagedTermination(
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200));

        var outcome = await ladder.TerminateAsync(boundary, never.Task, sendProtocolCancel: null, CancellationToken.None);

        Assert.Contains("Killed", outcome.StagesEntered);
        Assert.True(outcome.ProcessesConfirmedGone);
    }

    [Fact]
    public async Task RespondsToSigterm_StopsWithoutKill()
    {
        var exited = new TaskCompletionSource<int>();
        var boundary = new FakeBoundary(respondsToSigterm: true, onTerm: () => exited.TrySetResult(0));

        var ladder = new StagedTermination(
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(200));

        var outcome = await ladder.TerminateAsync(boundary, exited.Task, sendProtocolCancel: null, CancellationToken.None);

        Assert.DoesNotContain("Killed", outcome.StagesEntered);
        Assert.True(outcome.ProcessesConfirmedGone);
    }

    [Fact]
    public async Task UnverifiableLiveness_ReportsNotConfirmedGone()
    {
        var boundary = new FakeBoundary(respondsToSigterm: false, throwOnProbe: true);
        var never = new TaskCompletionSource<int>();

        var ladder = new StagedTermination(
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(100));

        var outcome = await ladder.TerminateAsync(boundary, never.Task, sendProtocolCancel: null, CancellationToken.None);

        // Unverifiable must never be reported as clean.
        Assert.False(outcome.ProcessesConfirmedGone);
        Assert.Contains("KillVerificationFailed", outcome.StagesEntered);
    }

    private sealed class FakeBoundary : IIsolationBoundary
    {
        private readonly bool _respondsToSigterm;
        private readonly bool _throwOnProbe;
        private readonly Action? _onTerm;
        private bool _alive = true;

        public FakeBoundary(bool respondsToSigterm, bool throwOnProbe = false, Action? onTerm = null)
        {
            _respondsToSigterm = respondsToSigterm;
            _throwOnProbe = throwOnProbe;
            _onTerm = onTerm;
        }

        public string Id => "fake";
        public IsolationKind Kind => IsolationKind.ProcessGroup;

        public Task<IsolatedProcessHandle> StartAsync(IsolatedProcessSpec spec, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SignalAsync(TerminationSignal signal, CancellationToken ct)
        {
            if (_respondsToSigterm && signal == TerminationSignal.Term)
            {
                _alive = false;
                _onTerm?.Invoke();
            }

            return Task.CompletedTask;
        }

        public Task KillAsync(CancellationToken ct)
        {
            _alive = false;
            return Task.CompletedTask;
        }

        public Task<bool> HasLiveProcessesAsync(CancellationToken ct) =>
            _throwOnProbe ? throw new IOException("cgroup unreadable") : Task.FromResult(_alive);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
