# Agent Sunshine Stream Gate Implementation Plan

> For agentic workers: use superpowers:executing-plans and complete each task with its stated test command before moving to the next task.

**Goal:** Add a supervised local Windows stream gate that keeps Sunshine closed until a confirmed lease is active and shuts existing streams down at expiry or Agent disconnect.

**Architecture:** Keep the Agent under its restricted virtual service account and add a LocalSystem Stream Guard service in the existing Agent executable. Communicate over a local named pipe ACLed to the Agent service SID. The guard coordinates an all-profile program-scoped firewall block rule with the Sunshine Windows service, and accepts a lease-bound grant with a current-boot monotonic deadline.

**Tech Stack:** C#/.NET 10 Windows Services, Windows named pipes and ACLs, Windows Defender Firewall COM API, Service Control Manager, xUnit, Windows PowerShell 5.1.

**Spec:** docs/superpowers/specs/2026-11-06-agent-stream-gate-design.md; docs/full-rental-roadmap.md section 6; docs/sunshine-compatibility.md.

## Global Constraints

- Do not query, configure, or modify Reboot Restore Rx.
- Do not install services, add firewall rules, stop Sunshine, or change host state on the development workstation.
- The Agent remains under NT SERVICE\KamenRaider Agent; only the Stream Guard runs as LocalSystem.
- Block ingress before stopping Sunshine; start Sunshine while blocked; remove the block only after a valid current-boot lease grant and verified service start.
- Any failed, stale, malformed, expired, cross-boot, disconnected, or unverifiable request leaves the gate closed.
- All firewall profiles must be enabled and accept local policy changes before gate installation can succeed.
- Unit and plan tests are software evidence; only the hardware matrix in the spec can qualify effective stream isolation.

## Review Focus

1. Agent process exit, pipe loss, guard restart, and deadline crossing all close ingress and terminate Sunshine.
2. An explicit or replayed grant from an old lease revision, old boot, expired UTC time, or elapsed monotonic deadline never opens the gate.
3. Firewall, SCM, named-pipe ACL, and rollback failures do not leave the machine reported ready or stream-confirmed.
4. Setup handles pre-existing Sunshine allow rules without broadening ingress or silently replacing unmanaged service configuration.
5. Installer plan mode and transaction rollback do not alter services, firewall, data, or pre-existing Sunshine settings.

---

### Task 1: Bind gate authorization to the confirmed lease deadline

**Files:**
- Modify: services/Agent/Leases/LeaseEnforcer.cs
- Modify: services/Agent/Leases/LeaseEnforcementWorker.cs
- Modify: services/Agent.Tests/AgentHostCompositionTests.cs
- Modify: services/Agent.Tests/LeaseEnforcerTests.cs
- Modify: services/Agent.Tests/AgentSyncCycleTests.cs
- Modify: services/Agent.Tests/BootTests.cs

**Interfaces:**
- Consumes: ActiveLeaseConfirmation and the durable LeaseSnapshot already validated by LeaseEnforcer.
- Produces: StreamLeaseGrant(RentalId, Revision, BootId, ExpiresAtUtc, DeadlineUptimeMilliseconds); IStreamGate.AllowAsync(StreamLeaseGrant, CancellationToken).

- [x] Add a test asserting the gate receives the exact confirmed rental, revision, boot ID, UTC expiry, and enforced monotonic deadline.
- [x] Run the focused LeaseEnforcerTests and observe the missing grant contract fail to compile.
- [x] Pass the current durable LeaseSnapshot and effective deadline to AllowAsync only after the exact Controller confirmation.
- [x] Update gate fakes and the closed gate implementation to the new interface.
- [x] Run the full Agent.Tests project; all tests pass and no open-gate test receives a stale lease.
- [x] Commit as fix(agent): bind stream permission to lease deadline.

### Task 2: Implement the verified close/open lifecycle coordinator

**Files:**
- Create: services/Agent/Sunshine/StreamGateCoordinator.cs
- Create: services/Agent.Tests/StreamGateCoordinatorTests.cs
- Modify: services/Agent/Agent.csproj only if a Windows API package is required.

**Interfaces:**
- Consumes: StreamLeaseGrant, IBootClock, TimeProvider, IStreamIngressPolicy, and ISunshineServiceControl.
- Produces: a testable coordinator whose BlockAsync and AllowAsync methods enforce the operation ordering in the spec; its expiry monitor closes at the supplied monotonic deadline.

- [x] Add tests for block-rule-before-service-stop, service-start-before-block-removal, expiry, cross-boot/stale grant rejection, repeated block idempotence, and close-on-open failure; also cover UTC expiry and recovery from out-of-band gate changes.
- [x] Run StreamGateCoordinatorTests and observe the absent coordinator API fail to compile.
- [x] Implement the coordinator with one serialized lifecycle and fail-closed cleanup after each partial failure.
- [x] Run the focused coordinator tests and the full Agent.Tests project.
- [x] Commit as feat(agent): coordinate Sunshine stream fencing.

### Task 3: Add the restricted Agent-to-Guard named-pipe protocol

**Files:**
- Create: services/Agent/Sunshine/StreamGatePipeProtocol.cs
- Create: services/Agent/Sunshine/StreamGuardPipeClient.cs
- Create: services/Agent/Sunshine/StreamGuardPipeServer.cs
- Modify: services/Agent/Program.cs
- Create: services/Agent.Tests/StreamGuardPipeTests.cs

**Interfaces:**
- Consumes: versioned JSON-line Block and Allow requests, StreamLeaseGrant, and an injected gate coordinator.
- Produces: a persistent Agent pipe client implementing IStreamGate and a guard server that permits only SYSTEM and the Agent service SID, rejects remote clients, and blocks immediately on connection loss.

- [x] Add tests for protocol version, payload size, bad JSON, stale grant rejection, authorization ACL, client disconnection, reconnect, and failed acknowledgments.
- [x] Run StreamGuardPipeTests and observe the missing server/client protocol fail to compile.
- [x] Implement the guarded named-pipe server and fail-closed client; run the guard host only when explicitly launched in Stream Guard service mode.
- [x] Run StreamGuardPipeTests and the full Agent.Tests project; verify default Agent composition still reports unavailable rather than ready if the guard is missing.
- [x] Commit as feat(agent): add local stream guard service protocol.

### Task 4: Implement Windows firewall and Sunshine service providers

**Files:**
- Create: services/Agent/Sunshine/WindowsStreamIngressPolicy.cs
- Create: services/Agent/Sunshine/WindowsSunshineServiceControl.cs
- Create: services/Agent.Tests/WindowsStreamAdaptersTests.cs
- Modify: services/Agent/Program.cs

**Interfaces:**
- Consumes: exact Sunshine executable path and service name discovered from the installed Windows service.
- Produces: providers that verify all firewall profiles are enabled, create/validate a program-scoped all-profile block rule, control only SunshineService, and report success only from verified firewall and SCM state.

- [x] Add Windows-only adapter tests for exact rule shape, conflicting rule rejection, profile-policy failure, quoted service path parsing, missing service, start/stop timeout, and wrong service image rejection.
- [x] Run the adapter tests and observe the absent Windows providers fail to compile.
- [x] Implement Windows COM/SCM providers with injectable boundaries and no mutation during construction or plan checks.
- [x] Run all Agent.Tests on Windows and inspect firewall/service state read-only to prove tests created no host changes.
- [x] Commit as feat(agent): add Windows stream gate providers.

### Task 5: Install the guard and default-closed policy transactionally

**Files:**
- Modify: scripts/agent/Install-Service.ps1
- Modify: scripts/agent/Build-AgentPackage.ps1
- Modify: scripts/agent/Test-ServicePlans.ps1
- Modify: scripts/agent/Test-AgentPackageInstallPlan.ps1
- Create: scripts/agent/Prepare-SunshineStreamGate.ps1
- Create: scripts/agent/Test-SunshineStreamGatePlan.ps1
- Modify: docs/agent-operations.md
- Modify: docs/full-rental-roadmap.md
- Modify: docs/sunshine-compatibility.md

**Interfaces:**
- Consumes: the Agent package, the local Sunshine service/executable, and existing service/data installation transaction helpers.
- Produces: a LocalSystem KamenRaider Stream Guard automatic service, the restricted KamenRaider Agent service, an initially enabled closed firewall rule, and a plan mode with no mutations. Failed setup restores the prior Sunshine service startup/dependency values and removes only transaction-owned resources.

- [ ] Add plan and rollback tests for existing Agent/Guard services, missing Sunshine, broad vendor allow rules, firewall disabled by profile or policy, failures at each setup stage, and preservation of unrelated Sunshine settings.
- [ ] Run the new PowerShell plan suite under Windows PowerShell 5.1 and observe the missing setup path fail.
- [ ] Integrate the initial closed policy and Guard service before the Agent starts enrollment; keep existing files/config ACL-protected and maintain the Agent's virtual service account.
- [ ] Run all Agent PowerShell plan/install checks under Windows PowerShell 5.1 and PowerShell 7; verify Plan reports changes without applying them.
- [ ] Commit as feat(agent): install default-closed Sunshine stream guard.

### Task 6: Wire lease, boot, pairing cleanup, and host shutdown to the Guard

**Files:**
- Modify: services/Agent/Program.cs
- Modify: services/Agent/Startup/BootReconciler.cs
- Modify: services/Agent/Startup/UnavailableBootEnvironment.cs
- Modify: services/Agent/Sunshine/PairingSession.cs
- Modify: services/Agent/Sunshine/SunshineClient.cs
- Modify: services/Agent/Sync/LeaseCommandDispatcher.cs
- Modify: services/Agent.Tests/AgentHostCompositionTests.cs
- Modify: services/Agent.Tests/BootTests.cs
- Modify: services/Agent.Tests/SunshineTests.cs
- Modify: services/Agent.Tests/FoundationSunshineClientTests.cs

**Interfaces:**
- Consumes: the named-pipe gate and the installed Foundation Sunshine API.
- Produces: production composition that stays fail-closed until management TLS/credentials, fenced cleanup, and runtime health providers are available; cleanup can start Sunshine only behind a closed firewall, clear and verify clients, and stop it again; pairing API operations do not close the service before making their request.

- [x] Close and verify the stream gate in a Guard startup fence before the pipe/deadline workers start; add a SYSTEM recovery entry point for the SCM failure command. Guard host composition test passes with injected providers.
- [ ] Add tests for stop/clear/restart cleanup ordering, ambiguous PIN handshake cleanup, no old client after boot, unavailable TLS credentials, and exact active-lease-only pairing.
- [ ] Run focused pairing/boot tests and observe the current unavailable production dependencies or incorrect fence ordering.
- [ ] Implement only through the injected maintenance and health interfaces; do not add a fake ready signal or call Restore Rx.
- [ ] Run the full Agent and Controller test suites with isolated artifacts paths, plus all PowerShell plan checks; keep machine readiness false until each provider returns independently verified evidence.
- [ ] Commit as feat(agent): fence Sunshine lifecycle around rentals.

### Task 7: Record qualification limits and final software review

**Files:**
- Modify: docs/agent-acceptance.md
- Modify: docs/agent-operations.md
- Modify: docs/full-rental-roadmap.md
- Modify: docs/sunshine-compatibility.md

- [ ] Record software results and preserve not-tested hardware rows for effective IPv4/IPv6 block, existing connections, Agent/Guard crash, actual Moonlight pairing, expiry, and cold boot.
- [ ] Run the complete solution, Next.js build/lint/type checks, Agent PowerShell checks, `git diff --check`, and a read-only host inventory; report any environmental failure with the exact command.
- [ ] Review the plan changes as a whole and resolve Critical/Important findings before committing the final documentation state.
- [ ] Commit as docs(agent): record stream gate acceptance status.
