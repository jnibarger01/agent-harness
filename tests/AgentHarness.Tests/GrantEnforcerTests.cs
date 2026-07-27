using AgentHarness.PolicyBridge;
using AgentHarness.Tools.Enforcement;
using System.Security.Cryptography;
using Xunit;

namespace AgentHarness.ToolsTests;

public class GrantEnforcerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PolicyDecision Decision(
        Verdict verdict = Verdict.Allow,
        ToolScope? scope = null,
        DateTimeOffset? expiresAt = null
        ) => new()
    {
        DecisionId = Guid.NewGuid(),
        Verdict = verdict,
        NarrowedScope = scope ?? ToolScope.ReadOnly,
        ExpiresAt = expiresAt ?? Now.AddMinutes(5),
        InputsHash = "hash",
        Authority = "acs",
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

    [Fact]
    public void SignedGrant_IsAcceptedWhenAllBindingsMatch()
    {
        using var authority = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var grant = SignedGrant(authority, Guid.NewGuid(), "41", "args-1");

        var result = GrantEnforcer.CheckSigned(
            grant, grant.AttemptId, "41", "args-1", Now, authority);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void SignedGrant_CannotReplayAcrossAttempts()
    {
        using var authority = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var originalAttempt = Guid.NewGuid();
        var grant = SignedGrant(authority, originalAttempt, "41", "args-1");

        var result = GrantEnforcer.CheckSigned(
            grant, Guid.NewGuid(), "41", "args-1", Now, authority);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void SignedGrant_RejectsForgedBindings()
    {
        using var authority = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var grant = SignedGrant(authority, Guid.NewGuid(), "41", "args-1");
        var forged = grant with { FencingToken = "42" };

        var result = GrantEnforcer.CheckSigned(
            forged, forged.AttemptId, "42", "args-1", Now, authority);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void SignedGrant_RejectsChangedExpiryOrArguments()
    {
        using var authority = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var grant = SignedGrant(authority, Guid.NewGuid(), "41", "args-1");

        var changedExpiry = grant with { ExpiresAt = Now.AddHours(1) };
        var expiryResult = GrantEnforcer.CheckSigned(
            changedExpiry, changedExpiry.AttemptId, "41", "args-1", Now, authority);

        var changedArguments = grant with { ArgumentsHash = "args-2" };
        var argumentsResult = GrantEnforcer.CheckSigned(
            changedArguments, changedArguments.AttemptId, "41", "args-2", Now, authority);

        Assert.False(expiryResult.Allowed);
        Assert.False(argumentsResult.Allowed);
    }

    private static SignedGrant SignedGrant(ECDsa authority, Guid attemptId, string fencingToken, string argumentsHash)
    {
        var grant = new SignedGrant
        {
            DecisionId = Guid.NewGuid(),
            AttemptId = attemptId,
            FencingToken = fencingToken,
            ExpiresAt = Now.AddMinutes(5),
            ArgumentsHash = argumentsHash,
            ToolId = "test.read",
            Effect = ToolEffect.Read,
            NarrowedScope = ToolScope.ReadOnly,
            Authority = "acs",
        };
        return grant with { Signature = GrantSignature.Sign(grant, authority) };
    }
}
