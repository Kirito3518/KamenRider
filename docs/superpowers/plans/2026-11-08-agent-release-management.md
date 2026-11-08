# Agent release management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a stable/beta Agent release dashboard and a machine-scoped, maintenance-locked Agent update flow with package verification and local rollback.

**Architecture:** Controller owns release metadata, artifacts, update tickets, operation status, and the transactional maintenance lock. The Admin app publishes immutable full packages and issues a one-time machine-bound PowerShell command; the child stages and swaps binaries while preserving its identity/config/data and retaining a rollback copy.

**Tech Stack:** Next.js 16 App Router, C#/.NET 10 Windows Service, SQLite, PowerShell 5.1/7, xUnit, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-08-agent-release-management.md`

## Global Constraints

- Start a maintenance lock atomically only when the machine has no held lease.
- A maintenance-locked machine is not rentable; `CanRent` and lease creation enforce this in the Controller.
- Updates are per-machine and manual. No background rollout or bulk action is in scope.
- Package hashes must match before extraction or file replacement. Paths from uploaded ZIP entries must not escape the staging directory.
- The update operation is bound to one machine, one release, one target version, one package hash, and one short-lived ticket. Replayed starts/downloads and machine mismatch are rejected; recovery replaces the prior ticket without creating a new operation.
- Update reservation requires a versioned heartbeat no older than 20 seconds; an unstarted ticket expires after 10 minutes. Once started, only target-version or verified rollback-version heartbeat resolves the lock.
- Uploaded ZIP size is limited to 100 MiB, expanded size to 500 MiB, and entry count to 5,000; dirty development packages are rejected for release.
- Release package builds write outside `public/` and carry a release-build marker; enrollment's existing static package flow remains separate.
- Preserve the machine identity, private data, existing Agent configuration, service identity, and service dependencies while applying an update.
- On uncertain service or rollback state, fail closed: keep the maintenance lock and do not expose the machine as rentable.
- An Admin can renew a ticket for the same failed/interrupted locked operation; recovery never creates a second operation or unlocks the machine early.
- Reboot Restore Rx remains untouched.

## Review Focus

- A lease races with update reservation — both operations serialize in Controller; test both orderings and assert one winner.
- A stale or old-version heartbeat arrives after update starts — it must not clear the lock; test that only target or verified rollback confirmation resolves it.
- A one-time ticket is replayed, expires, or is supplied with a different machine ID — reject without downloading or applying the package.
- Uploaded ZIP contains a traversal entry, duplicate manifest, wrong version, or excessive size — reject before publishing or extraction.
- The process exits between backup and new service start — preserve recovery material and keep the machine locked until recovery is confirmed.
- An unstarted ticket expires while an update is running — expire only the unstarted reservation; keep a started operation locked until a verified outcome.

## File Map

- Controller migrations, contracts, `ControllerDatabase`, and machine/agent endpoints own releases, update operations, and the lock.
- `scripts/agent/Build-AgentPackage.ps1` and package tests emit and validate the release manifest.
- Controller release-store code validates uploads and serves package bytes only after ticket authorization.
- Admin API routes authenticate session/origin, then call Controller; the dashboard never handles service credentials.
- `scripts/agent`/`public/agent` updater scripts validate identity and perform stage/swap/rollback; PowerShell tests use temporary files and fakes.
- `src/app/admin/agent-releases`, `src/components/admin`, and `AdminShell` implement the screenshot-inspired release dashboard and per-machine update actions.

### Task 1: Add Controller release/operation records and maintenance lock

**Files:**
- Create: `services/Controller/Data/Migrations/012-agent-releases.sql`
- Modify: `services/Contracts/Machines.cs`
- Modify: `services/Controller/Data/ControllerDatabase.cs`
- Modify: `services/Controller/Data/LeaseState.cs`
- Modify: `services/Controller/Data/SyncState.cs`
- Modify: `services/Controller/Machines/MachineEndpoints.cs`
- Test: `services/Controller.Tests/LeaseTests.cs`, new `AgentUpdateTests.cs`, and database migration tests

**Interfaces:**
- Consumes: approved machine state, held lease state, and Agent heartbeat version from Controller SQLite.
- Produces: a machine-scoped update reservation/maintenance lock; a new lease cannot be allocated while an update operation is active.
- Operation statuses: `awaitingInstaller`, `installing`, `awaitingHeartbeat`, `rollingBack`, `completed`, `rolledBack`, `expired`, `failed`. `completed` and `rolledBack` require a confirming heartbeat; a started `failed` operation remains locked until recovery is confirmed.

- [x] **Step 1: Add failing tests** for update reservation with a held lease, lease/update race serialization, `CanRent=false` during maintenance, expired unstarted ticket cleanup, failed-operation lock retention, same-operation recovery-ticket renewal, and target/rollback heartbeat resolution.
- [x] **Step 2: Run focused Controller tests** with isolated output; verify the new behavior fails before implementation.
- [x] **Step 3: Add migration 012 and Controller transaction methods** for release metadata/update operation state. Reserve the lock only after confirming no allocation exists in the same transaction; preserve a one-active-operation-per-machine invariant.
- [x] **Step 4: Reuse the existing rental predicate** to include active maintenance; resolve operations only through the specified version/ticket-confirmed transitions.
- [x] **Step 5: Run focused and full Controller tests**, then commit `feat(controller): add Agent update maintenance locks`.

### Task 2: Publish immutable, versioned Agent packages

**Files:**
- Modify: `scripts/agent/Build-AgentPackage.ps1`
- Modify: `services/Controller/Program.cs`
- Create: `services/Controller/Agents/AgentReleaseStore.cs`
- Modify: Controller contracts, endpoints, and migration 012 as required by Task 1 interfaces
- Test: `scripts/agent` package validation tests and new `AgentReleaseStoreTests.cs`

**Interfaces:**
- Consumes: `AgentPackage.zip`, channel `stable|beta`, release notes, version manifest, and Admin actor metadata. The build accepts an explicit release version that must equal the Agent's embedded informational version.
- Produces: immutable release metadata and a private content-addressed artifact, returned only by authorized Controller endpoints.

- [x] **Step 1: Add failing package tests** for manifest generation, release/private output mode, package/version mismatch, ZIP traversal, duplicate entries, maximum size, and immutable version/hash conflicts.
- [x] **Step 2: Implement the package manifest** with exact Agent informational version, source commit, clean/dirty state, release-build marker, and package format version; allow the build caller to set a release version and include the manifest in the archive. Release mode defaults to a private artifact directory outside `public/`; the existing enrollment build output remains compatible.
- [x] **Step 3: Implement Controller validation/storage** with streaming size limits, server-computed SHA-256, atomic publication, and no overwrite of existing release bytes.
- [ ] Reject packages above 100 MiB compressed, 500 MiB declared expanded size, or 5,000 entries; reject dirty or non-release manifests and repeated versions with different hashes.
- [x] **Step 4: Run focused package/store tests** without installing services or MSI, then commit `feat(agent): publish immutable versioned packages`.

### Task 3: Add update tickets, authenticated Admin APIs, and package download

**Files:**
- Modify: `services/Contracts/Machines.cs` and update-operation contracts
- Modify: `services/Controller/Machines/MachineEndpoints.cs` and Agent endpoints
- Create: `src/app/api/admin/agent-releases/route.ts`
- Create: `src/app/api/admin/machines/[machineId]/agent-update-command/route.ts`
- Create: `services/Controller/Data/Migrations/013-agent-update-downloads.sql`
- Test: Next route authorization tests and Controller update-ticket tests

**Interfaces:**
- Consumes: Admin-selected machine/release, same-origin authenticated Admin request, Controller release metadata.
- Produces: short-lived operation ticket, fixed package hash/version, and one-time copyable PowerShell update command; Controller's HTTPS package endpoint authorizes download only for that operation/ticket. Recovery issues a new ticket for the same active operation without creating or releasing a lock.

- [ ] **Step 1: Add failing route/API tests** for unauthenticated/non-admin/cross-origin requests, wrong machine, wrong release, ticket replay, expiry, and download without a valid update ticket.
- [ ] **Step 2: Add migration 013 for durable per-ticket start/package-download consumption. Implement Controller operation issue/start/download/status/recovery endpoints** while keeping package selection and target version immutable for that operation; one ticket authorizes one start, one package download, and operation status reports, and a recovery ticket replaces it on the existing failed/interrupted operation and resets its one-download allowance.
- [ ] **Step 3: Implement Admin publish/list/command routes** using the existing session and same-origin checks; avoid minting tickets on GET/mount.
- [ ] **Step 4: Implement ticketed Controller package retrieval** with bounded response, exact stored hash, and no public static path.
- [ ] **Step 5: Run focused route and Controller tests**, then commit `feat(admin): issue machine-scoped Agent update commands`.

### Task 4: Implement Windows Agent update with rollback

**Files:**
- Create: `scripts/agent/Update-AgentPackage.ps1`
- Create: `public/agent/Update-Agent.ps1`
- Modify: `scripts/agent/Build-AgentPackage.ps1`
- Modify: `scripts/agent` PowerShell tests

**Interfaces:**
- Consumes: Controller URL, machine ID, operation ID/ticket, fixed release version/hash, trusted root thumbprint, and Admin return URL.
- Produces: verified update status; either the requested version is running or the original installation is restored and the Controller receives rollback status.

- [ ] **Step 1: Add failing plan/mocked tests** for wrong machine, invalid hash, unexpected service identity/path, unsafe ACL/reparse point, and package traversal; assert no service/file mutation before validation.
- [ ] **Step 2: Add transaction-plan tests** for backup, stage/swap, start failure rollback, interrupted swap recovery, and identity/config preservation using temporary files/fake service APIs only.
- [ ] **Step 3: Implement the updater** to verify ticket/package/hash/manifest and existing private paths; preserve `identity.json`, enrollment receipt, data, and `appsettings.json`; retain a protected backup; stop/restart only managed Agent/Guard services while keeping the verified stream fence closed.
- [ ] **Step 4: Implement rollback and status reporting**; keep the Controller lock until target/previous-version heartbeat is confirmed. Preserve backups until one of those outcomes is confirmed; a fresh Admin recovery command uses the same operation.
- [ ] **Step 5: Run PowerShell 5.1 and PowerShell 7 mock/plan suites**, build the updated package, and commit `feat(agent): update installed binaries with rollback`.

### Task 5: Build the Admin release dashboard and per-machine Update Agent action

**Files:**
- Create: `src/app/admin/agent-releases/page.tsx`
- Create: `src/components/admin/agent-releases-manager.tsx`
- Modify: `src/components/admin/admin-shell.tsx` or the shared Admin sidebar navigation
- Modify: `src/components/admin/machines-manager.tsx`
- Modify: machine/release contracts and UI tests

**Interfaces:**
- Consumes: Controller release list, machine versions/update statuses, Admin upload/command routes.
- Produces: screenshot-inspired summary cards, alerts, release history, stable/beta upload/publish form, and explicit per-machine command-copy flow.

- [ ] **Step 1: Add failing UI tests** for summary counts, stable/beta labels, warnings, upload validation, per-machine update lock state, and copyable command flow.
- [ ] **Step 2: Implement the dashboard** with accessible status text, bounded polling, release history, and upload controls; require the uploaded package manifest version to match the displayed release version; show SHA-256 status without exposing credentials/tickets before an Admin explicitly requests a command.
- [ ] **Step 3: Add the separate per-machine Update Agent button**; disable while a lease is held, show awaiting-installer/installing/awaiting-heartbeat/rolling-back/failed/rolled-back results, and offer recovery for a failed/interrupted locked operation.
- [ ] **Step 4: Verify relevant Next.js 16.4 docs and run focused UI/API tests plus TypeScript/build checks**, then commit `feat(admin): add Agent release dashboard`.

### Task 6: Reconcile operational acceptance and documentation

**Files:**
- Modify: `docs/full-rental-roadmap.md`
- Modify: `docs/agent-acceptance.md`
- Test: Controller integration tests and Admin route/UI tests

- [ ] **Step 1: Add an end-to-end software scenario** for publish → reserve update with no lease → reject concurrent lease → apply/rollback → heartbeat confirmation → readiness gate.
- [ ] **Step 2: Run relevant .NET, PowerShell 5.1/7, auth, machine UI, TypeScript, lint, and production build validations.** Use isolated output directories while Controller is live.
- [ ] **Step 3: Record tested software behavior and explicitly separate untested Windows service, firewall, Sunshine, VNC, external network, cold-boot, and hardware acceptance. Do not inspect or change Reboot Restore Rx.**
- [ ] **Step 4: Commit `docs: record Agent release update acceptance`.
