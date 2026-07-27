# Architecture & Policy Decisions (Fork A)

This document is the binding rationale for the `agent-harness` design. It exists so the
`.sln` is not a matter of taste.

## Why .NET, and why not a fourth policy brain

This host already runs two policy authorities:

- **ACS** (`acs-kernel-slice`): fail-closed, default-deny, maker-can't-approve, hash-chained
  audit. Owns a strict work-item lifecycle (`created → evaluated → denied | awaiting_approval
  → approved → executing`). It has **no `succeeded` state the executor sets** — the verdict
  rolls up via verify/replay. No-self-attestation is enforced in code.
- **OpenClaw** control plane: `exec-policy`, `tool-catalog`, `host-env-security`,
  `ToolApproval`.

A .NET harness that also owned routing + policy + tool approval would be a **third** policy
authority. Three fail-closed brains that drift become **fail-inconsistent**. Therefore:

> **Fork A — the harness is a runtime/host, not a policy brain.** Policy is delegated to ACS /
> OpenClaw via `PolicyBridge`. `AgentHarness.Policy` does not exist.

The harness's real value is **isolated, killable worker execution + a durable Run/Attempt
state machine** — the part neither ACS nor OpenClaw does well.

## The wedge we are preventing

Evidence from a live incident: a Telegram session froze with
`aborted=false drained=false forceCleared=true released=0`. Root cause: an incomplete ACP
config (`runtime:{type:acp}` without the `acp` sub-block) caused silent fallback to an
**embedded, non-killable** runtime. The gateway *was* the execution stack, in the same
failure domain.

This harness prevents it structurally:

- Execution is **always out-of-process** (`ExecutionSupervisor` spawns a worker).
- Cancellation is **two-tier**: cooperative `CancellationToken` → grace window → kill the
  **process group** (`kill(-pgid, SIGKILL)`), because `Process.Kill(entireProcessTree:true)`
  does not reliably reap a detached grandchild on Linux.
- Leases are **durable** (SQLite) with TTL + fencing token; a reclaim pass on startup kills
  any worker whose lease expired.

## PolicyBridge: an artifact, not a boolean

```csharp
public sealed record PolicyDecision {
    public Guid DecisionId { get; init; }
    public Verdict Verdict { get; init; }            // Allow | Deny | RequireApproval
    public ToolScope NarrowedScope { get; init; }    // harness enforces; never re-derives
    public DateTimeOffset ExpiresAt { get; init; }
    public string InputsHash { get; init; }
    public string Authority { get; init; }           // "acs" | "openclaw"
}
```

- Authority outage: return no new grant. If read continuity is ever required, use a pre-issued
  ACS-signed standing grant; never mint authority locally during an outage.
- Every `ToolCall` carries `DecisionId`. `ITurnJournal.ReplayViolationsAsync` joins
  `ToolCall → PolicyDecision` and flags any call with no `DecisionId` or args outside
  `NarrowedScope`. "Policy-consuming" is thereby *proven*, not asserted.

## Lifecycle subordination

`Attempt` carries `WorkItemId` (externally minted by ACS). Attempt states ⊆ ACS's set;
`Completed` = "worker reported observations," **not** a success verdict. The harness reports;
ACS rolls up. This keeps ACPX's worst-wins / no-self-attestation rule intact — a harness that
declares its own `Succeeded` has already broken it.

Retry and timeout are **ACS work-item properties**; the harness executes Attempts and does not
decide whether to retry. This prevents the retry/timeout disagreement that a second lifecycle
would otherwise guarantee.

## Config is a manifest, not authorization

`"tools": [...]` in `appsettings` lists what is linked/available. Authorization is always
runtime-queried via `PolicyBridge`. The config file can never widen authority.

## Seam test (what is policy vs runtime)

> Policy is anything that could answer differently for two identical requests.

- Timeouts / concurrency caps: policy-*configured*, runtime-*enforced* (never a bridge verdict).
- Write scope: ACS decides the boundary; the harness enforces containment.
- Tool allow/deny: pure policy, exclusively ACS/OpenClaw.
- Ambiguous case: `Deny` (default-deny, matching ACS).

## Durable authority over in-memory

`System.Threading.Channels` is a local accelerator only. The system of record is SQLite
(inbox/outbox/store/journal). PostgreSQL replaces SQLite in Phase 5 by re-implementing the
same interfaces — the claim logic does not change.

## Phase 2: from stated principle to enforcing code

The principles above were correct from the start; three of them shipped as prose with no code
behind them. `AgentHarness.Isolation`, the `IHarnessStore` lease methods, and
`AgentHarness.Tools.Enforcement` close that gap. See `docs/DECISIONS.md` for the short form.

- **Killable execution (principle #2).** `ExecutionSupervisor` used to spawn a bare
  `System.Diagnostics.Process` and call empty `NativeSetProcessGroup`/`NativeKillProcessGroup`
  stubs. `IIsolationBoundary` (cgroup v2 in production, process group via `setsid`/`killpg` as
  the explicit fallback) plus `StagedTermination` make the two-tier cancel real: cooperative
  token → protocol cancel → grace → SIGTERM → grace → kill the boundary → **verify** no
  processes remain. `tools/AgentHarness.IsolationSmoke` proves it against a workload that traps
  SIGTERM and forks a child — the wedge shape — rather than asserting it in a comment.
- **Fencing tokens (principle #1, "one logical authority").** `Lease.OwnerToken` was
  documented as a fencing token but `SaveLeaseAsync` was a blind `INSERT OR REPLACE` — nothing
  stopped a wedged holder from overwriting a successor's lease. `IHarnessStore` now exposes
  `TryAcquireLeaseAsync`/`TryRenewLeaseAsync`/`TryReleaseLeaseAsync`, each conditioned on the
  fencing token inside the SQL `WHERE` clause; zero affected rows is how a stale holder finds
  out it no longer owns anything.
- **Enforcement at the call site (principles #5-#7).** Journaled replay could previously only
  *detect* a policy violation after the fact. `GrantEnforcer.Check` is the point a worker loop
  calls before invoking a tool: deny-by-default, checks verdict/expiry/narrowed scope, and (via
  `PathContainment`) the write-scope containment the "Seam test" section already
  promised but had no code enforcing.

### Phase 2 safety contracts

`SignedGrant` is the worker-facing authority artifact. Its ECDSA payload binds the grant to the
`attemptId`, current fencing token, expiry, `argumentsHash`, tool, effect, narrowed scope, and
authority. `GrantEnforcer.CheckSigned` rejects a valid signature presented for another attempt,
lease, expiry, or argument set. The harness never creates an outage-time substitute grant.

`ToolExecutionJournalGate` makes the journal write part of execution admission. A failed append
returns a denial; callers must stop before invoking the side effect. Lease renewal also checks
the attempt row, so `Completed` and `LeaseExpired` attempts cannot keep renewing their lane.
