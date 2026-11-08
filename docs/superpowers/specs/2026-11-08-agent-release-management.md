# Agent release management and safe updates

## Intent

Give administrators a release dashboard similar to the supplied reference and a safe way to update an installed Windows Agent. The dashboard should show the fleet's installed versions, online and update states, alerts, and stable/beta release history. An administrator can publish a complete Agent package and explicitly prepare an update command for one machine.

The user selected a separate **Update Agent** action with a machine maintenance lock and rollback. The screenshot is visual guidance for status cards, alerts, release history, and release publishing. Text and values under redaction are not part of this specification.

## Goals

- Provide an admin-only `/admin/agent-releases` page with summary counts, actionable alerts, immutable release history, stable/beta channels, release notes, and package publishing.
- Show each machine's current Agent version and update-operation state. Provide a separate per-machine **Update Agent** action that generates a PowerShell command for that machine and chosen release.
- Publish the full `AgentPackage.zip` produced by the existing build flow, with an embedded version/commit/dirty-state manifest. A release build accepts an explicit version and writes outside `public/` so it cannot be served as an unprotected static file. The dashboard version must match the Agent binary. Do not publish a raw `Agent.exe` as an incomplete update package.
- Compute and retain the package SHA-256. Bind the selected package hash and machine identity to a short-lived, one-time update ticket in the Admin-generated command. Serve the package only through the ticketed Controller path.
- Start a maintenance lock atomically only when the machine has no held lease. A maintenance-locked machine is not rentable; `CanRent` and lease creation enforce this in the Controller.
- Preserve the machine identity, private data, existing Agent configuration, service identity, and service dependencies while applying an update.
- Stage new binaries, retain the prior installation for rollback, and restore the old binaries if verification or service startup fails. Keep the lock until Controller receives the target-version heartbeat or a verified rollback-version heartbeat.
- Show awaiting-installer, installing, awaiting-heartbeat, rolling-back, completed, failed, expired, and rolled-back outcomes. Do not update machines automatically or roll out to the whole fleet without an explicit per-machine Admin action.
- Give an Admin a recovery action that can issue a fresh ticket for an already locked failed/interrupted operation without creating a second operation or releasing the lock.

## Non-goals

- Reboot Restore Rx integration or control.
- Publicly exposing Agent packages or adding a general-purpose download endpoint.
- Replacing the existing C#/.NET Windows Service, Sunshine, or TightVNC installer architecture.
- Treating a published package as ready for rental; readiness, approval, and config ACK remain separate gates.
- Adding a new code-signing key lifecycle. The first release management implementation uses the project's existing Admin-authenticated SHA-256 pin in the one-time command; a separate signing-key design would be required to accept detached signatures.

## Approaches

1. Upload only `Agent.exe` and a detached signature. Rejected for this project because the deployed unit also contains Guard/VNC installation scripts and no release-signing key is configured. Updating only the executable could leave an inconsistent installation.
2. Keep one static package in `public/agent` and reuse the enrollment-repair button. Rejected because repair does not replace binaries and cannot show stable/beta history or machine update outcomes.
3. Store immutable full Agent packages, compute and pin their SHA-256, issue machine-bound update tickets, and use an explicit per-machine maintenance-locked command with local rollback. Selected by the user for the update workflow; this preserves the existing installation format while matching the requested release dashboard.

## Architecture

- Controller owns release metadata, ticket state, update operations, and maintenance locks in its SQLite database. The complete immutable package is stored content-addressed in a private directory beside the Controller database; Controller validates the ZIP manifest and archive entries before publishing it.
- The Next.js Admin API authenticates the Admin session and same-origin POST, forwards uploads/commands to Controller, and renders only release metadata. It never exposes the service credential or an unbounded public package path.
- Release Agent package builds include a small manifest containing the informational version, source commit, clean/dirty state, release-build marker, and package format. Controller checks the manifest against the release form and computes SHA-256 while receiving the upload.
- A machine-specific Admin action asks Controller to reserve an update operation. Controller requires a fresh heartbeat (no older than 20 seconds) with a known Agent version different from the target, and in one database transaction rejects a held lease or another active operation, creates a one-time ticket, and sets the maintenance lock. The ticket expires after 10 minutes if the updater has not started; that only releases the pending reservation. After start, the same ticket authorizes that invocation's package download and status reports; start and package-download consumption are persisted, repeated starts and package downloads are rejected. A recovery command replaces the ticket for the same locked operation and resets its one-download allowance.
- The child runs the generated PowerShell command elevated. It checks machine ID, package hash, service paths, private ACLs, Guard fence, and existing config before touching binaries. It stages and swaps the whole Agent publish directory while preserving local config/data and retaining a rollback copy.
- Controller keeps a started operation in `installing` until the updater reports the swap result. Only after the swap and managed-service startup succeed may the updater report `awaitingHeartbeat`; only after the previous files are restored and both managed services restart successfully may it report `rollingBack`. Controller clears the lock only on a subsequent authenticated target-version heartbeat while `awaitingHeartbeat`, or a subsequent previous-version heartbeat while `rollingBack`, then records `completed` or `rolledBack` respectively. A target heartbeat during rollback cannot complete the update. If any stop, restoration, restart, or outcome is uncertain, the updater reports `failed` where possible and the lock remains closed. Admin can recover the same operation by issuing a fresh ticket; this does not create a new lock or release the current one.
- Stable and beta are release labels; publication never initiates fleet updates. An Admin explicitly selects a machine and target release.

## Constraints

- The lease-creation transaction remains the authority for `CanRent`; a separate UI check is not sufficient.
- Update creation and lease allocation serialize on the same Controller database state so exactly one of them can acquire the machine first.
- Updates are per-machine and manual. No background rollout or bulk action is in scope.
- Package bytes are immutable after publication; release versions are unique and the release version must match the embedded Agent informational version. The server computes and enforces package SHA-256.
- Uploaded ZIP size is limited to 100 MiB, expanded size to 500 MiB, and entry count to 5,000; dirty or non-release development packages are rejected for release.
- Package hashes must match before extraction or file replacement. Paths from uploaded ZIP entries must not escape the staging directory.
- The update operation is bound to one machine, one release, one target version, one package hash, and one short-lived ticket. Replayed starts/downloads and machine mismatch are rejected; recovery replaces the prior ticket without creating a new operation.
- An update may start only from a fresh, versioned heartbeat with a different target version so the Controller has a distinct source version to verify on rollback.
- The Agent service has read-only access to installed binaries. Never force a Guard stop or stop Sunshine or other dependent services; an unsuccessful stop leaves the operation failed and locked.
- On uncertain service or rollback state, fail closed: keep the maintenance lock and do not expose the machine as rentable.
- Reboot Restore Rx remains untouched.

## Acceptance

- Admin-only release publishing accepts a valid built Agent package and rejects malformed, mismatched-version, dirty, oversized, hash-altered, and path-traversal archives.
- Release history shows stable/beta labels, versions, notes, SHA-256 prefix, and publish time; artifacts cannot be overwritten by reusing a version.
- An update can be reserved only when no lease is held; concurrent update reservation and lease creation have one winner.
- A maintenance-locked machine reports `canRent=false`, and Controller rejects new lease creation until the lock is resolved.
- An update command that expires before its installer starts releases only its unstarted reservation; an interrupted/failed operation stays locked until target/rollback confirmation or an explicit recovery command is completed.
- Ticket replay, expiration, use on a different machine, and download for a different release are rejected.
- The update script preserves the existing identity/config/data, verifies the package before replacement, and either confirms the new version or restores the old binaries.
- A successful target-version heartbeat resolves the operation; an unverified rollback does not release the lock; a verified rollback does.
- The release dashboard shows accurate counts and operation status and never treats an update as machine approval/readiness.
- Software tests use temporary files and fake services only. Actual Agent/Guard/TightVNC service changes, firewall behavior, and hardware readiness remain separate live acceptance items. No Restore Rx operation is performed.
