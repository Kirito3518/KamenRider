# Task 6 report

Status: **incomplete; production update gate remains closed**.

Work resumed in the existing main checkout at baseline `e7cabcf`. Partial implementation commit: `acf2df1` (`Add fail-closed maintenance fencing seams`). No deployment was performed. No Windows service was stopped or started, no firewall rule was read or changed on the live host, no database was changed, no reboot was performed, and Reboot Restore Rx was not inspected or operated.

## Implemented and checked

- The retained publisher scripts now run under both Windows PowerShell 5.1 and PowerShell 7. They build the two helper fixture assets, verify content-addressed paths and lengths/hashes, and reject missing/tampered manifest or helper assets.
- Agent and Stream Guard consult a protected persistent fence. A missing, corrupt, unknown, ACL-unsafe, reparse-point, or hardlinked marker is treated as held. Delayed lease application/confirmation paths recheck the fence before they can leave streaming open. Ordinary transient block operations do not clear the persistent marker.
- The helper fence stores a protected marker and uses a separate Windows Firewall rule from Stream Guard's managed rule. The adapter checks effective `ActiveStore` profile settings and requires a unique active local rule for the exact Sunshine path, with inbound/block/all profiles and unrestricted filters. Failed release restores a held marker and attempts to re-establish the firewall rule.
- Added a one-operation journal slice with bound operation/controller/identity/target facts, ordered phases, a required backup owner/digest before `backupPublished`, a distinct source version/inventory digest before `sourceBound`, and source-equals-target rejection. The Windows store uses a protected System/Administrators ACL, rejects reparse points/hardlinks, and writes through a flushed temporary file before atomic replacement.
- Added a fakeable service quiescer. Before any stop it requires an injected authorization check and validates the Agent, Guard, and Sunshine service identities, executable paths, accounts, and required dependencies. It stops Agent and Sunshine before Guard, waits for Stopped plus PID 0, rejects remaining matching processes, rechecks the injected held condition, and only then advances `servicesQuiesced`.

The firewall policy command, Windows Service Control Manager adapter, helper journal filesystem store, and ACL behavior were not exercised against the live host. The tests use fakes for all firewall and service operations.

## Exact verification results

`dotnet test services/Controller.Tests/Controller.Tests.csproj --configuration Release --filter "FullyQualifiedName~MaintenanceFirewallPolicyTests|FullyQualifiedName~MaintenanceJournalTests|FullyQualifiedName~MaintenanceServiceQuiescerTests|FullyQualifiedName~AgentMaintenanceContractTests" --no-restore`

Passed: 20, failed: 0.

`dotnet test services/Agent.Tests/Agent.Tests.csproj --configuration Release --filter "FullyQualifiedName~MaintenanceFenceReaderTests|FullyQualifiedName~WindowsMaintenanceFenceSecurityTests|FullyQualifiedName~Maintenance" --no-restore`

Passed: 18, failed: 0.

`& 'C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe' -NoLogo -NoProfile -ExecutionPolicy Bypass -File scripts\agent\Test-AgentMaintenancePublish.ps1 -RepositoryRoot (Get-Location).Path`

Passed under Windows PowerShell `5.1.22621.6133`.

`./scripts/agent/Test-AgentMaintenancePublish.ps1 -RepositoryRoot (Get-Location).Path`

Passed under PowerShell `7.6.9`.

`git diff --cached --check`

Passed before commit `acf2df1`; Git printed line-ending conversion notices for existing working files, with no whitespace errors.

## Production impact of the missing-marker rule

The new Agent/Guard deliberately treats a missing or invalid fence as held. Therefore an installation that starts these new binaries without a verified marker will fail closed and cannot stream. Fresh-install initialization and a safe legacy-upgrade migration have not been implemented or wired into trusted installers. This can leave fresh installs and older installations unavailable until trusted initialization or a complete maintenance operation publishes a verified clear/terminal marker. Do not weaken the reader to treat absence as clear.

For an upgrade, a clear marker must not be minted until a trusted path has proved no held current or pending lease and no conflicting Controller update hold. The old Agent cannot provide the new exact current/pending snapshot, and that migration proof is not available yet. The production update gate stays closed while this is unresolved.

## Remaining blockers

- `services/AgentMaintenance/Program.cs` is still a placeholder. There is no fixed typed CLI request protocol, native architecture selection, trusted identity reader, read-only PKCS#8 loading, or end-to-end orchestration.
- There is no trusted installer initializer for a fresh install and no proven legacy upgrade migration. No safe clear marker is currently provisioned by the installer.
- `IMaintenanceStopPrerequisites` has only a test fake. No production implementation verifies the installation lock, durable idempotent Controller start ACK, current plus pending Agent lease state, Controller held allocations, independently effective fence, and target architecture continuously before and through stop.
- The firewall ActiveStore probe is source code only and was not run. Its profile/rule checks are not yet qualified as a complete positive packet-enforcement proof for supported Windows/GPO configurations.
- The journal currently supports only one operation entry. Multi-operation recovery chains, `legacyImported`, intent-before-move integration, tree digest reconciliation after each crash boundary, source/backup binding retries, report retries, and held-preserving recovery are not implemented.
- `TrustedIdentity`, the local update engine, helper CLI operations (`inspect`, `validate-archive`, `extract`, `start`, `bind-source`, `apply`, `report`, `reconcile`), and the legacy installed Agent fixture are absent.
- Controller start idempotency/request ID and source-challenge/source-binding endpoints and transactions are absent. The existing challenge rows still lack a durable canonical request digest; same-ID/same-body retry versus same-ID/different-body rejection is not enforced. The helper still has no read-only PKCS#8 source-signing path, and the legacy deps parser seam remains unresolved.
- The public wrapper/ZIP-script execution path is not replaced. The publisher manifest is not consumed by the deployed wrapper or a trusted command route. Package/install wiring for the helper assets is missing.
- No full Task 6 acceptance suite or independent review of the full change has passed. Commit `acf2df1` is a checkpoint, not Task 6 completion.
