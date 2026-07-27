namespace AgentHarness.Domain;

/// <summary>File, output, generated data, or attachment produced by a run.</summary>
public sealed record Artifact
{
    public Guid Id { get; init; }
    public Guid RunId { get; init; }
    public string Kind { get; init; } = "";
    public string Reference { get; init; } = ""; // content-addressed path/hash
    public DateTimeOffset CreatedAt { get; init; }
}
