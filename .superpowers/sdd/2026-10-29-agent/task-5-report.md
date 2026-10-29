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
