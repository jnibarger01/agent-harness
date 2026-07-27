using System.Text.Json;

namespace AgentHarness.PolicyBridge;

/// <summary>
/// Fork A: the harness consumes policy; it does NOT own it. This bridge relays to ACS/OpenClaw.
/// It returns a PolicyDecision artifact (never a boolean) so every ToolCall can carry DecisionId.
///
/// If ACS is unreachable, the bridge returns no grant. Read continuity, if ever required, must
/// use a pre-issued ACS-signed standing grant rather than minting authority during an outage.
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
