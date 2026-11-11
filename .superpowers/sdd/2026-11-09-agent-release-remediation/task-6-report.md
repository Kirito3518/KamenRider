# Task 6 report

Status: **incomplete; production update gate remains closed**.

Work resumed in the existing main checkout at baseline `e7cabcf`. Partial implementation commit: `acf2df1` (`Add fail-closed maintenance fencing seams`). No deployment was performed. No Windows service was stopped or started, no firewall rule was read or changed on the live host, no database was changed, no reboot was performed, and Reboot Restore Rx was not inspected or operated.

## Implemented and checked

- The retained publisher scripts now run under both Windows PowerShell 5.1 and PowerShell 7. They build the two helper fixture assets, verify content-addressed paths and lengths/hashes, and reject missing/tampered manifest or helper assets.
- Agent and Stream Guard consult a protected persistent fence. A missing, corrupt, unknown, ACL-unsafe, reparse-point, or hardlinked marker is treated as held. Delayed lease application/confirmation paths recheck the fence before they can leave streaming open. Ordinary transient block operations do not clear the persistent marker.
- The helper fence stores a protected marker and uses a separate Windows Firewall rule from Stream Guard's managed rule. The adapter checks effective `ActiveStore` profile settings and requires a unique active local rule for the exact Sunshine path, with inbound/block/all profiles and unrestricted filters. Failed release restores a held marker and attempts to re-establish the firewall rule.
- Added a one-operation journal slice with bound operation/controller/identity/target facts, ordered phases, a required backup owner/digest before `backupPublished`, a distinct source version/inventory digest before `sourceBound`, and source-equals-target rejection. The Windows store uses a protected System/Administrators ACL, rejects reparse points/hardlinks, and writes through a flushed temporary file before atomic replacement.
- Added a fakeable service quiescer. Before any stop it requires an injected authorization check and validates the Agent, Guard, and Sunshine service identities, executable paths, accounts, and required dependencies. It stops Agent and Sunshine before Guard, waits for Stopped plus PID 0, rejects remaining matching processes, rechecks the injected held condition, and only then advances `servicesQuiesced`.
- Resolved the fence owner mismatch by making the strict Agent/Guard reader accept either SYSTEM or Builtin Administrators as the owner while requiring the same protected exact ACE set. Added deterministic ACL descriptor tests for both secure owner policies. Added a real helper-store-to-production-reader held→clear roundtrip test that is explicitly elevated-only; this host is not elevated, so that integration test was skipped and is not claimed as passed.
- Added `TrustedIdentity.ReadExisting`, a read-only existing PKCS#8 identity loader with identity ACL, reparse-point, hardlink, size and key validation. It has no create/load-or-create path. Added domain-separated source-evidence signing using the same canonicalizer that Controller verifies.
- Added Controller migration 018 for durable start request ID/body digest and exact source challenge request ID/body digest. Migration 019 is now reserved for Task 9 restore quarantine in the tracked remediation plan; version 17 remains an input that migrates forward. Version-aware proof schema validation covers v15, v16/v17 and v18.
- Added durable idempotent start ACK storage before the Controller returns success. The exact request ID and canonical typed body retry returns the current operation; reusing the ID with a different canonical body conflicts. Start HTTP callers now provide stable request IDs.
- Added signed `/source-challenge` and `/source-binding` routes. Both require canonical request bodies, exact POST path authentication with the enrolled key, a matching held current-head/generation ticket and a fresh scoped nonce. The first-upgrade routes do not use the legacy signed-seconds cutoff; stale client-signed seconds remain authenticated by the enrolled-key signature while replay is rejected by the server-retained scoped nonce. Challenge request ID/body retries are exact within the issuing Controller runtime. Challenges carry persisted issue UTC, monotonic tick, and runtime epoch; monotonic age bounds expiry, wall-clock rollback fails closed, and a restarted Controller rejects unconsumed old-epoch challenges while keeping their rows for audit. Old-epoch rows do not consume the new runtime's active challenge cap. Binding additionally verifies the domain-separated enrollment-key source evidence signature, one-use challenge, distinct source/target versions, same-operation backup ownership, digests, current Controller runtime epoch, and the `backupPublished` phase. A consumed exact binding retry can recover its durable response after restart. Binding provenance is committed atomically and immutable fields are not overwritten. TestServer coverage exercises bad HTTP signatures, bad source-evidence signatures, exact retries, changed-body conflicts, stale signed seconds, nonce replay, challenge runtime restart/rollback, and migration compatibility.
- New start tickets carry an immutable issuing runtime epoch and monotonic issue tick. The first start must occur within the same Controller runtime and within its monotonic lifetime; restart or elapsed lifetime followed by wall-clock rollback fails closed. Once the durable start ACK exists, an exact request retry can recover it after restart.
- Replaced the helper `Program` placeholder with a fixed-verb CLI and a typed request parser. The command line accepts one known operation name and rejects extra arguments, so callers cannot supply an archive/script path. The first local engine slice implements `validate-archive` and `extract`: it reads one protected `%ProgramData%\\KamenRaider.AgentMaintenance\\work\\request.json`, opens only the fixed `target.zip`, verifies request schema, byte length, SHA-256, target version and native runtime through `PackageVerifier`, and extracts only to the fixed fresh `target` directory. Request JSON rejects unknown, duplicate and case-aliased fields. The remaining CLI verbs return an explicit unavailable status; the public wrapper does not yet write this request or launch the helper.
- Fixed the helper archive handle ownership defect found in the early CLI review: the stream now owns its validated native handle after return, and the handle is disposed only if validation or stream construction fails. Added non-elevated Windows file/hardlink regressions and an elevated production-store temp-tree roundtrip test; the latter is explicitly skipped on this non-elevated host.
- Audited every `GetFileInformationByHandle` P/Invoke in AgentMaintenance, Agent, and AgentPackage. AgentMaintenance now shares explicit `Pack=4` FILETIME/DWORD structures; the Agent and PackageVerifier structures use explicit `Pack=4`. Added native layout assertions for the 52-byte Win32 structure and exercised the native handle path with an ordinary file and hardlink rejection.
- Added a backup publisher slice with bounded source inventory, path/reparse/hardlink checks, copy hashing, a protected staging tree, a typed immutable manifest, atomic sibling-directory publication, and Windows file/directory flushes. An injected held-and-quiesced authorization is required both before touching the backup area and before publication. Exact retry verifies the published tree and refuses to repair tampering. The production authorization provider is not implemented, so only fake authorization tests ran; this publisher is not wired to helper CLI orchestration or a source-tree swap.
- Fixed the backup ACL review finding: source checks now reject untrusted file-data, create/delete-child, `WRITE_DAC`, and `WRITE_OWNER` rights; verify owner and ACL on each source file/directory and every existing ancestor through the volume root; and reject reparses before opening source files. The actual production predicate has deterministic descriptor tests for SYSTEM/Administrators owners, read-only Users access, Creator Owner inherited defaults, and dangerous untrusted ACEs. Production Program Files ACL behavior has not been qualified on a deployed Agent machine.
- Added a protocol-1 installed-tree fixture with `Agent.exe`, `Agent.dll`, `.deps.json`, runtime config, version metadata, and protocol marker. `InstalledAgentSourceInspector` requires one active `Agent/<version>` project entry, a matching `Agent.dll` runtime target, expected assembly identity, and matching informational version when present; it returns hashes for `Agent.dll` and `.deps.json`. `MaintenanceBackupOperation` requires journal phase `servicesQuiesced`, rejects source=target, checks that the published immutable backup contains the inspected metadata bytes, and only then advances the journal to `backupPublished`. Exact retry recovers a journal-write failure after backup publication. The fixed `inspect` CLI now exposes this parser through production Program composition, but the inspector does not establish source provenance for user-writable bytes. Backup still needs a production held/quiesced authorization provider and is not wired to CLI or swap.
- Added a fakeable source-binding coordinator. It accepts only the exact `servicesQuiesced` or later recoverable journal head; from `servicesQuiesced` it publishes and journals the immutable backup before requesting a Controller challenge. It verifies the backup on retry, signs canonical source evidence with the existing read-only enrollment identity, persists the exact signed bind intent before sending it, validates the Controller acknowledgement against the same held head/generation and body digest, and advances to `sourceBound` only after the ACK. Request IDs are stable per persisted attempt. Controller returns a typed renewal conflict only for an expired or previous-runtime unconsumed challenge. The journal records each challenge body digest before sending, preserves rejected attempt evidence, and permits a new attempt only after that typed response; other conflicts, transport errors, and altered parameters leave the original signed intent intact. A committed exact bind retry returns its durable response before runtime checks, including after Controller restart. The coordinator is not wired to the CLI, so there is no deployed helper command flow.
- Addressed the scoped source-coordinator re-review: an unconsumed challenge from an earlier Controller runtime now produces the typed renewal response in the bind path, while a bind claiming an epoch different from its current challenge remains a generic conflict. A TestServer regression runs the actual helper coordinator and HTTP client, durably records the bind intent, restarts Controller before commit, then verifies one safe attempt rotation and a successful bind with unchanged source and backup facts. The endpoint exact-retry test now also reconnects after Controller restart to cover a lost committed response.
- Wired the fixed `inspect` CLI verb to the canonical installed Agent source inspector. Its typed result contains only version/protocol metadata and the deps/DLL digests; unsafe ACLs return the bounded `sourceUntrusted` category and malformed metadata returns `sourceUnverifiable`. No source path, ticket, or enrollment key is returned. The current interactive-user-writable installed tree still fails the production ACL check and is not treated as trusted.
- The current host's `C:\Program Files\KamenRaider\Agent` tree is owned by the interactive user and grants that SID FullControl. The source ACL predicate therefore rejects this tree; no ACL repair was made and these bytes are not accepted as trusted update source. A trusted installer must establish protection only while installing independently verified bytes, or maintenance must remain held with recovery instructions.

The firewall policy command, Windows Service Control Manager adapter, helper journal filesystem store, and ACL behavior were not exercised against the live host. The tests use fakes for all firewall and service operations.

## Exact verification results

`dotnet test services/Controller.Tests/Controller.Tests.csproj --configuration Release --filter "FullyQualifiedName~MaintenanceFirewallPolicyTests|FullyQualifiedName~MaintenanceJournalTests|FullyQualifiedName~MaintenanceServiceQuiescerTests|FullyQualifiedName~AgentMaintenanceContractTests" --no-restore`

Passed: 20, failed: 0.

`dotnet test services/Agent.Tests/Agent.Tests.csproj --configuration Release --filter "FullyQualifiedName~MaintenanceFenceReaderTests|FullyQualifiedName~WindowsMaintenanceFenceSecurityTests|FullyQualifiedName~Maintenance" --no-restore`

Passed: 18, failed: 0.

`dotnet test services/Agent.Tests/Agent.Tests.csproj --configuration Release --filter "FullyQualifiedName~WindowsMaintenanceFenceSecurityTests|FullyQualifiedName~MaintenanceFenceStoreRoundTripTests" --no-restore`

Passed: 6, skipped: 1, failed: 0. The skipped elevated Windows writer→production-reader roundtrip did not execute on this non-elevated host; secure owner ACL policy tests for SYSTEM and Administrators did execute.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --configuration Release --filter "FullyQualifiedName~AgentUpdateTests|FullyQualifiedName~AgentReleaseAcceptanceTests|FullyQualifiedName~AgentUpdateMigrationTests|FullyQualifiedName~AgentMaintenanceIdentityTests" --no-restore`

Passed: 83, failed: 0. This includes signed stale-seconds source challenge/binding requests, nonce replay rejection, source challenge rollback/restart behavior, durable start ACK retry after restart, and v17→v18 migration checks.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --configuration Release --filter "FullyQualifiedName~AgentMaintenanceCliTests" --no-restore`

Passed: 13, skipped: 1, failed: 0. CLI tests cover exact argument shape, typed request parsing, pinned archive length/hash/runtime, bounded inventory and extraction into the fixed fresh tree. The elevated production work-store roundtrip was skipped because this process is not elevated.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --no-restore -p:BaseOutputPath=C:\Users\User\AppData\Local\Temp\kr-task6-build-20261111\bin\ --filter FullyQualifiedName~MaintenanceBackupPublisherTests --logger "console;verbosity=minimal"`

Passed: 9, failed: 0. These use a temporary-tree security fake and fake held/quiesced authorization; they do not qualify production Windows ACL or service/fence authorization behavior.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --no-restore -p:BaseOutputPath=C:\Users\User\AppData\Local\Temp\kr-task6-build-20261111\bin\ --filter "FullyQualifiedName~AgentMaintenanceCliTests|FullyQualifiedName~MaintenanceBackupPublisherTests|FullyQualifiedName~AgentUpdateTests|FullyQualifiedName~AgentReleaseAcceptanceTests|FullyQualifiedName~AgentUpdateMigrationTests|FullyQualifiedName~AgentMaintenanceIdentityTests" --logger "console;verbosity=minimal"`

Passed: 105, skipped: 1, failed: 0. The skipped test is the elevated production work-store integration.

`dotnet test services/Agent.Tests/Agent.Tests.csproj --no-restore -p:BaseOutputPath=C:\Users\User\AppData\Local\Temp\kr-task6-build-20261111\bin\ --filter "FullyQualifiedName~WindowsMaintenanceFenceSecurityTests|FullyQualifiedName~MaintenanceFenceStoreRoundTripTests" --logger "console;verbosity=minimal"`

Passed: 6, skipped: 1, failed: 0. The elevated production fence writer→reader roundtrip was skipped; deterministic ACL policy tests ran.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --no-restore -p:BaseOutputPath=C:\Users\User\AppData\Local\Temp\kr-task6-build-20261111\bin\ --filter FullyQualifiedName~ExtractedTreeRejectsMissingDirectoryAndHardlinkedFile --logger "console;verbosity=minimal"`

Passed: 1, failed: 0. This exercises PackageVerifier's native handle metadata path against an ordinary extracted tree and a hardlinked file.

The first focused Controller test build using the repository's normal `bin` path did not reach test execution: live `Controller.exe` PID 74944 held `services/Controller/bin/Debug/net10.0/Contracts.dll`. I left that process untouched and reran the tests with the isolated temporary `BaseOutputPath` shown above.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --no-restore -p:BaseOutputPath=C:\Users\User\AppData\Local\Temp\kr-task6-build-20261111\bin\ --filter "FullyQualifiedName~InstalledAgentSourceInspectorTests|FullyQualifiedName~ProductionSourceAclPredicate|FullyQualifiedName~MaintenanceBackupPublisherTests|FullyQualifiedName~AgentMaintenanceCliTests" --logger "console;verbosity=minimal"`

Passed: 33, skipped: 1, failed: 0. Coverage includes source version ambiguity/mismatch, source mutation across inspection/copy, quiesced journal precondition, backup publication retry after a journal write failure, source ACL mutation rights including `WRITE_DAC`/`WRITE_OWNER`, current-user ownership/write rejection, the current Windows volume-root ACL predicate, archive handle ownership, native Win32 layout, and hardlink rejection. The elevated production work-store roundtrip remained skipped.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --configuration Release --no-restore -p:BaseOutputPath=C:\Users\User\AppData\Local\Temp\kr-task6-build-20261111\bin\ --filter "FullyQualifiedName~InstalledAgentSourceInspectorTests" --logger "console;verbosity=minimal"`

Passed: 10, skipped: 0, failed: 0. Includes the source-binding coordinator's backup publication from `servicesQuiesced`, canonical enrollment-key signature, exact intent persistence, held-head acknowledgement validation, and exact request retry after a simulated journal write failure following Controller ACK.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --configuration Release --no-restore -p:BaseOutputPath=C:\Users\User\AppData\Local\Temp\kr-task6-build-20261111\bin\ --filter "FullyQualifiedName~InstalledAgentSourceInspectorTests|FullyQualifiedName~AgentUpdateTests.SourceChallengeExpiresMonotonicallyAndOldRuntimeRowsDoNotConsumeNewRuntimeLimit|FullyQualifiedName~AgentUpdateTests.SourceChallengeAndBindingAreSignedBoundAndExactRetryIsIdempotent|FullyQualifiedName~AgentReleaseAcceptanceTests.ExpiredUnconsumedSourceChallengeReturnsTypedRenewalSignal" --logger "console;verbosity=minimal"`

Passed: 17, skipped: 0, failed: 0. Covers challenge expiry/runtime restart before commit, exact committed bind retry after Controller reopen (lost-response recovery), typed HTTP renewal classification, safe challenge/bind attempt rotation, immutable source and backup facts, untyped conflict and altered-parameter non-rotation, and exact request replay after a local journal write failure.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --configuration Release --filter "FullyQualifiedName~AgentMaintenanceCliTests|FullyQualifiedName~AgentUpdateTests|FullyQualifiedName~AgentReleaseAcceptanceTests|FullyQualifiedName~AgentUpdateMigrationTests|FullyQualifiedName~AgentMaintenanceIdentityTests" --no-restore`

Passed: 93, failed: 0.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --configuration Release --no-restore -p:BaseOutputPath=C:\Users\User\AppData\Local\Temp\kr-task6-build-20261111\bin\ --filter "FullyQualifiedName~InstalledAgentSourceInspectorTests|FullyQualifiedName~AgentMaintenanceCliTests|FullyQualifiedName~AgentUpdateTests.SourceChallengeExpiresMonotonicallyAndOldRuntimeRowsDoNotConsumeNewRuntimeLimit|FullyQualifiedName~AgentUpdateTests.SourceChallengeAndBindingAreSignedBoundAndExactRetryIsIdempotent|FullyQualifiedName~AgentReleaseAcceptanceTests.SourceChallengeAndBindingRequireTheEnrolledKeyAndSupportExactRetry|FullyQualifiedName~AgentReleaseAcceptanceTests.ExpiredUnconsumedSourceChallengeReturnsTypedRenewalSignal|FullyQualifiedName~AgentReleaseAcceptanceTests.CurrentRuntimeMismatchIsGenericConflictAndDoesNotConsumeChallenge|FullyQualifiedName~AgentReleaseAcceptanceTests.PersistedBindIntentRotatesAfterControllerRestartBeforeCommit" --logger "console;verbosity=minimal"`

Passed: 35, skipped: 1, failed: 0. Includes read-only `inspect` command metadata/rejection tests, source challenge expiry and exact retry cases, and a real TestServer restart before a persisted bind intent is submitted. The actual helper coordinator/client rotate to a new attempt, preserve the same source/backup evidence, and bind successfully. A current challenge with a mismatched requested runtime returns an untyped conflict and remains consumable by the exact valid bind. The exact committed retry also recovers after Controller restart. The single skipped test is the elevated production work-store ACL integration.

`dotnet test services/Controller.Tests/Controller.Tests.csproj --configuration Release --no-restore -p:BaseOutputPath=C:\Users\User\AppData\Local\Temp\kr-task6-build-20261111\bin\ --filter "FullyQualifiedName~InstalledAgentSourceInspectorTests|FullyQualifiedName~AgentMaintenanceCliTests" --logger "console;verbosity=minimal"`

Passed: 29, skipped: 1, failed: 0. Covers source inspection output, safe untrusted-source rejection category, and fixed archive CLI behavior. The elevated production work-store ACL integration was skipped.

`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/agent/Test-AgentUpdatePublicWorkflow.ps1`

Passed under Windows PowerShell 5.1 with fake HTTP, fake services and temporary files.

`pwsh.exe -NoProfile -File scripts/agent/Test-AgentUpdatePublicWorkflow.ps1`

Passed under PowerShell 7 with fake HTTP, fake services and temporary files.

The public updater fake workflow was rerun under both Windows PowerShell 5.1 and PowerShell 7 after the source/runtime changes; both runs passed.

`& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoLogo -NoProfile -ExecutionPolicy Bypass -File scripts\agent\Test-AgentMaintenancePublish.ps1 -RepositoryRoot (Get-Location).Path`

Passed under Windows PowerShell `5.1.22621.6133`.

`./scripts/agent/Test-AgentMaintenancePublish.ps1 -RepositoryRoot (Get-Location).Path`

Passed under PowerShell `7.6.9`.

`dotnet publish services/AgentMaintenance/AgentMaintenance.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false`

Passed; emitted the self-contained `win-x64` helper under `services/AgentMaintenance/bin/Release/net10.0/win-x64/publish`.

`dotnet publish services/AgentMaintenance/AgentMaintenance.csproj --configuration Release --runtime win-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false`

Passed; emitted the self-contained `win-arm64` helper under `services/AgentMaintenance/bin/Release/net10.0/win-arm64/publish`.

`$repo = (Get-Location).Path; $output = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'kr-maintenance-real-publish-20261111'); & ./scripts/agent/Publish-AgentMaintenance.ps1 -RepositoryRoot $repo -OutputRoot $output`

Passed under PowerShell 7.6.9 against real x64/arm64 helper publish outputs; wrote a content-addressed manifest under a temporary output directory. This was a local artifact check and was not deployed.

`git diff --cached --check`

Passed before commit `acf2df1`; Git printed line-ending conversion notices for existing working files, with no whitespace errors.

## Production impact of the missing-marker rule

The new Agent/Guard deliberately treats a missing or invalid fence as held. Therefore an installation that starts these new binaries without a verified marker will fail closed and cannot stream. Fresh-install initialization and a safe legacy-upgrade migration have not been implemented or wired into trusted installers. This can leave fresh installs and older installations unavailable until trusted initialization or a complete maintenance operation publishes a verified clear/terminal marker. Do not weaken the reader to treat absence as clear.

For an upgrade, a clear marker must not be minted until a trusted path has proved no held current or pending lease and no conflicting Controller update hold. The old Agent cannot provide the new exact current/pending snapshot, and that migration proof is not available yet. The production update gate stays closed while this is unresolved.

## Remaining blockers

- `services/AgentMaintenance/Program.cs` dispatches fixed CLI verbs; `inspect`, `validate-archive`, and `extract` are implemented. There is no native architecture selection for the bootstrap or end-to-end orchestration. The read-only identity component is still not consumed by a helper command.
- There is no trusted installer initializer for a fresh install and no proven legacy upgrade migration. No safe clear marker is currently provisioned by the installer.
- `IMaintenanceStopPrerequisites` has only a test fake. No production implementation verifies the installation lock, current plus pending Agent lease state, Controller held allocations, independently effective fence, and target architecture continuously before and through stop. Controller's durable start ACK exists, but it is not wired into helper orchestration.
- The firewall ActiveStore probe is source code only and was not run. Its profile/rule checks are not yet qualified as a complete positive packet-enforcement proof for supported Windows/GPO configurations.
- The journal currently supports only one operation entry. Multi-operation recovery chains, `legacyImported`, intent-before-move integration, tree digest reconciliation after each crash boundary, report retries, and held-preserving recovery are not implemented. Cross-process journal locking is also not implemented, so concurrent helpers remain blocked from production use.
- The local archive engine implements only `validate-archive` and `extract`; `inspect` is wired as a read-only command. Helper CLI operations `start`, `bind-source`, `backup`, `apply`, `report`, and `reconcile` remain unavailable. The request work store's production ACL/hardlink behavior has not had an elevated real writer/reader integration test. The helper signing API and source-binding coordinator are not integrated into a running CLI flow.
- No trusted installer initializer provisions a safe clear fence for fresh installs or proven legacy upgrades. Missing/corrupt still fails closed, so starting new Agent/Guard binaries without a verified marker leaves streaming unavailable.
- The backup publisher and journal-aware backup operation create and verify a protected backup tree in isolation, but no production held/quiesced authorization is wired. The tested source-binding coordinator composes these seams in-process, but the helper does not call it; no protected source-tree swap, crash reconciliation, or outcome report orchestration exists. The Controller accepts source digests only after the separate enrollment-key signature; the coordinator computes those digests locally, but there is no trusted CLI path yet. The inspected installed tree is user-writable and remains held; no in-place ACL repair is authorized as proof of byte provenance.
- The public wrapper/ZIP-script execution path is not replaced. The publisher manifest is not consumed by the deployed wrapper or a trusted command route. Package/install wiring for the helper assets is missing.
- No full Task 6 acceptance suite or independent review of the full change has passed. Commits `acf2df1`, `0c02db9`, `51c38b5`, `3d2791b`, `fe6a1ae`, `95d6f1d`, `5ba4553`, and `52762ed` are partial implementation only, not Task 6 completion.

Controller start/source challenge rows created before migration 018 have no issuing runtime epoch. They remain auditable but cannot be used to begin a new update under the new first-start rules. Existing unstarted tickets therefore fail closed after this migration; rollout requires a reviewed migration or replacement-ticket path before production deployment.
