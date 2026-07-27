using System.Text.Json;
using AgentHarness.Domain;
using AgentHarness.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentHarness.Persistence;

/// <summary>
/// Phase 1 durable store: SQLite. Single connection, transactional transitions.
/// This is the SYSTEM OF RECORD — not an in-memory queue.
/// Swap to PostgreSQL in Phase 5 by reimplementing these interfaces; claim logic unchanged.
/// </summary>
public sealed class SqliteHarnessStore : IInbox, IOutbox, IHarnessStore, IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteHarnessStore(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        Migrate();
    }

    private void Migrate()
    {
        using var tx = _conn.BeginTransaction();
        var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Turns (
                Id TEXT PRIMARY KEY, Channel TEXT, InboundKey TEXT UNIQUE, Payload TEXT,
                State TEXT, CreatedAt TEXT, RejectReason TEXT);
            CREATE TABLE IF NOT EXISTS Runs (
                Id TEXT PRIMARY KEY, TurnId TEXT, State TEXT, CreatedAt TEXT);
            CREATE TABLE IF NOT EXISTS Attempts (
                Id TEXT PRIMARY KEY, WorkItemId TEXT, RunId TEXT, AgentId TEXT, State TEXT,
                CreatedAt TEXT, LeaseExpiresAt TEXT, WorkerId TEXT, OwnerToken TEXT);
            CREATE TABLE IF NOT EXISTS Leases (
                Id TEXT PRIMARY KEY, PartitionKey TEXT, OwnerToken TEXT, ExpiresAt TEXT,
                AcquiredAt TEXT, Kind TEXT);
            CREATE TABLE IF NOT EXISTS Conversations (
                Id TEXT PRIMARY KEY, Channel TEXT, ExternalId TEXT, CreatedAt TEXT);
            CREATE TABLE IF NOT EXISTS Deliveries (
                Id TEXT PRIMARY KEY, RunId TEXT, Channel TEXT, OutboundKey TEXT, Payload TEXT,
                State TEXT, Attempts INT, CreatedAt TEXT);
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    // ---- IInbox (idempotent accept) ----
    public async Task<Turn> AcceptAsync(string channel, string inboundKey, string payload, CancellationToken ct)
    {
        // UNIQUE on InboundKey makes duplicate delivery a constraint violation -> return existing.
        var existing = await GetByInboundKeyAsync(channel, inboundKey, ct);
        if (existing is not null) return existing;

        var turn = new Turn
        {
            Id = Guid.NewGuid(),
            Channel = channel,
            InboundKey = inboundKey,
            Payload = payload,
            CreatedAt = DateTimeOffset.UtcNow
        };
        turn.Accept();
        await UpsertTurnAsync(turn, ct);
        return turn;
    }

    public async Task<Turn?> GetAsync(Guid turnId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Id,Channel,InboundKey,Payload,State,CreatedAt,RejectReason FROM Turns WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", turnId.ToString());
        using var r = await cmd.ExecuteReaderAsync(ct);
        return r.Read() ? ReadTurn(r) : null;
    }

    public async IAsyncEnumerable<Turn> PendingAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Id,Channel,InboundKey,Payload,State,CreatedAt,RejectReason FROM Turns WHERE State='Accepted'";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) yield return ReadTurn(r);
    }

    // ---- IHarnessStore ----
    public async Task SaveRunAsync(Run run, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Runs(Id,TurnId,State,CreatedAt) VALUES($id,$t,$s,$c)";
        cmd.Parameters.AddWithValue("$id", run.Id.ToString());
        cmd.Parameters.AddWithValue("$t", run.TurnId.ToString());
        cmd.Parameters.AddWithValue("$s", run.State.ToString());
        cmd.Parameters.AddWithValue("$c", run.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Run?> GetRunAsync(Guid runId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Id,TurnId,State,CreatedAt FROM Runs WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", runId.ToString());
        using var r = await cmd.ExecuteReaderAsync(ct);
        return r.Read() ? new Run { Id = Guid.Parse(r.GetString(0)), TurnId = Guid.Parse(r.GetString(1)),
            State = Enum.Parse<RunState>(r.GetString(2)), CreatedAt = DateTimeOffset.Parse(r.GetString(3)) } : null;
    }

    public async Task SaveAttemptAsync(Attempt attempt, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Attempts(Id,WorkItemId,RunId,AgentId,State,CreatedAt,LeaseExpiresAt,WorkerId,OwnerToken) VALUES($id,$wi,$r,$a,$s,$c,$le,$w,$o)";
        cmd.Parameters.AddWithValue("$id", attempt.Id.ToString());
        cmd.Parameters.AddWithValue("$wi", attempt.WorkItemId.ToString());
        cmd.Parameters.AddWithValue("$r", attempt.RunId.ToString());
        cmd.Parameters.AddWithValue("$a", attempt.AgentId);
        cmd.Parameters.AddWithValue("$s", attempt.State.ToString());
        cmd.Parameters.AddWithValue("$c", attempt.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$le", attempt.LeaseExpiresAt?.ToString("O"));
        cmd.Parameters.AddWithValue("$w", attempt.WorkerId);
        cmd.Parameters.AddWithValue("$o", attempt.OwnerToken);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Attempt?> GetAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Id,WorkItemId,RunId,AgentId,State,CreatedAt,LeaseExpiresAt,WorkerId,OwnerToken FROM Attempts WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", attemptId.ToString());
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!r.Read()) return null;
        return new Attempt
        {
            Id = Guid.Parse(r.GetString(0)), WorkItemId = Guid.Parse(r.GetString(1)),
            RunId = Guid.Parse(r.GetString(2)), AgentId = r.GetString(3),
            State = Enum.Parse<AttemptState>(r.GetString(4)), CreatedAt = DateTimeOffset.Parse(r.GetString(5)),
            LeaseExpiresAt = r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6)),
            WorkerId = r.IsDBNull(7) ? null : r.GetString(7),
            OwnerToken = r.IsDBNull(8) ? null : r.GetString(8)
        };
    }

    public async Task<IReadOnlyList<Attempt>> AttemptsForRunAsync(Guid runId, CancellationToken ct)
    {
        var list = new List<Attempt>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Id,WorkItemId,RunId,AgentId,State,CreatedAt,LeaseExpiresAt,WorkerId,OwnerToken FROM Attempts WHERE RunId=$r";
        cmd.Parameters.AddWithValue("$r", runId.ToString());
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadAttempt(r));
        return list;
    }

    public async Task SaveLeaseAsync(Lease lease, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Leases(Id,PartitionKey,OwnerToken,ExpiresAt,AcquiredAt,Kind) VALUES($id,$p,$o,$e,$a,$k)";
        cmd.Parameters.AddWithValue("$id", lease.Id.ToString());
        cmd.Parameters.AddWithValue("$p", lease.PartitionKey);
        cmd.Parameters.AddWithValue("$o", lease.OwnerToken);
        cmd.Parameters.AddWithValue("$e", lease.ExpiresAt.ToString("O"));
        cmd.Parameters.AddWithValue("$a", lease.AcquiredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$k", lease.Kind.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Lease>> ExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var list = new List<Lease>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Id,PartitionKey,OwnerToken,ExpiresAt,AcquiredAt,Kind FROM Leases WHERE ExpiresAt <= $now";
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadLease(r));
        return list;
    }

    public async Task<Conversation> GetOrCreateConversationAsync(string channel, string externalId, CancellationToken ct)
    {
        using var find = _conn.CreateCommand();
        find.CommandText = "SELECT Id,Channel,ExternalId,CreatedAt FROM Conversations WHERE Channel=$c AND ExternalId=$e";
        find.Parameters.AddWithValue("$c", channel);
        find.Parameters.AddWithValue("$e", externalId);
        using var r = await find.ExecuteReaderAsync(ct);
        if (r.Read()) return ReadConversation(r);

        var conv = new Conversation { Id = Guid.NewGuid(), Channel = channel, ExternalId = externalId, CreatedAt = DateTimeOffset.UtcNow };
        using var ins = _conn.CreateCommand();
        ins.CommandText = "INSERT INTO Conversations(Id,Channel,ExternalId,CreatedAt) VALUES($id,$c,$e,$a)";
        ins.Parameters.AddWithValue("$id", conv.Id.ToString());
        ins.Parameters.AddWithValue("$c", conv.Channel);
        ins.Parameters.AddWithValue("$e", conv.ExternalId);
        ins.Parameters.AddWithValue("$a", conv.CreatedAt.ToString("O"));
        await ins.ExecuteNonQueryAsync(ct);
        return conv;
    }

    // ---- IOutbox ----
    public async Task<Delivery> EnqueueAsync(Guid runId, string channel, string outboundKey, string payload, CancellationToken ct)
    {
        var d = new Delivery { Id = Guid.NewGuid(), RunId = runId, Channel = channel, OutboundKey = outboundKey, Payload = payload, CreatedAt = DateTimeOffset.UtcNow };
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Deliveries(Id,RunId,Channel,OutboundKey,Payload,State,Attempts,CreatedAt) VALUES($id,$r,$c,$o,$p,'Pending',0,$a)";
        cmd.Parameters.AddWithValue("$id", d.Id.ToString());
        cmd.Parameters.AddWithValue("$r", d.RunId.ToString());
        cmd.Parameters.AddWithValue("$c", d.Channel);
        cmd.Parameters.AddWithValue("$o", d.OutboundKey);
        cmd.Parameters.AddWithValue("$p", d.Payload);
        cmd.Parameters.AddWithValue("$a", d.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return d;
    }

    public async IAsyncEnumerable<Delivery> PendingAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Id,RunId,Channel,OutboundKey,Payload,State,Attempts,CreatedAt FROM Deliveries WHERE State='Pending'";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) yield return ReadDelivery(r);
    }

    public async Task MarkSentAsync(Guid deliveryId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE Deliveries SET State='Sent',Attempts=Attempts+1 WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", deliveryId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- readers ----
    private static Turn ReadTurn(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)), Channel = r.GetString(1), InboundKey = r.GetString(2),
        Payload = r.GetString(3), State = Enum.Parse<TurnState>(r.GetString(4)),
        CreatedAt = DateTimeOffset.Parse(r.GetString(5)),
        RejectReason = r.IsDBNull(6) ? null : r.GetString(6)
    };

    private static Attempt ReadAttempt(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)), WorkItemId = Guid.Parse(r.GetString(1)),
        RunId = Guid.Parse(r.GetString(2)), AgentId = r.GetString(3),
        State = Enum.Parse<AttemptState>(r.GetString(4)), CreatedAt = DateTimeOffset.Parse(r.GetString(5)),
        LeaseExpiresAt = r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6)),
        WorkerId = r.IsDBNull(7) ? null : r.GetString(7),
        OwnerToken = r.IsDBNull(8) ? null : r.GetString(8)
    };

    private static Lease ReadLease(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)), PartitionKey = r.GetString(1), OwnerToken = r.GetString(2),
        ExpiresAt = DateTimeOffset.Parse(r.GetString(3)), CreatedAt = DateTimeOffset.Parse(r.GetString(4)),
        Kind = Enum.Parse<LeaseKind>(r.GetString(5))
    };

    private static Conversation ReadConversation(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)), Channel = r.GetString(1), ExternalId = r.GetString(2),
        CreatedAt = DateTimeOffset.Parse(r.GetString(3))
    };

    private static Delivery ReadDelivery(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)), RunId = Guid.Parse(r.GetString(1)), Channel = r.GetString(2),
        OutboundKey = r.GetString(3), Payload = r.GetString(4), State = Enum.Parse<DeliveryState>(r.GetString(5)),
        Attempts = r.GetInt32(6), CreatedAt = DateTimeOffset.Parse(r.GetString(7))
    };

    private async Task<Turn?> GetByInboundKeyAsync(string channel, string inboundKey, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Id,Channel,InboundKey,Payload,State,CreatedAt,RejectReason FROM Turns WHERE InboundKey=$k";
        cmd.Parameters.AddWithValue("$k", inboundKey);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return r.Read() ? ReadTurn(r) : null;
    }

    private async Task UpsertTurnAsync(Turn turn, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Turns(Id,Channel,InboundKey,Payload,State,CreatedAt,RejectReason) VALUES($id,$c,$k,$p,$s,$a,$rr)";
        cmd.Parameters.AddWithValue("$id", turn.Id.ToString());
        cmd.Parameters.AddWithValue("$c", turn.Channel);
        cmd.Parameters.AddWithValue("$k", turn.InboundKey);
        cmd.Parameters.AddWithValue("$p", turn.Payload);
        cmd.Parameters.AddWithValue("$s", turn.State.ToString());
        cmd.Parameters.AddWithValue("$a", turn.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$rr", turn.RejectReason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose() => _conn.Dispose();
}
