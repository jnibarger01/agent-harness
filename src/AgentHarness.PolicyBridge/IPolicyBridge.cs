using System.Text.Json;

namespace AgentHarness.PolicyBridge;

/// <summary>
/// Fork A: the harness consumes policy; it does NOT own it. This bridge relays to ACS/OpenClaw.
/// It returns a PolicyDecision artifact (never a boolean) so every ToolCall can carry DecisionId.
///
/// Degraded mode (ACS unreachable): Deny everything EXCEPT ACS's own "read" class tools.
/// Every degraded decision is journaled (Degraded=true) for post-hoc audit.
/// </summary>
public interface IPolicyBridge
{
    Task<PolicyDecision> DecideAsync(
        string agentId,
        string toolId,
        ToolEffect requestedEffect,
        JsonElement args,
        CancellationToken cancellationToken);
}
