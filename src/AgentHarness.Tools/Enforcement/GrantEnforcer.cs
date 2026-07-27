using AgentHarness.PolicyBridge;

namespace AgentHarness.Tools.Enforcement;

/// <summary>
/// Local enforcement of a remotely issued <see cref="PolicyDecision"/>. Deny is the default and
/// every early return is a deny — there is no path through this method that reaches Allow by
/// omission.
///
/// This closes a real gap in the scaffold: principles #5-#7 in README.md say the harness
/// "enforces NarrowedScope; it never re-derives it" and that degraded mode "denies everything
/// except read-class tools", but until now nothing in the codebase actually checked a
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

        // Belt and braces: a degraded-mode decision never authorizes a side effect, regardless
        // of what NarrowedScope claims. The bridge should not have issued one, and the
        // enforcer refuses to honour it if it did (README principle #6: "Degraded mode fails closed").
        if (decision.Degraded && requestedEffect != ToolEffect.Read)
            return GrantCheck.Deny("degraded-mode decision cannot authorize a non-read effect");

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
}

public sealed record GrantCheck(bool Allowed, string? Reason)
{
    public static GrantCheck Allow() => new(true, null);
    public static GrantCheck Deny(string reason) => new(false, reason);
}
