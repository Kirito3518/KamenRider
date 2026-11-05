# Agent Presence During Boot Reconciliation Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Keep enrolled Agents visible as reachable while boot reconciliation is blocked without allowing commands or rental readiness.

**Architecture:** Keep the signed Agent sync protocol. Send each heartbeat with ready only after successful reconciliation; otherwise send notReady, observe Controller time, and accept no commands or lease confirmation. The Controller presence-only path updates heartbeat/config metadata without processing command results or changing boot, lease, or command state; the Agent retains result batches until a Ready sync processes them. Apply blocked-response config after observing authenticated time so config storage failure cannot discard the sample.

**Tech Stack:** C#/.NET 10, ASP.NET Core hosted services, xUnit.

**Spec:** docs/superpowers/specs/2026-11-05-agent-presence-design.md

## Global Constraints

- A machine is rentable only when approved, ready, configuration-acknowledged, and unallocated.
- An Agent must fail closed when reconciliation, restoration evidence, stream fencing, or health checks are unavailable.
- Restore Rx is configured and operated by the user; the Agent must not call or modify it.

## Review Focus

1. NotReady or Fault reconciliation still emits a heartbeat but never confirms a lease or applies commands.
2. Controller time is observed even while blocked so a local clock rollback cannot extend a persisted lease.
3. A Controller/network failure or presence-only response does not cause a fake ready status or discard unacknowledged command results; current configuration ACK is required for availability.

---

## Task 1: Heartbeat while blocked — complete

**Files:**
- Modify: services/Agent/Startup/BootSyncBarrier.cs
- Modify: services/Agent/Sync/SyncWorker.cs
- Modify: services/Agent/Sync/LeaseCommandDispatcher.cs
- Add: services/Agent/Sync/AgentSyncCycle.cs
- Modify: services/Controller/Data/SyncState.cs
- Modify: services/Controller/Commands/CommandStore.cs
- Modify: services/Controller/Data/ControllerDatabase.cs
- Test: services/Agent.Tests/BootSyncBarrierTests.cs
- Test: services/Agent.Tests/LeaseEnforcerTests.cs
- Test: services/Agent.Tests/AgentSyncCycleTests.cs
- Test: services/Controller.Tests/BootTests.cs
- Test: services/Controller.Tests/LeaseTests.cs

**Interfaces:**
- Consumes: BootSyncBarrier.RunAsync(Func<Task<Readiness>>, Func<Readiness, Task>)
- Produces: BootSyncBarrier runs sync after every completed reconciliation result; LeaseCommandDispatcher.AcceptBlockedAsync(SyncResponse, long requestStartedUptimeMilliseconds, CancellationToken) observes Controller time, retains results not processed by the presence-only response, and does not process commands or an active lease confirmation.

- [x] Step 1: Add tests for NotReady/Fault heartbeat continuation, blocked command responses, and ready-only pairing offers.
- [x] Step 2: Observe RED. The targeted build first reported the missing AcceptBlockedAsync method; after that method landed, BootSyncBarrierTests failed for NotReady and Fault with callback count zero. A later callback-signature test failed to compile until RunAsync passed Readiness into the callback.
- [x] Step 3: Implement AcceptBlockedAsync to observe Controller time and preserve fail-closed confirmation while retaining result batches because presence-only responses do not process them.
- [x] Step 4: Change BootSyncBarrier to run sync for NotReady/Fault; add Controller presence-only handling that does not mutate boot/lease/command history; process blocked server time before config persistence; sample request uptime after pairing discovery; and require the current config ACK for rent availability.
- [x] Step 5: Run focused tests with an isolated TEMP artifacts path: dotnet test services/Agent.Tests/Agent.Tests.csproj --filter 'FullyQualifiedName~BootSyncBarrierTests|FullyQualifiedName~BlockedSync|FullyQualifiedName~Heartbeat' --artifacts-path <TEMP>\kr-agent-presence-barrier-api-green. Result: 8 passed.
- [x] Step 6: Run the Agent test suite with an isolated TEMP artifacts path: dotnet test services/Agent.Tests/Agent.Tests.csproj --artifacts-path <TEMP>\kr-agent-presence-full. Result: 97 passed, 0 failed.
- [x] Step 7: Commit as fix: keep blocked agents visible without accepting commands.

### Verification note

The broader solution test attempt ran Agent.Tests successfully (94/94). Controller.Tests.Build_DoesNotCreateProductionDatabase failed because --artifacts-path moves AppContext.BaseDirectory, and the test derives Controller.csproj relative to that output directory. The path consequently points under TEMP rather than the repository. This is separate from Task 1 and was left unchanged.
### Fix review wave 1

- Controller regression: blocked notReady/unknown heartbeats on boot B preserve boot A active lease and extension command; verified challenge then returns ResumeLease with the original expiry. Retired boot replay remains rejected.
- AgentSyncCycle regression: failed HTTP and repeated blocked responses retain pending results; authenticated time is observed before config write failure for Ready and blocked states; Ready sync clears results only after config succeeds and response processing completes. Pairing discovery delay does not enter the request RTT sample.
- Availability regression: approved machine becomes non-rentable immediately after config revision changes, remains so with stale ACK, and becomes rentable after matching ACK.
- Focused tests: Controller 5 passed; AgentSyncCycle 4 passed, including Ready and blocked config-write failures.
- Full Agent suite after the Ready-path follow-up: 97 passed, 0 failed.
- Broader solution attempt before that Agent-only follow-up: Agent.Tests 96 passed; Controller.Tests 99 passed, one known HostTests.Build_DoesNotCreateProductionDatabase failure because --artifacts-path moves the relative project lookup under TEMP.
- Full diff check passed.

- [x] Step 8: Commit the review fix wave and complete scoped re-review. Fix commit e75ea6520b91c78acc2039fae44ac56745a6af24; all four findings addressed; no new Critical/Important breakage.
