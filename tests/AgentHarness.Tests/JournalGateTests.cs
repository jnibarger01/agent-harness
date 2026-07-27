using System.Text.Json;
using AgentHarness.Observability;
using Xunit;

namespace AgentHarness.ToolsTests;

public class JournalGateTests
{
    [Fact]
    public async Task JournalFailure_DeniesExecution()
    {
        var gate = new ToolExecutionJournalGate(new FailingJournal());

        var result = await gate.TryAdmitAsync(
            Guid.NewGuid(), Guid.NewGuid(), "test.write", Guid.NewGuid(),
            JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Contains("journal", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JournalSuccess_AllowsExecution()
    {
        var gate = new ToolExecutionJournalGate(new RecordingJournal());

        var result = await gate.TryAdmitAsync(
            Guid.NewGuid(), Guid.NewGuid(), "test.read", Guid.NewGuid(),
            JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.True(result.Allowed);
    }

    private sealed class FailingJournal : ITurnJournal
    {
        public Task AppendInboundAsync(Guid turnId, string channel, string inboundKey, CancellationToken ct) => throw new IOException();
        public Task AppendToolCallAsync(Guid toolCallId, Guid attemptId, string toolId, Guid? decisionId, JsonElement args, CancellationToken ct) => throw new IOException();
        public Task AppendToolResultAsync(Guid toolCallId, JsonElement result, CancellationToken ct) => throw new IOException();
        public Task AppendProviderAsync(Guid runId, string requestHash, string? responseHash, CancellationToken ct) => throw new IOException();
        public async IAsyncEnumerable<PolicyViolation> ReplayViolationsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
    }

    private sealed class RecordingJournal : ITurnJournal
    {
        public Task AppendInboundAsync(Guid turnId, string channel, string inboundKey, CancellationToken ct) => Task.CompletedTask;
        public Task AppendToolCallAsync(Guid toolCallId, Guid attemptId, string toolId, Guid? decisionId, JsonElement args, CancellationToken ct) => Task.CompletedTask;
        public Task AppendToolResultAsync(Guid toolCallId, JsonElement result, CancellationToken ct) => Task.CompletedTask;
        public Task AppendProviderAsync(Guid runId, string requestHash, string? responseHash, CancellationToken ct) => Task.CompletedTask;
        public async IAsyncEnumerable<PolicyViolation> ReplayViolationsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
    }
}
