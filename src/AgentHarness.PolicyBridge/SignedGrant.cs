using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentHarness.PolicyBridge;

/// <summary>
/// A policy grant signed by the external authority. The harness verifies this artifact; it does
/// not mint grants when the authority is unavailable.
/// </summary>
public sealed record SignedGrant
{
    public Guid DecisionId { get; init; }
    public Guid AttemptId { get; init; }
    public string FencingToken { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    public string ArgumentsHash { get; init; } = "";
    public string ToolId { get; init; } = "";
    public ToolEffect Effect { get; init; }
    public ToolScope NarrowedScope { get; init; } = ToolScope.None;
    public string Authority { get; init; } = "";
    public byte[] Signature { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Canonical signing and verification for grants. The signed payload deliberately includes the
/// attempt, fencing token, expiry, and argument hash so a valid grant cannot be moved to another
/// execution or replayed with different input.
/// </summary>
public static class GrantSignature
{
    public static byte[] CreatePayload(SignedGrant grant)
    {
        var scope = grant.NarrowedScope;
        var paths = string.Join("\u001f", scope.AllowedWritePaths.Select(Encode));
        var canonical = string.Join("\n", new[]
        {
            "agent-harness-grant-v1",
            grant.DecisionId.ToString("D"),
            grant.AttemptId.ToString("D"),
            Encode(grant.FencingToken),
            grant.ExpiresAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            Encode(grant.ArgumentsHash),
            Encode(grant.ToolId),
            grant.Effect.ToString(),
            scope.CanRead ? "1" : "0",
            scope.CanWrite ? "1" : "0",
            scope.CanExec ? "1" : "0",
            paths,
            Encode(grant.Authority),
        });
        return Encoding.UTF8.GetBytes(canonical);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public static byte[] Sign(SignedGrant grant, ECDsa authorityKey) =>
        authorityKey.SignData(CreatePayload(grant), HashAlgorithmName.SHA256);

    public static bool Verify(SignedGrant grant, ECDsa authorityKey)
    {
        try
        {
            return authorityKey.VerifyData(CreatePayload(grant), grant.Signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
