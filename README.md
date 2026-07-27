# AgentHarness

A .NET agent harness: **one routing choke point, isolated killable execution, durable
authority.** Designed to survive the failure classes that froze a live Telegram lane
(the OpenClaw-style "embedded runtime wedge": `aborted=false drained=false
forceCleared=true released=0`).

This is **Fork A**: the harness is a *runtime/host*, **not** a policy brain. Policy is owned
by an external authority (ACS / OpenClaw). See `docs/architecture.md` for the full decision
rationale. `AgentHarness.Policy` does **not** exist by design.

## Principles (enforced in code, not docs)

1. **One logical authority — not one process/queue/thread.** The durable state store
   (`Turn`/`Run`/`Attempt`/`Lease`) is the authority. The control-plane process may crash.
2. **Out-of-process execution.** `ExecutionSupervisor` spawns worker processes. Two-tier
   cancel: cooperative `CancellationToken` → grace window → kill the **process group**
   (pgid), not just `Process.Kill`. Embedded, non-killable execution is the wedge bug.
3. **Durable inbox/outbox, not `Channel<T>` as authority.** `Channel<T>` is only a local
   accelerator. A host crash must not lose accepted work.
4. **`Attempt` is SUBORDINATE to ACS.** Every `Attempt` carries a `WorkItemId` it did NOT
   mint. Attempt states are a strict subset of ACS's lifecycle; `Attempt` can never
   self-attest `Succeeded` — ACS rolls up the verdict (no-self-attestation).
5. **Policy is an artifact, not a boolean.** `PolicyBridge` returns `PolicyDecision
   { decisionId, verdict, narrowedScope, expiresAt, inputsHash }`. The harness *enforces*
   `narrowedScope`; it never re-derives it (re-deriving = fail-open).
6. **Degraded mode fails closed.** ACS unreachable → `Deny` everything except ACS's own
   `read`-class tools. Every degraded decision is journaled (`Degraded=true`) for audit.
7. **Every `ToolCall` carries `DecisionId`.** `ITurnJournal.ReplayViolationsAsync` proves the
   runtime never acted outside granted authority. "Policy-consuming" is verified, not asserted.
8. **`appsettings` tools = capability manifest, not authorization.** Authorization is always
   runtime-queried. The config file can never widen authority.
9. **Config snapshot at lane claim.** `IOptionsMonitor` swaps are applied at boundaries only;
   last-known-good is retained on bad config.
10. **BackgroundService creates a scope per iteration** (no leaked DbContext/singletons).

## Layout

```
src/
  AgentHarness.Domain/         Turn, Run, Attempt, Lease, ToolCall, Conversation, Artifact, Delivery (state machines)
  AgentHarness.Persistence/    IInbox/IOutbox/IHarnessStore + SQLite impl (system of record)
  AgentHarness.Observability/  ITurnJournal + SQLite impl (append-only, replay violations)
  AgentHarness.PolicyBridge/   IPolicyBridge + PolicyDecision artifact (Fork A; NO local policy)
  AgentHarness.Execution/       ExecutionSupervisor (killable workers, 2-tier cancel, lease reclaim)
  AgentHarness.Worker/          Killable worker process (takes CT; reports observations)
  AgentHarness.ControlPlane/    Generic Host + BackgroundService inbox processor
tests/
  AgentHarness.Tests/           State-machine + inbox-replay (crash) failure tests
```

## Build order

- **Phase 1 (implemented here):** Domain entities, SQLite persistence, inbox/outbox,
  console ingress, one worker process, heartbeats/deadlines/cancel/process-group kill.
- Phase 2+: leases + attempt reclamation, restart recovery, idempotent inbound, delivery
  retries, failure tests, model routing (MEAI), real channels, ACP adapter, PostgreSQL,
  multiple control-plane replicas.

## Running

Requires .NET 9 SDK.

```
dotnet test            # runs the crash-replay + state-machine failure tests
dotnet run --project src/AgentHarness.ControlPlane
```

## Note on this scaffold

`dotnet` is not installed in the environment where this was generated, so the solution has
**not** been compiled here. The `.cs`/`.csproj`/`.sln` files are written to be valid; run
`dotnet build` on a machine with the SDK to verify.
