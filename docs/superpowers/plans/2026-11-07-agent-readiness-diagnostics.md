# Agent readiness diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show administrators why an enrolled Agent is not ready while preserving current fail-closed behavior.

**Architecture:** Boot reconciliation records fixed diagnostic check IDs and statuses. The existing signed Agent heartbeat carries the optional bounded report to the Controller. The Controller validates and stores it with heartbeat presence, returns it only from the Admin machine list, and the Admin UI renders localized labels.

**Tech Stack:** Existing C#/.NET services and contracts, SQLite migrations/maintenance validator, Next.js 16 Client Component, React Testing Library/Vitest.

**Spec:** `docs/superpowers/specs/2026-11-07-agent-readiness-diagnostics-design.md`.

## Global Constraints

- Work in the authorized main checkout on `master`; do not create or switch worktrees.
- Use Luna Max for every delegated subagent.
- Do not query, inspect, configure, or modify Reboot Restore Rx.
- Do not install services or change machine, firewall, Sunshine, VNC, certificate, or `.env.local` state.
- Preserve the existing fail-closed boot, health, stream-gate, `ready`, and `CanRent` behavior. Diagnostics are display-only.
- The exact ordered check IDs are `baseline`, `bootChallenge`, `restoration`, `streamFence`, `desktopHealth`, `configurationRevision`, `leaseReconciliation`, and `runtimeHealth`.
- The exact statuses are `passed`, `blocked`, `unavailable`, and `unknown`; the report has at most eight entries with unique check IDs.
- Keep old Agent heartbeat requests without diagnostics valid and clear any stale stored report when a new heartbeat omits it.
- Read installed Next.js docs relevant to the Admin Client Component before writing application code.

## Review Focus

- Missing or expired boot challenge blocks readiness and marks later, unvisited checks `unknown`.
- The default unqualified baseline and boot providers report `unavailable`, not a claimed hardware failure or pass.
- A malformed, oversized, duplicate, or unknown diagnostic cannot enter Controller storage.
- An old Agent with no diagnostic property stays compatible and clears the prior report.
- Changing diagnostic values alone cannot change `CanRent`; a `notReady` heartbeat remains non-rentable even if its report contains only `passed` checks.
- Diagnostic fields never appear in the renter-facing `/api/machines` response.

---

### Task 1: Report and persist Agent readiness diagnostics

**Files:**
- Modify: `services/Contracts/AgentSync.cs` — optional signed-heartbeat diagnostic property.
- Modify: `services/Contracts/Machines.cs` — optional admin machine diagnostic property.
- Modify: `services/Agent/Startup/BootReconciler.cs` — collect statuses from the existing checks.
- Modify: `services/Agent/Startup/UnavailableBootEnvironment.cs` and baseline evidence implementation — return explicit unavailable status.
- Modify: `services/Agent/Sync/AgentSyncRequestFactory.cs` and `services/Agent/Sync/SyncWorker.cs` — attach the latest bounded report.
- Modify: Agent boot reconciliation and sync factory tests — prove status mapping and unchanged gate behavior.
- Create: `services/Controller/Data/Migrations/011-readiness-diagnostics.sql` — one optional report per machine and schema version 11.
- Modify: `services/Controller/Data/ControllerDatabase.cs` and `services/Controller/Data/SyncState.cs` — validate, atomically upsert/clear, and return the report.
- Modify: `services/Controller/Data/ControllerDatabaseMaintenance.cs` — require/validate the new table only for schema version 11+ while allowing supported historical backups.
- Modify: Controller sync, machine-list, migration, and maintenance tests.
- Modify: `services/Controller.Tests/SyncHttpTests.cs` — prove diagnostics remain inside the signed request body and tampering is rejected.

**Interfaces:**
- Consumes: current `BootReconciler` checks, authenticated signed `SyncRequest`, SQLite migration sequence, and `ControllerDatabase.List()` snapshot.
- Produces: `ReadinessDiagnostic(string CheckId, string Status)` and optional `SyncRequest.ReadinessDiagnostics`; `MachineDto.ReadinessDiagnostics`; explicit `BootEvidenceStatus` values from baseline and boot environment checks.

- [x] **Step 1: Write Agent regression tests first.** Cover the exact status order for default unavailable baseline, a missing/expired/mismatched challenge, each false evidence check, all-positive evidence, a failed runtime recheck, and the Agent request carrying a copy of the report. Assert readiness/gate outcomes stay as before.
- [x] **Step 2: Run the focused Agent tests and capture the expected RED result.** Run `dotnet test services/Agent.Tests/Agent.Tests.csproj --no-restore --filter FullyQualifiedName~BootTests`; it must fail because diagnostic contract/results are absent.
- [x] **Step 3: Implement Agent report collection.** Add explicit `BootEvidenceStatus` results (`Passed`, `Blocked`, `Unavailable`, `Unknown`) to baseline/evidence interfaces, make default evidence unavailable, maintain an eight-entry canonical report in `BootReconciler`, and include it in `AgentSyncRequestFactory`. Continue using the same `Readiness` decisions and gate calls.
- [x] **Step 4: Run focused Agent tests, then the full `Agent.Tests` suite.** Both must pass; record exact counts.
- [x] **Step 5: Write Controller tests first.** Cover validation of null entries, unknown IDs/statuses, duplicates and >8 entries; optional/omitted old-Agent reports; replacement and clearing in the same heartbeat transaction; admin-list round trip; that diagnostics alone do not change `CanRent`; schema 11 validation; acceptance of supported pre-11 backups; and signed-heartbeat acceptance/rejection when diagnostic bytes are altered after signing.
- [x] **Step 6: Run the focused Controller sync tests and capture the expected RED result.** Run `dotnet test services/Controller.Tests/Controller.Tests.csproj --no-restore --filter FullyQualifiedName~SyncTests`; the new diagnostics assertions must fail because Controller has no diagnostics contract or persistence yet.
- [x] **Step 7: Implement Controller persistence and schema validation.** Add migration 011, strict allowlist/limit validation, canonical-order serialization, same-transaction report upsert/delete, admin DTO projection, and version-aware backup/restore validation. Do not change readiness or lease logic.
- [x] **Step 8: Run focused Controller tests, then the full `Controller.Tests` suite.** Run `dotnet test services/Controller.Tests/Controller.Tests.csproj --no-restore`; both focused and full tests must pass; record exact counts.
- [x] **Step 9: Commit Task 1** as `feat(agent): report readiness diagnostics`.

---

### Task 2: Display readiness diagnostics only to administrators

**Files:**
- Modify: `src/lib/machine-contracts.ts` — optional typed diagnostic list.
- Modify: `src/components/admin/machines-manager.tsx` — localized checklist and diagnostic-only caption.
- Modify: `tests/machines-manager.test.tsx` — rendered mixed-status and absent-report cases.
- Modify: `tests/machines.test.mjs` or the existing public machine route test — prove `/api/machines` omits the diagnostics.
- Modify: `docs/full-rental-roadmap.md` and `docs/agent-acceptance.md` — record current implementation and verification without promoting hardware acceptance.

**Interfaces:**
- Consumes: Task 1's `MachineDto.ReadinessDiagnostics?: ReadinessDiagnostic[]` response.
- Produces: an Admin-only checklist for latest check IDs/statuses, with absent reports represented as not reported.

- [x] **Step 1: Write the failing component and public-route regressions first.** Assert Thai labels for mixed statuses, a clear not-reported state when absent, and no readiness diagnostics in renter-facing JSON.
- [x] **Step 2: Run the UI and machine route tests and capture the expected RED result.** The focused UI run failed as expected with 2 new failures and 3 prior passes. The first route-harness attempt against stale `.next-local` failed unrelated existing PATCH/expiry checks; the fresh isolated build and route run later passed.
- [x] **Step 3: Implement the Admin view and public response assertion.** Keep diagnostics under the Admin machine card only; do not change the public API mapping or rental actions.
- [x] **Step 4: Run UI tests, `pnpm test:machines`, `pnpm exec tsc --noEmit`, targeted ESLint, and an isolated Webpack build.** Focused UI tests passed 6/6; `NEXT_DIST_DIR=.next-task2-validation pnpm test:machines` passed (including 9/9 helper tests and route scenarios); TypeScript and isolated Webpack build passed. Targeted ESLint reports the pre-existing `react-hooks/set-state-in-effect` error at `machines-manager.tsx:128`; no diagnostics-specific finding was reported.
- [x] **Step 5: Update the roadmap and acceptance record.** State that the diagnostics improve visibility only; boot/restoration/desktop proof, installed Agent/Guard, live VNC/Sunshine/WOL, and hardware acceptance remain NOT READY / NOT TESTED.
- [x] **Step 6: Commit Task 2** as `feat(admin): show agent readiness diagnostics`.

## Completion

- [ ] Obtain a fresh Luna Max review of each task diff using the SDD task-review process.
- [ ] Confirm the working tree is clean and the full range has no production readiness claim unsupported by hardware evidence.
