# Fleet Sunshine management credential delivery (Agent side)

This note covers only `services/Agent`: how one fleet-wide Sunshine management credential,
configured once in the admin web, reaches every Agent. It is Agent-only delivery. The Agent does
**not** run `Sunshine.exe --creds` and never edits Sunshine's own configuration file; the delivered
credential is stored locally for the Agent's own pairing/management calls.

## Flow

1. After a successful signed `POST /agent/sync`, the Agent observes
   `SyncResponse.SunshineCredentialRevision`, the current fleet revision (`0` = not configured).
2. `SunshineCredentialWorker` (hosted in `AgentHost`) is woken through `SunshineCredentialSyncSignal`.
   It only acts when the fleet revision is greater than the locally applied revision.
3. The Agent sends a signed `POST /agent/sunshine-credential` with body
   `{"knownRevision":N}` and the same `X-Agent-Id` / `X-Agent-Timestamp` / `X-Agent-Nonce` /
   `X-Agent-Signature` scheme as `SyncClient`, over normal platform TLS validation (never bypassed).
4. Responses:
   - `200` + `SunshineCredentialDelivery` -> save with
     `WindowsSunshineCredentialStore.Save(credential, replace: true)` (DPAPI machine protection and
     the existing private file ACL), then record the applied revision.
   - `204` already current, `404` not configured, `401` signature rejected, `409` not
     approved/quarantined, or any transport/parse failure -> no write; retry on the next sync signal
     with bounded backoff (5s..60s). Only exception types and status categories are logged; credential
     values are never logged.
5. Rotation therefore takes effect without a full Agent restart: `LazySunshinePairingSession`
   invalidates its cached `SunshineClient`/`PairingSession` when the applied revision changes and
   reloads the protected credential on the next pairing operation.

## Local revision state

`DataDirectory\sunshine\applied-credential.json` contains only `{"revision":N}`. It uses an atomic
temporary-file replace and the same protected ACL model as the other private Agent files (Agent
service SID, SYSTEM, Administrators). The password and username are never written there.

A manually provisioned credential (`Agent.exe --provision-sunshine-credentials`) with **no**
applied-revision file counts as local revision `0`. If a fleet credential is configured, the next
successful sync replaces the manual credential with the fleet value. When the fleet value is `0`
(not configured) the Agent leaves any local/manual credential untouched and does nothing.

## Failure isolation

Credential delivery is not a readiness or approval gate. Every failure is contained: the hosted
worker logs the category, keeps the previous credential, retries later, and never crashes the Agent
service. A malformed local revision file is reported as revision `0` in heartbeats (so the
Controller can see the machine is behind) and blocks rotation until the protected file is repaired.
