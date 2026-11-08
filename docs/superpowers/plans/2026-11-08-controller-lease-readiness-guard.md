# Controller lease readiness guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent a new lease unless the Controller transaction confirms the machine is approved, fresh, ready, and running the current configuration.

**Architecture:** Extract one rental-eligibility predicate in `ControllerDatabase` and use it both when projecting `MachineDto.CanRent` and when `CreateTrialLease` allocates a machine. The lease path evaluates it from the transaction's current SQLite snapshot using the injected `TimeProvider`.

**Tech Stack:** C#/.NET, SQLite, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-08-controller-lease-readiness-guard.md`

## Global Constraints

- Require approval, a fresh heartbeat no older than 20 seconds, reported readiness `ready`, an applied configuration revision equal to the current machine revision, and no held allocation.
- Evaluate eligibility inside the same Controller database transaction that creates a lease.
- Reject an ineligible request without creating a lease or recording its idempotency key.
- Preserve successful lease preparation, durable idempotent retries, and single-allocation concurrency behavior.
- No Reboot Restore Rx operation is part of this work.

## Review Focus

- Readiness can change between inventory reads and lease allocation — create uses a fresh query inside the transaction; test a transition to `notReady` before create.
- Heartbeat freshness boundary can drift from inventory — test just beyond the existing 20-second cutoff.
- Config acknowledgment can lag after an admin edit — test rejection until the latest revision is acknowledged.
- Idempotency retry must not create an extra lease or consume a failed key — retain the existing durable retry/concurrency test and assert failed requests leave no allocation.
- The current successful flow must remain intact — retain the ready/current-config success case.

## File Map

- Modify `services/Controller/Data/ControllerDatabase.cs` to share the existing `CanRent` projection predicate.
- Modify `services/Controller/Data/LeaseState.cs` to evaluate current eligibility transactionally before inserting a lease.
- Modify `services/Controller.Tests/LeaseTests.cs` to exercise rejected and permitted allocation states through the real database fixture.

### Task 1: Enforce rental readiness during lease allocation

**Files:**
- Modify: `services/Controller/Data/ControllerDatabase.cs`
- Modify: `services/Controller/Data/LeaseState.cs`
- Test: `services/Controller.Tests/LeaseTests.cs`

**Interfaces:**
- Consumes: current machine revision, current config acknowledgment, heartbeat status/time, approval state, and allocation state stored in Controller SQLite.
- Produces: a shared database predicate that returns true only for the currently rentable machine state; `CreateTrialLease` rejects false using its existing conflict exception type.

- [ ] **Step 1: Write the failing behavior tests**
  - Add a focused test proving `CreateTrialLease` rejects `notReady` and `unknown` heartbeats and creates no held allocation.
  - Add a focused test proving a heartbeat older than 20 seconds is rejected.
  - Add a focused test proving a stale config acknowledgment is rejected, then a current acknowledgment permits lease creation.
  - Keep the existing ready/current-config and cross-connection allocation/idempotency tests.
- [ ] **Step 2: Run the focused tests to verify RED**
  - Run `dotnet test services/Controller.Tests/Controller.Tests.csproj --no-restore --filter FullyQualifiedName~LeaseTests` with an isolated `%TEMP%` `BaseOutputPath` so the live Controller process does not lock build outputs.
  - Expected: the new unready/stale-state cases fail because lease creation currently succeeds; pre-existing tests pass.
- [ ] **Step 3: Implement a shared eligibility predicate**
  - Use one helper in `ControllerDatabase` for the exact `CanRent` rules already used by `List()`.
  - In `LeaseState.CreateTrialLease`, read current machine, heartbeat, config acknowledgment, and allocation from the active transaction; reject when the shared predicate is false before inserting the lease or saving the idempotency key.
  - Use the injected `TimeProvider` for freshness. Do not add a second freshness threshold.
- [ ] **Step 4: Run the focused tests to verify GREEN**
  - Run the same isolated focused test command.
  - Expected: all `LeaseTests` pass, including freshness, readiness, current configuration, allocation uniqueness, and idempotency.
- [ ] **Step 5: Run the Controller test suite**
  - Run `dotnet test services/Controller.Tests/Controller.Tests.csproj --no-restore` with a separate isolated `%TEMP%` `BaseOutputPath`.
  - Expected: the full Controller suite passes.
- [ ] **Step 6: Commit**
  - Commit the production and test changes with message `fix(controller): require rentable machine state for lease allocation`.
