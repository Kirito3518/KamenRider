# Agent Presence During Boot Reconciliation Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Keep enrolled Agents visible as reachable while boot reconciliation is blocked without allowing commands or rental readiness.

**Architecture:** Keep the signed Agent sync protocol. Send each heartbeat with ready only after successful reconciliation; otherwise send notReady, apply the authoritative port configuration, observe Controller time, acknowledge successful command-result delivery, and ignore commands and active lease confirmations.

**Tech Stack:** C#/.NET 10, ASP.NET Core hosted services, xUnit.

**Spec:** docs/superpowers/specs/2026-11-05-agent-presence-design.md

## Global Constraints

- A machine is rentable only when approved, ready, configuration-acknowledged, and unallocated.
- An Agent must fail closed when reconciliation, restoration evidence, stream fencing, or health checks are unavailable.
- Restore Rx is configured and operated by the user; the Agent must not call or modify it.

## Review Focus

1. NotReady or Fault reconciliation still emits a heartbeat but never confirms a lease or applies commands.
2. Controller time is observed even while blocked so a local clock rollback cannot extend a persisted lease.
3. A Controller/network failure does not cause a fake ready status or discard an unacknowledged result.

---

## Task 1: Heartbeat while blocked — review pending

**Files:**
- Modify: services/Agent/Startup/BootSyncBarrier.cs
- Modify: services/Agent/Sync/SyncWorker.cs
- Modify: services/Agent/Sync/LeaseCommandDispatcher.cs
- Test: services/Agent.Tests/BootSyncBarrierTests.cs
- Test: services/Agent.Tests/LeaseEnforcerTests.cs

**Interfaces:**
- Consumes: BootSyncBarrier.RunAsync(Func<Task<Readiness>>, Func<Readiness, Task>)
- Produces: BootSyncBarrier runs sync after every completed reconciliation result; LeaseCommandDispatcher.AcceptBlockedAsync(SyncResponse, long requestStartedUptimeMilliseconds, CancellationToken) observes Controller time, acknowledges the prior result batch, and does not process commands or an active lease confirmation.

- [x] Step 1: Add tests for NotReady/Fault heartbeat continuation, blocked command responses, and ready-only pairing offers.
- [x] Step 2: Observe RED. The targeted build first reported the missing AcceptBlockedAsync method; after that method landed, BootSyncBarrierTests failed for NotReady and Fault with callback count zero. A later callback-signature test failed to compile until RunAsync passed Readiness into the callback.
- [x] Step 3: Implement AcceptBlockedAsync to observe Controller time, preserve fail-closed confirmation, and clear only results acknowledged by the successful heartbeat response.
- [x] Step 4: Change BootSyncBarrier to run its sync callback for NotReady/Fault and update SyncWorker to report notReady, apply server configuration, sanitize commands/confirmation, preserve pairing offers only after Ready, and process commands only after Ready.
- [x] Step 5: Run focused tests with an isolated TEMP artifacts path: dotnet test services/Agent.Tests/Agent.Tests.csproj --filter 'FullyQualifiedName~BootSyncBarrierTests|FullyQualifiedName~BlockedSync|FullyQualifiedName~Heartbeat' --artifacts-path <TEMP>\kr-agent-presence-barrier-api-green. Result: 8 passed.
- [x] Step 6: Run the Agent test suite with an isolated TEMP artifacts path: dotnet test services/Agent.Tests/Agent.Tests.csproj --artifacts-path <TEMP>\kr-agent-presence-full. Result: 94 passed, 0 failed.
- [x] Step 7: Commit as fix: keep blocked agents visible without accepting commands.

### Verification note

The broader solution test attempt ran Agent.Tests successfully (94/94). Controller.Tests.Build_DoesNotCreateProductionDatabase failed because --artifacts-path moves AppContext.BaseDirectory, and the test derives Controller.csproj relative to that output directory. The path consequently points under TEMP rather than the repository. This is separate from Task 1 and was left unchanged.