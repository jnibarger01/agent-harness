# Decisions (Phase 2 additions)

Short ADRs for the pieces added on top of `docs/architecture.md`. Each records the failure it
prevents, because a decision without a failure attached gets reversed by the next person who
finds it inconvenient.

## 001 — Kill the boundary, not a `Process` handle

**Decision.** `ExecutionSupervisor` never touches `System.Diagnostics.Process` directly for
termination. It creates an `IIsolationBoundary` (cgroup v2 scope in production, a `setsid`
process group as the explicit, opt-in fallback) and drives it through `StagedTermination`.

**Prevents.** `Process.Kill(entireProcessTree: true)` reconstructs a tree from parent pids; a
double-forked or reparented child escapes it, and the parent can be reported exited while
descendants live. That is the wedge (`aborted=false drained=false forceCleared=true
released=0`) this whole project exists to prevent. A cgroup does not have that failure mode —
membership is a property of the process, not a relationship the killer must infer.

## 002 — The ladder verifies; it does not assume

**Decision.** `StagedTermination` treats "the process handle exited" as insufficient proof of
death. The last rung calls `HasLiveProcessesAsync` and, on any error probing liveness, reports
"still live" rather than "clean" — an unverifiable state is not a verified-clean one.

**Prevents.** A leader process exiting while its children (or a zombie the parent has not yet
reaped) survive, silently passing as a successful kill.

## 003 — Leases carry a fencing token, and it is enforced in SQL

**Decision.** `TryAcquireLeaseAsync` issues a monotonically increasing token. Every
subsequent write (`TryRenewLeaseAsync`, `TryReleaseLeaseAsync`) is conditioned on that token in
the `WHERE` clause; zero affected rows means the caller no longer owns the lease.

**Prevents.** A wedged worker waking after its lease expired and writing stale results over the
successor's work. Owner-id-plus-TTL alone cannot detect this: the late writer believes it is
healthy right up until it tries to write and the row count comes back zero.

## 004 — GrantEnforcer answers one question, the narrow one

**Decision.** `GrantEnforcer.Check` only asks "does this call fit inside the `PolicyDecision`
PolicyBridge already returned" — verdict, expiry, narrowed scope, degraded flag, write-path
containment. It does not decide policy and holds no state between calls.

**Prevents.** The enforcement point quietly growing opinions of its own, which is exactly how a
fourth policy brain gets built one convenient `if` at a time (see `docs/architecture.md`, "Why
.NET, and why not a fourth policy brain").

## 005 — Authority outage never mints a grant

**Decision.** An ACS outage produces no new local grant. A future read-continuity feature may
consume a pre-issued ACS-signed standing grant, but it may not mint a token-0 or otherwise
degraded grant at outage time.

**Prevents.** A grant that passes one layer's degraded-mode rule while failing another layer's
fencing check, creating authority that is dead in some paths and usable in others.

## 006 — Bind grants to the execution that received them

**Decision.** The authority signature covers `attemptId`, fencing token, expiry, `argumentsHash`,
tool, effect, narrowed scope, and authority. The worker verifies the signature and all bindings
before admission.

**Prevents.** A valid grant being replayed on a successor attempt, with a successor lease, or
against modified arguments.

## 007 — No journal, no side effect

**Decision.** The journal append is an execution admission step. Any write failure returns a
denial and the caller must not invoke the tool.

**Prevents.** Unjournaled side effects that cannot be reconciled, replayed, or attributed after a
crash.

## 008 — Terminal attempts cannot renew

**Decision.** Lease renewal requires an unexpired fencing token and is rejected when the attempt
row is `Completed` or `LeaseExpired`.

**Prevents.** A completed-but-unreleased attempt retaining a lane until TTL, or a terminal worker
resurrecting ownership after its state is already final.
