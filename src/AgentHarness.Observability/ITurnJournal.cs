using System.Text.Json;

namespace AgentHarness.Observability;

/// <summary>
/// Append-only, content-addressed journal. Every ToolCall carries DecisionId so replay
/// proves the runtime never acted outside granted authority. Hash args+result.
/// Establishes ActivitySource/Meter for OpenTelemetry participation.
/// </summary>
public interface ITurnJournal
{
    Task AppendInboundAsync(Guid turnId, string channel, string inboundKey, CancellationToken ct);
    Task AppendToolCallAsync(Guid toolCallId, Guid attemptId, string toolId, Guid? decisionId, JsonElement args, CancellationToken ct);
    Task AppendToolResultAsync(Guid toolCallId, JsonElement result, CancellationToken ct);
    Task AppendProviderAsync(Guid runId, string requestHash, string? responseHash, CancellationToken ct);

    /// <summary>Replay: join ToolCall -> PolicyDecision, confirm args in NarrowedScope. Returns violations.</summary>
    IAsyncEnumerable<PolicyViolation> ReplayViolationsAsync(CancellationToken ct);
}

public sealed record PolicyViolation(Guid ToolCallId, string Reason);
