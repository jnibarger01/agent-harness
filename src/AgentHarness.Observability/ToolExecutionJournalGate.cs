using System.Text.Json;
using AgentHarness.Observability;

namespace AgentHarness.Observability;

/// <summary>
/// Admission gate for an irreversible tool call. The journal append is part of admission: if it
/// cannot be durably recorded, the caller receives a denial and must not invoke the tool.
/// </summary>
public sealed class ToolExecutionJournalGate
{
    private readonly ITurnJournal _journal;

    public ToolExecutionJournalGate(ITurnJournal journal) => _journal = journal;

    public async Task<JournalAdmission> TryAdmitAsync(
        Guid toolCallId,
        Guid attemptId,
        string toolId,
        Guid? decisionId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            await _journal.AppendToolCallAsync(
                toolCallId, attemptId, toolId, decisionId, arguments, cancellationToken)
                .ConfigureAwait(false);
            return JournalAdmission.Permit();
        }
        catch (Exception)
        {
            // A missing journal entry destroys the audit and replay boundary. Fail closed: the
            // caller must not execute the side effect after this result.
            return JournalAdmission.Reject("journal write failed; execution is denied");
        }
    }
}

public sealed record JournalAdmission(bool Allowed, string? Reason)
{
    public static JournalAdmission Permit() => new(true, null);
    public static JournalAdmission Reject(string reason) => new(false, reason);
}
