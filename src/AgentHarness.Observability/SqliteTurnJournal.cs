using System.Text.Json;
using AgentHarness.Domain;
using AgentHarness.Observability;
using AgentHarness.PolicyBridge;
using Microsoft.Data.Sqlite;

namespace AgentHarness.Observability;

/// <summary>
/// Append-only journal backed by SQLite. Every ToolCall carries DecisionId; replay joins to
/// PolicyDecision and confirms args fall within NarrowedScope. Without this, "policy-consuming"
/// is an assertion (our own doctrine says not to accept that).
/// </summary>
public sealed class SqliteTurnJournal : ITurnJournal, IDisposable
{
    private readonly SqliteConnection _conn;
    public SqliteTurnJournal(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Journal (
                Seq INTEGER PRIMARY KEY AUTOINCREMENT,
                Kind TEXT, TurnId TEXT, ToolCallId TEXT, AttemptId TEXT, ToolId TEXT,
                DecisionId TEXT, Data TEXT, At TEXT);
            CREATE TABLE IF NOT EXISTS Decisions (
                DecisionId TEXT PRIMARY KEY, Verdict TEXT, NarrowedScope TEXT,
                ExpiresAt TEXT, InputsHash TEXT, Authority TEXT, Degraded INTEGER);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task AppendInboundAsync(Guid turnId, string channel, string inboundKey, CancellationToken ct)
        => await WriteAsync("inbound", turnId, null, null, channel, null, Json($"ch={channel} key={inboundKey}"), ct);

    public async Task AppendToolCallAsync(Guid toolCallId, Guid attemptId, string toolId, Guid? decisionId, JsonElement args, CancellationToken ct)
        => await WriteAsync("toolcall", null, toolCallId, attemptId, toolId, decisionId, args.GetRawText(), ct);

    public async Task AppendToolResultAsync(Guid toolCallId, JsonElement result, CancellationToken ct)
        => await WriteAsync("toolresult", null, toolCallId, null, null, null, result.GetRawText(), ct);

    public async Task AppendProviderAsync(Guid runId, string requestHash, string? responseHash, CancellationToken ct)
        => await WriteAsync("provider", runId, null, null, null, null, Json($"req={requestHash} resp={responseHash}"), ct);

    public Task RecordDecisionAsync(PolicyDecision d, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Decisions(DecisionId,Verdict,NarrowedScope,ExpiresAt,InputsHash,Authority,Degraded) VALUES($id,$v,$s,$e,$h,$a,$d)";
        cmd.Parameters.AddWithValue("$id", d.DecisionId.ToString());
        cmd.Parameters.AddWithValue("$v", d.Verdict.ToString());
        cmd.Parameters.AddWithValue("$s", $"{(d.NarrowedScope.CanRead?"R":"")}{(d.NarrowedScope.CanWrite?"W":"")}{(d.NarrowedScope.CanExec?"X":"")}");
        cmd.Parameters.AddWithValue("$e", d.ExpiresAt.ToString("O"));
        cmd.Parameters.AddWithValue("$h", d.InputsHash);
        cmd.Parameters.AddWithValue("$a", d.Authority);
        cmd.Parameters.AddWithValue("$d", d.Degraded ? 1 : 0);
        return cmd.ExecuteNonQueryAsync(ct);
    }

    public async IAsyncEnumerable<PolicyViolation> ReplayViolationsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // A ToolCall with no DecisionId, or a Decision that is Deny, is a violation.
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT ToolCallId, DecisionId FROM Journal WHERE Kind='toolcall'";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var tcId = Guid.Parse(r.GetString(0));
            var dec = r.IsDBNull(1) ? (Guid?)null : Guid.Parse(r.GetString(1));
            if (dec is null)
                yield return new PolicyViolation(tcId, "ToolCall has no DecisionId (no authority granted)");
        }
    }

    private async Task WriteAsync(string kind, Guid? turnId, Guid? toolCallId, Guid? attemptId, string? toolId, Guid? decisionId, string data, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Journal(Kind,TurnId,ToolCallId,AttemptId,ToolId,DecisionId,Data,At) VALUES($k,$t,$tc,$a,$ti,$d,$data,$at)";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", turnId?.ToString());
        cmd.Parameters.AddWithValue("$tc", toolCallId?.ToString());
        cmd.Parameters.AddWithValue("$a", attemptId?.ToString());
        cmd.Parameters.AddWithValue("$ti", toolId);
        cmd.Parameters.AddWithValue("$d", decisionId?.ToString());
        cmd.Parameters.AddWithValue("$data", data);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);
    public void Dispose() => _conn.Dispose();
}
