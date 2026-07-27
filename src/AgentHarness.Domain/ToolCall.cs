using System.Text.Json;

namespace AgentHarness.Domain;

/// <summary>
/// Auditable proposed or executed side effect. Carries DecisionId so replay can prove
/// the runtime never acted outside the authority's granted scope.
/// </summary>
public sealed record ToolCall
{
    public Guid Id { get; init; }
    public Guid AttemptId { get; init; }
    public string ToolId { get; init; } = "";
    public JsonElement Arguments { get; init; }

    /// <summary>From PolicyBridge. Null/empty = policy violation (no authority granted).</summary>
    public Guid? DecisionId { get; set; }

    public ToolCallState State { get; set; } = ToolCallState.Proposed;
    public DateTimeOffset CreatedAt { get; init; }

    public void MarkExecuted() => State = ToolCallState.Executed;
    public void MarkFailed(string? reason) { State = ToolCallState.Failed; FailureReason = reason; }
    public string? FailureReason { get; set; }

    /// <summary>Worker must durably persist Executed BEFORE the irreversible side effect.</summary>
    public bool IsSideEffectDurable => State == ToolCallState.Executed;
}

public enum ToolCallState { Proposed, Executed, Failed }
