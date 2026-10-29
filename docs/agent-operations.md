# Agent operations and Restore Rx qualification

## Deployment status

This repository is **not operationally ready for rentals**. Task 6 has no qualified default-closed stream gate or desktop health provider. `BootReconciler` is a testable coordinator, not registered in the Agent host. No production `IBootEnvironment`, signed reconciliation HTTP client, or durable cross-boot `LeaseEnforcer` import exists. Sync currently continues reporting unknown readiness. Install-Service intentionally creates the Agent disabled. Do not enable it or expose Sunshine based on a green unit test, a successful HTTP call, or service recovery.

## Installation preparation

Publish without installing: `./scripts/agent/Publish-Services.ps1 -Role Both -OutputDirectory C:\KamenRaiderPublish -Plan`. Remove `-Plan` to publish self-contained Windows binaries into separate Controller/Agent directories. This does not start them.

Use a protected executable directory (normally under Program Files); standard users and tenants must have no write access. Provision role-specific config using absolute data paths, then inspect `Install-Service.ps1 -Plan` with Role, PublishDirectory, DataDirectory, ConfigurationFile and TrustedRootThumbprint. Install is an explicit elevated operator action. The script uses a distinct virtual service account (`NT SERVICE\KamenRaider Controller` or `NT SERVICE\KamenRaider Agent`), resets the data/config ACL to that account, SYSTEM and Administrators, configures recovery at 5/15/60 seconds and starts no service. Controller is set to automatic; Agent remains disabled pending qualification. Do not share an existing data directory with unrelated applications: ACL reset is intentional. Review partial-install failures before retrying; the service initially stays disabled.

Provision certificate trust through the organization's approved process. Verify the expected root fingerprint out of band and install the root in LocalMachine Root; the installer only checks its presence, never imports an untrusted certificate. Provision Controller HTTPS server certificate/private-key ACL for its account, verify hostname/SAN and chain, and verify Agent validation against that chain. Do not bypass validation. Controller internal HTTP listener stays loopback on a different port. Provision its service credential through an ACL-protected config or approved secret store; never put real credentials into Git or command arguments. Sunshine management credentials and trusted localhost certificate still need the Task 6 service-only loader; this installer does not supply one. No firewall changes are performed.

Example Agent configuration (no secrets):

```json
{"ControllerUrl":"https://controller.example.invalid:7443","DataDirectory":"C:\\ProgramData\\KamenRaider\\Agent","MoonlightPort":47989,"SunshinePort":47990,"NetworkAdapterId":"REPLACE_WITH_QUALIFIED_ADAPTER_ID"}
```

Controller config needs InternalUrl (`http://127.0.0.1:7442`), AgentUrl (`https://controller.example.invalid:7443`), absolute DatabasePath and a separately provisioned ServiceCredential. Populate certificate binding per deployment. Never copy Controller config to Agent or embed tenant credentials/pairings in either image.

## Per-machine baseline

1. Qualify the installed Restore Rx edition/version using its vendor-supported workflow. Restore-on-every-boot behavior is a deployment requirement; no Restore Rx API or command is implemented here.
2. Register the device, set ports/name/price and approve it. Save a per-machine baseline containing that device's own private identity and service configuration. Never clone an identity to another machine.
3. The baseline must contain no tenant game accounts, tenant files, Moonlight paired clients or pending pairing requests. Protect any persistent exclusion containing device identity/config from tenant writes. Decide whether local lease state survives rollback; the authoritative lease stays on Controller either way.
4. Enable restore for every Windows boot and perform a cold boot. Prove tenant files/game accounts/pairing disappear, identity remains the same, and the latest Controller config replaces the older baseline revision before any ready report.
5. Repeat with active streams, old client reconnect, process-only service restart, network outage, hibernate/fast startup and expiry during downtime. A service restart uses the same native Windows boot identifier. A changed identifier proves an OS boot occurred; it does **not** prove restoration happened.

`baselineValidated` is an operator installation attestation. It is never per-boot proof that Restore Rx actually restored a volume. A qualified restoration evidence provider must independently supply that proof for the current boot; until it exists, reconciliation holds. Same-boot restart still requires qualified proof and fresh fenced cleanup/health checks; it cannot manufacture restoration from a new process ID.

## Reconciliation contract and limits

Signed `POST /agent/reconcile` uses the same registered-device signature, timestamp and nonce boundary as `/agent/sync`. Request carries bootId, appliedConfigRevision, baselineValidated and explicit restorationVerified/fencedCleanupVerified/healthVerified evidence (all default false). The trusted Agent must collect fresh evidence in a serialized startup flow; Controller cannot independently observe disk restoration or Windows stream state. Do not set these flags from operator configuration or transport success.

The first call fetches current configuration with evidence false. Agent persists config before submitting evidence. Controller requires approval and exact current config revision. Missing proof returns Hold without releasing allocation. Full proof retires older boots/commands, invalidates pending commands including pairing, clears offers and uncommitted extensions. Only a still-active, unexpired committed lease can return ResumeLease, with its original absolute expiry and committed revision. Preparing leases become failed. Expired/ending/failed leases never resume; fenced reconciliation releases their held allocation and returns PrepareAvailable without sending a shutdown command. Reconciliation records notReady; action and readiness do not grant stream permission or tenant approval.

Call reconciliation **before** starting ordinary sync on a new boot. Existing sync intentionally turns a boot-mismatched active lease into ending. Such a lease is not revived by reconciliation. This conservative fallback may end a valid rental if ordering is wrong. Production wiring must serialize startup, reconcile/import the durable lease with a monotonic deadline using total request elapsed time, and only then start command sync. It must retain the real default-closed gate throughout. Same-boot known expiry enforcement must continue independently of network, cleanup and persistence. Do not add a new lease duration on resume, replay delayed old-boot commands or revive an expired lease.

Production completion additionally requires tested fenced lifecycle cleanup and fresh proof binding to the current lease/boot, a live gate closed before Sunshine/network startup and on service failure, health/capture/input/audio checks, cross-boot lease import, and real machine qualification. `docs/sunshine-compatibility.md` lists the remaining Sunshine tests. These are deployment blockers, not completed tests.
