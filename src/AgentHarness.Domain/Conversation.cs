namespace AgentHarness.Domain;

/// <summary>Long-lived user/channel context. Durable, partition key for leases.</summary>
public sealed record Conversation
{
    public Guid Id { get; init; }
    public string Channel { get; init; } = "";
    public string ExternalId { get; init; } = ""; // e.g. Telegram chat_id
    public DateTimeOffset CreatedAt { get; init; }
}
