namespace AgentHarness.PolicyBridge;

/// <summary>
/// The authority's verdict, returned by PolicyBridge. This is an ARTIFACT, not a boolean.
/// The harness ENFORCES NarrowedScope; it must never re-derive it (re-deriving = fail-open).
/// </summary>
public sealed record PolicyDecision
{
    public Guid DecisionId { get; init; }
    public Verdict Verdict { get; init; }
    public ToolScope NarrowedScope { get; init; } = ToolScope.None;
    public DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Hash of (agentId, toolId, args-shape, capabilities) at decision time. Replay checks this.</summary>
    public string InputsHash { get; init; } = "";
    /// <summary>"acs" or "openclaw". Outage-time local grants are not a valid authority.</summary>
    public string Authority { get; init; } = "";
}

public enum Verdict { Allow, Deny, RequireApproval }

/// <summary>What the harness MAY do. Enforced, never re-derived by the harness.</summary>
public sealed record ToolScope
{
    public static readonly ToolScope None = new(false, false, false);
    public static readonly ToolScope ReadOnly = new(true, false, false);

    public ToolScope(bool read, bool write, bool exec, IReadOnlyList<string>? allowedWritePaths = null)
    {
        CanRead = read; CanWrite = write; CanExec = exec;
        AllowedWritePaths = allowedWritePaths ?? Array.Empty<string>();
    }

    public bool CanRead { get; }
    public bool CanWrite { get; }
    public bool CanExec { get; }

    /// <summary>
    /// Roots a Write effect's target path must resolve inside (see
    /// AgentHarness.Tools.Enforcement.PathContainment). Empty means the effect flag alone
    /// governs — no path-level containment is checked.
    /// </summary>
    public IReadOnlyList<string> AllowedWritePaths { get; }

    public bool Permits(ToolEffect effect) => effect switch
    {
        ToolEffect.Read => CanRead,
        ToolEffect.Write => CanWrite,
        ToolEffect.Exec => CanExec,
        _ => false
    };
}

public enum ToolEffect { Read, Write, Exec }
