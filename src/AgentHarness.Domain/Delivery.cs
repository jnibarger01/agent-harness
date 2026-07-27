namespace AgentHarness.Domain;

/// <summary>
/// Attempt to send a result back through a channel. Triggered by a state transition
/// (Run -> Succeeded emits an Outbox Delivery), NOT by the worker returning.
/// Durable + idempotent so redelivery is safe.
/// </summary>
public sealed record Delivery
{
    public Guid Id { get; init; }
    public Guid RunId { get; init; }
    public string Channel { get; init; } = "";
    public string OutboundKey { get; init; } = ""; // idempotency key for channel send
    public string Payload { get; init; } = "";
    public DeliveryState State { get; set; } = DeliveryState.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

public enum DeliveryState { Pending, Sent, Failed }
