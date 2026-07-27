using AgentHarness.PolicyBridge;
using AgentHarness.Tools.Enforcement;
using Xunit;

namespace AgentHarness.ToolsTests;

public class GrantEnforcerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PolicyDecision Decision(
        Verdict verdict = Verdict.Allow,
        ToolScope? scope = null,
        DateTimeOffset? expiresAt = null,
        bool degraded = false) => new()
    {
        DecisionId = Guid.NewGuid(),
        Verdict = verdict,
        NarrowedScope = scope ?? ToolScope.ReadOnly,
        ExpiresAt = expiresAt ?? Now.AddMinutes(5),
        InputsHash = "hash",
        Authority = "acs",
        Degraded = degraded,
    };

    [Fact]
    public void NoDecision_IsDenied()
    {
        var result = GrantEnforcer.Check(null, ToolEffect.Read, Now);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void DenyVerdict_IsDenied()
    {
        var result = GrantEnforcer.Check(Decision(verdict: Verdict.Deny), ToolEffect.Read, Now);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void RequireApprovalVerdict_IsDenied()
    {
        var result = GrantEnforcer.Check(Decision(verdict: Verdict.RequireApproval), ToolEffect.Read, Now);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void ExpiredDecision_IsDenied()
    {
        var result = GrantEnforcer.Check(Decision(expiresAt: Now.AddSeconds(-1)), ToolEffect.Read, Now);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void EffectExceedingNarrowedScope_IsDenied()
    {
        var result = GrantEnforcer.Check(Decision(scope: ToolScope.ReadOnly), ToolEffect.Write, Now);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void DegradedDecision_CannotAuthorizeWrite()
    {
        var scope = new ToolScope(read: true, write: true, exec: false);
        var result = GrantEnforcer.Check(Decision(scope: scope, degraded: true), ToolEffect.Write, Now);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void DegradedDecision_StillAllowsRead()
    {
        var result = GrantEnforcer.Check(Decision(scope: ToolScope.ReadOnly, degraded: true), ToolEffect.Read, Now);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void MatchingDecision_IsAllowed()
    {
        var result = GrantEnforcer.Check(Decision(), ToolEffect.Read, Now);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void WriteOutsideAllowedPaths_IsDenied()
    {
        var scope = new ToolScope(read: false, write: true, exec: false, allowedWritePaths: new[] { "/srv/app" });
        var result = GrantEnforcer.Check(Decision(scope: scope), ToolEffect.Write, Now, writeTargetPath: "/srv/appdata/x.txt");
        Assert.False(result.Allowed);
    }

    [Fact]
    public void WriteInsideAllowedPaths_IsAllowed()
    {
        var scope = new ToolScope(read: false, write: true, exec: false, allowedWritePaths: new[] { "/srv/app" });
        var result = GrantEnforcer.Check(Decision(scope: scope), ToolEffect.Write, Now, writeTargetPath: "/srv/app/data/x.txt");
        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData("/srv/app", "/srv/app/data/x.txt", true)]
    [InlineData("/srv/app", "/srv/appdata/x.txt", false)]
    [InlineData("/srv/app", "/srv/app/../../etc/passwd", false)]
    [InlineData("/srv/app", "/srv/app", true)]
    public void PathContainment_RejectsPrefixAndTraversalTricks(string root, string candidate, bool expected)
    {
        Assert.Equal(expected, PathContainment.IsContained(root, candidate));
    }
}
