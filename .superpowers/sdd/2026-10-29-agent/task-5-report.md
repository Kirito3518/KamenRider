# Task 5 report

Implemented Agent-local lease persistence and deadline enforcement, Windows boot-scoped clock, power adapter, sync ApplyLease dispatch/results, and matching Controller confirmation permission. Commit subject: `feat: enforce rental expiry on windows agents`.

## Verification

- Wrote eight initial cases with throwing production seams, then watched all eight fail with NotImplementedException (initial seam compilation emitted unused-parameter warnings).
- Implemented persistence, enforcement and confirmation. Seven passed; the duplicate/restart fixture originally retried at its exact 30-second command TTL boundary. Corrected that fixture to a valid 120-second TTL; all eight passed. TTL validation was not weakened.
- Added sync-dispatch coverage with a throwing dispatcher seam, watched its runtime NotImplementedException failure, then implemented dispatch. A temporary test-source brace placement compilation error was corrected before the red runtime run. Added corrupt-file regression coverage, which already passed against the fail-closed storage handling.
- Added initial local clock rollback regression against sync; observed expected shutdown count 1 / actual 0. Added Controller-time observation anchored to uptime; regression passed.
- Final targeted command: `dotnet test services/Agent.Tests --filter FullyQualifiedName~LeaseEnforcerTests`: 11 passed, 0 failed.
- Full command: `dotnet test services/KamenRaider.slnx`: Agent 16 + Controller 64 = 80 passed, 0 failed, no warnings.
- All shutdown tests inject fake IShutdown. No WindowsShutdown instance was invoked, no live services were started, and this computer was never shut down. Native boot clock and real shutdown adapter remain unexercised against the live OS.

## Persistence and clock invariants

- LeaseStore writes a unique same-directory temporary JSON file with WriteThrough, flushes to disk, then atomically replaces lease.json. Save succeeds before an applied result exists. Write/read/JSON failure closes permission and exposes LeaseStorageFailed; persistence failure never returns an applied result.
- Snapshot includes rentalId, revision, expiresAtUtc, bootId, lastCommandId and absolute deadlineUptimeMilliseconds. This last value is a Windows uptime deadline, never a process-local Stopwatch timestamp.
- WindowsBootClock reads the native Windows BootIdentifier through NtQuerySystemInformation(SystemBootEnvironmentInformation = 90). An unavailable/invalid boot identifier prevents startup rather than inventing an identity. GetTickCount64 is called explicitly through Win32: its elapsed time includes sleep/hibernation. This avoids changed .NET Environment.TickCount64 semantics.
- Deadline survives Agent process restart in the same OS boot. New boot with a persisted lease exposes ReconciliationRequired, keeps permission closed and rejects ApplyLease until Task 7 supplies explicit reconciliation; it does not silently rebind or extend old state.
- Sync observes Controller ServerTimeUtc as an uptime-advancing floor. Local wall time and the persisted lease's expiry/uptime projection can only make validation more conservative. Wall rollback, duplicate delivery, reconnect, and a process restart cannot reset the current deadline. Extensions derive a new deadline from the effective time floor and require the same rental and exact next revision, with a later expiry, before the current deadline has elapsed.
- Commands require ApplyLease type, current boot, positive revision/rental ID, future TTL, valid UTC payload, and unexpired current lease. Replaying the last command returns applied only for matching rental/revision/expiry and never saves a fresh deadline. Older revisions and other-rental commands fail.

## Sync, gate and shutdown

- SyncWorker now reuses the OS boot identity and includes dispatcher PendingResults in signed heartbeat requests. Successful response processes ApplyLease commands and queues exact command ID/revision applied/failed results; failed HTTP sync retains queued results. On process restart, Controller command replay reconstructs an applied result from durable lease state.
- The existing CommandResult protocol carries command ID/revision; Controller's durable command binding supplies rental/boot correlation. No wire-contract changes were needed.
- Applied/ACKed is not streaming permission. LeaseEnforcer.CanStream becomes true only after matching rental, revision, boot and exact expiry in ActiveLease confirmation, while the local monotonic deadline remains valid. Extension application closes permission until its revision is confirmed. Null or mismatched confirmations close permission.
- IStreamGate remains an abstraction. ClosedStreamGate is the intentionally closed placeholder; it performs no Sunshine/firewall operations. Task 6 must replace it and consume CanStream when deciding whether to open the actual gate. PrepareMachine/readiness remains Task 6; SyncWorker still reports unknown readiness, so this task cannot accidentally activate streaming.
- LeaseEnforcementWorker polls independently of network sync every 250 ms. At/after the deadline it requests power immediately, then blocks the gate; it does not wait for network cleanup. There is no warning or grace period. A successful request is recorded as ShutdownRequested (not powered-off) and is not repeated within that process. A failure records ShutdownFailed, remains closed, and retries on later checks.
- WindowsShutdown launches the SystemDirectory shutdown.exe with `/s /f /t 0`, no shell/window, and checks the process exit code. The adapter itself was never executed in tests.

## Integration limits and operational notes

- OS-native boot identity access is a deliberate fail-closed Windows dependency. Live Windows restart/hibernate/fast-startup qualification is still necessary before deployment; unsupported access fails startup. Microsoft documents GetTickCount64 sleep inclusion at https://learn.microsoft.com/en-us/windows/win32/sysinfo/windows-time and cautions that NtQuerySystemInformation is an internal API at https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation.
- The live Task 6 gate adapter must keep blocking operations bounded; the enforcer serializes local state changes and gate operations with a semaphore. It must not introduce network waits under that operation.
- ClosedStreamGate does not itself control a running Sunshine instance. Installation/Task 6 must ensure an actually closed stream surface before enabling service use. No claim of operational streaming protection is made by this placeholder.
- Task 7 owns cross-boot reconciliation and release. This task conservatively retains persisted state and does not recycle rentals or allocations.
- Native adapters and host dependency registration were reviewed and built; they were not exercised destructively. Core and dispatcher behavior were verified with deterministic fake clocks, fake gate/power boundaries, and real temporary persisted files.

## Task 5 review fixes (supersedes earlier semaphore and clock details)

Addressed both P1 review findings with behavioral regressions before changes.

- `GateStallCannotDelayOldExpiryOrReviveExtension` and `PersistenceStallCannotDelayExpiryAndLateWriteCannotReviveOnRestart` originally timed out because EnforceAsync waited behind command I/O. Initial red-run cleanup raced temporary-file disposal; amended the test cleanup to release and await the blocked write, then observed the intended TimeoutException failures. Both now complete enforcement while their gate/save remains blocked, issue fake shutdown once, reject the delayed extension, and keep revision 1 in runtime state. The persistence test also restarts from the late durable write and proves it cannot revive the lease.
- `SyncElapsedIncludesTransitAndConfigurationWrite` originally expected fake shutdown count 1 and observed 0. The fake local clock is one day behind, with 20 seconds elapsed between request start and dispatch. The fixed deadline expires after another 40 seconds, rather than another 60.
- Further boundary checks `FirstLeasePersistenceStallStillArmsExpiry` (expected 1, actual 0), `StartupGateStallCannotPreventPersistedExpiry` (TimeoutException), and `FirstLeaseGateStallStillArmsExpiry` (expected 1, actual 0) were each observed red and fixed. Validated initial commands arm a provisional deadline before awaiting gate/persistence; that state never permits streaming or yields an applied ACK before durable save.
- `ConfirmedExtensionRetainsNewDeadlineAcrossRestart` verifies durable finalization of a matching Controller-confirmed extension, including shutdown only at the extended deadline. It passed against the recovery-guard implementation and protects that requirement.

Enforcement now uses a short synchronous state lock for decisions and a separate command semaphore for mutation serialization. It never takes the command semaphore to enforce a known deadline. Neither gate blocking nor lease save is awaited on the known-expiry path. Shutdown starts before a separately observed asynchronous gate-block operation. An atomic shutdown-state guard prevents concurrent requests and retains retry after failures. Initial recovery loading skips awaiting the stream gate before checking the loaded deadline.

Application snapshots the old lease, computes the candidate deadline, and clears streaming permission before any external await. It rechecks boot, fault, old deadline, new deadline, command TTL and unchanged current snapshot after gate completion and again after durable save. A stalled extension can never replace a lease whose old deadline has elapsed; the expiry worker can request shutdown throughout that stall.

The persisted snapshot now includes optional RecoveryDeadlineUptimeMilliseconds. An extension is written with its preceding recovery deadline so that even a save completing after shutdown cannot leave a durable artifact that resurrects the rental after restart. Runtime publication still happens only after successful persistence and deadline rechecks. A matching Controller ActiveLease confirmation proves the extension's ACK was accepted; confirmation durably removes the conservative recovery guard before allowing streaming. A restart before this finalization uses the earlier recovery deadline and may require reconciliation; a confirmed and finalized extension retains its proper new deadline across restart. This conservative behavior intentionally prefers a closed stream over reviving an uncertain late write.

SyncWorker captures Windows uptime immediately before SyncAsync. It passes that request-start sample to LeaseCommandDispatcher after configuration application. Controller ServerTimeUtc is advanced by the **entire elapsed uptime from request start through dispatch**, including HTTP response transit and configuration persistence. Full request elapsed can overestimate transit since the server timestamp may be generated later in the request; that conservatively shortens validity. Subsequent waiting in command processing advances the same uptime-anchored effective clock. Receipt-time-only convenience overloads remain for immediate in-process samples/tests; the actual network worker always supplies the request-start sample. No wire-contract changes were introduced.

Final review-fix verification:

- `dotnet test services/Agent.Tests --filter FullyQualifiedName~LeaseEnforcerTests`: 18 passed, 0 failed.
- `dotnet test services/KamenRaider.slnx`: Agent 23 + Controller 64 = 87 passed, 0 failed, no build/test warnings.
- All power actions remain fake-only. No live services or real shutdown adapter were executed.
- Earlier note that the Task 6 gate must be bounded to avoid delaying shutdown is superseded: known-deadline power enforcement is now independent of gate completion. Task 6 should still bound its own operations for resource/liveness hygiene.

## Task 5 second review fixes (recovery confirmation and gate task coalescing)

- P2 recovery regression `RestartConfirmationBeforeGuardRestoresProposedExtensionDeadline` was written first and observed red: fake shutdown expected 0 at the old deadline, actual 1. Loading no longer overwrites the proposed monotonic deadline with the recovery guard. The runtime keeps the loaded guard separately, enforcing the minimum of guard and proposed deadline while recovering. Effective UTC projection retains the original expiry/deadline relationship instead of incorrectly pairing an extended UTC expiry with an earlier uptime deadline.
- A matching Controller confirmation must be evaluated strictly before the recovery guard expires. Under the state lock that confirmation removes the runtime guard; the Controller has now confirmed the extended revision, so pending finalization I/O cannot cause an early shutdown at the old guard. The unguarded proposed deadline is durably saved before stream permission is granted. If finalization is interrupted, disk still retains the conservative guard. The new `RestartConfirmationAtGuardExpiryCannotRestoreExtension` boundary test verifies that confirmation exactly at the guard cannot revive the extension and shutdown still occurs.
- P2 resource regression `RepeatedExpiryPollsCoalesceStalledGateBlock` was written first and observed red: expected 2 total gate calls including initial application, actual 101 after 100 expiry polls. Expiry gate blocking now uses an atomic in-flight guard, reset in finally when the operation completes or fails. Polling cannot create additional work while the adapter is stalled, and later polls can retry after completion/failure. Power enforcement remains independent of gate completion.
- Targeted `dotnet test services/Agent.Tests --filter FullyQualifiedName~LeaseEnforcerTests`: 21 passed, 0 failed.
- Full `dotnet test services/KamenRaider.slnx`: Agent 26 + Controller 64 = 90 passed, 0 failed, no build/test warnings.
- Power remained fake-only throughout. No live service or Windows shutdown call was executed.
