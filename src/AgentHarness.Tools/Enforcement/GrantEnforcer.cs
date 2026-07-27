using AgentHarness.PolicyBridge;
using System.Security.Cryptography;

namespace AgentHarness.Tools.Enforcement;

/// <summary>
/// Local enforcement of a remotely issued <see cref="PolicyDecision"/>. Deny is the default and
/// every early return is a deny — there is no path through this method that reaches Allow by
/// omission.
///
/// This closes a real gap in the scaffold: principles #5-#7 in README.md say the harness
/// "enforces NarrowedScope; it never re-derives it", but until now nothing in the codebase actually checked a
/// ToolCall against a PolicyDecision before letting it through — only
/// <c>ITurnJournal.ReplayViolationsAsync</c> could prove a violation, after the fact. This is
/// the enforcement point a worker loop should call before invoking a tool. It answers "does
/// this call fit inside the authority PolicyBridge already granted",
/// never "should this be allowed" — that question stays with PolicyBridge/ACS.
/// </summary>
public static class GrantEnforcer
{
    public static GrantCheck Check(
        PolicyDecision? decision,
        ToolEffect requestedEffect,
        DateTimeOffset now,
        string? writeTargetPath = null)
    {
        if (decision is null)
            return GrantCheck.Deny("no policy decision presented (deny by default)");

        if (decision.Verdict != Verdict.Allow)
            return GrantCheck.Deny($"verdict is {decision.Verdict}, not Allow");

        if (decision.ExpiresAt <= now)
            return GrantCheck.Deny("policy decision expired");

        if (!decision.NarrowedScope.Permits(requestedEffect))
            return GrantCheck.Deny($"effect {requestedEffect} exceeds narrowed scope");

        if (requestedEffect == ToolEffect.Write && decision.NarrowedScope.AllowedWritePaths.Count > 0)
        {
            if (writeTargetPath is null ||
                !decision.NarrowedScope.AllowedWritePaths.Any(root => PathContainment.IsContained(root, writeTargetPath)))
            {
                return GrantCheck.Deny("write target path is outside the granted scope");
            }
        }

        return GrantCheck.Allow();
    }

    public static GrantCheck CheckSigned(
        SignedGrant? grant,
        Guid attemptId,
        string fencingToken,
        string argumentsHash,
        DateTimeOffset now,
        ECDsa authorityKey,
        string? writeTargetPath = null)
    {
        if (grant is null)
            return GrantCheck.Deny("no signed grant presented (deny by default)");

        if (grant.AttemptId != attemptId)
            return GrantCheck.Deny("grant is bound to a different attempt");

        if (!string.Equals(grant.FencingToken, fencingToken, StringComparison.Ordinal))
            return GrantCheck.Deny("grant fencing token does not match the current lease");

        if (!string.Equals(grant.ArgumentsHash, argumentsHash, StringComparison.Ordinal))
            return GrantCheck.Deny("grant arguments hash does not match the requested call");

        if (!GrantSignature.Verify(grant, authorityKey))
            return GrantCheck.Deny("grant signature is invalid");

        return Check(new PolicyDecision
        {
            DecisionId = grant.DecisionId,
            Verdict = Verdict.Allow,
            NarrowedScope = grant.NarrowedScope,
            ExpiresAt = grant.ExpiresAt,
            InputsHash = grant.ArgumentsHash,
            Authority = grant.Authority,
        }, grant.Effect, now, writeTargetPath);
    }
}

public sealed record GrantCheck(bool Allowed, string? Reason)
{
    public static GrantCheck Allow() => new(true, null);
    public static GrantCheck Deny(string reason) => new(false, reason);
}
