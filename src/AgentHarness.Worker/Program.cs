using System.Text.Json;
using AgentHarness.Domain;

namespace AgentHarness.Worker;

/// <summary>
/// Killable external process. Executes one or more leased attempts.
/// Every method takes a CancellationToken (Claude fix #1) — no embedded, non-killable execution.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var attemptArg = args.FirstOrDefault(a => a.StartsWith("--attempt "));
        if (attemptArg is null) { Console.Error.WriteLine("worker: missing --attempt"); return; }
        var attemptId = Guid.Parse(attemptArg["--attempt ".Length..]);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var loop = new WorkerLoop();
        await loop.RunAsync(attemptId, cts.Token);
    }
}

/// <summary>
/// Reads StartAttemptCommand from stdin (versioned harness protocol, NOT ACP's).
/// Durably persists ToolCall state BEFORE executing the side effect, then executes.
/// Reports observations; never self-attests success (ACS rolls up the verdict).
/// </summary>
public sealed class WorkerLoop
{
    public async Task RunAsync(Guid attemptId, CancellationToken ct)
    {
        // 1. Read StartAttemptCommand (JSON-RPC / pipe / socket transport in later phases)
        var line = await Console.In.ReadLineAsync(ct);
        if (line is null) return;
        var cmd = JsonSerializer.Deserialize<StartAttemptCommand>(line)
            ?? throw new InvalidOperationException("worker: empty start command");

        // 2. Heartbeat loop
        using var hb = new Timer(_ => EmitHeartbeat(attemptId), null, 0, 5000);

        // 3. Model invocation + tool execution happen here, each call taking `ct`.
        //    ToolCall state transition is persisted BEFORE the side effect.
        await Task.Delay(50, ct); // placeholder for real loop

        // 4. Report observations; do NOT declare Succeeded.
        Console.WriteLine(JsonSerializer.Serialize(new WorkerEvent(attemptId, 0, WorkerEventType.Completed, JsonDocument.Parse("{}").RootElement, DateTimeOffset.UtcNow)));
    }

    private static void EmitHeartbeat(Guid attemptId) =>
        Console.WriteLine(JsonSerializer.Serialize(new WorkerEvent(attemptId, 0, WorkerEventType.Heartbeat, JsonDocument.Parse("{}").RootElement, DateTimeOffset.UtcNow)));
}

/// <summary>Versioned harness protocol command. ACP is an adapter, not this core shape.</summary>
public sealed record StartAttemptCommand(
    Guid AttemptId, Guid RunId, string AgentId, string ProtocolVersion,
    string Model, IReadOnlyList<string> GrantedCapabilities, DateTimeOffset Deadline);

public sealed record WorkerEvent(Guid AttemptId, long Sequence, string Type, JsonElement Payload, DateTimeOffset OccurredAt);
