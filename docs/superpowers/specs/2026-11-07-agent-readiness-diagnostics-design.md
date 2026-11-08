# Agent readiness diagnostics design

## Intent

Give administrators a clear, bounded explanation of why an enrolled Agent reports `notReady`. The current Agent correctly keeps the stream gate closed when boot, restoration, fencing, or desktop evidence is unavailable, but the admin page only shows the single readiness label.

## Goals

- Report the latest per-check readiness result from the Agent through its authenticated, signed heartbeat.
- Show those results only in the administrator's machine list.
- Use a small allowlist of check IDs and status values; never transmit exception text, credentials, local file paths, or Restore Rx details.
- Persist each report atomically with the heartbeat so the admin list shows one consistent snapshot.
- Keep `ready`, rental eligibility, lease allocation, and stream-gate decisions unchanged.

## Non-goals

- Do not create boot/restoration/desktop-health evidence or change any fail-closed provider.
- Do not infer Restore Rx success from boot ID or inspect, query, configure, or modify Restore Rx.
- Do not change lease, approval, stream-gate, Sunshine, VNC, or hardware behavior.
- Do not expose diagnostic reports in renter-facing routes.

## Approaches considered

1. **Show only a generic not-ready message.** Smallest change, but does not tell an administrator what is missing.
2. **Collect Windows logs or live service state from the web server.** This cannot reliably inspect remote client machines and risks presenting availability as proof of readiness.
3. **Send bounded check results in the existing Agent heartbeat, persist them in Controller storage, and render them in Admin.** Selected because the Agent owns local evidence, the Controller already authenticates and validates heartbeat data, and the UI can explain blockers without changing the rental gate.

## Data and behavior

- Each diagnostic is a `{ checkId, status }` pair. The exact ordered check IDs are `baseline`, `bootChallenge`, `restoration`, `streamFence`, `desktopHealth`, `configurationRevision`, `leaseReconciliation`, and `runtimeHealth`. Status is one of `passed`, `blocked`, `unavailable`, or `unknown`.
- A report contains at most eight entries, has no duplicate check IDs, and contains only the fixed IDs/statuses above. The Agent sends the entries in the defined order; the Controller may accept any order but stores and returns the canonical order.
- `passed` means that check returned its required positive evidence during this run. `blocked` means an implemented check ran and rejected the evidence. `unavailable` means no qualified provider exists to run the check. `unknown` means the check was not reached or its input is not yet available. A missing or expired/mismatched boot challenge is `blocked`; checks after an earlier stop are `unknown`. The default baseline, restoration, desktop-health, and runtime-health evidence report `unavailable`. `StreamGuardBootEnvironment` delegates the stream-fence cleanup check through the registered pairing session and reports `passed` only after guarded cleanup completes, but `BootReconciler` does not reach that step until baseline and restoration evidence pass.
- Boot evidence interfaces return an explicit status rather than a `bool`, so an absent provider cannot be mislabeled as a failed live test. Only `passed` lets the current reconciliation algorithm proceed past that check.
- The Agent reports the latest diagnostic snapshot alongside its existing readiness value. It must not convert an unavailable check to `passed` or change the readiness decision.
- Diagnostics are part of the existing signed heartbeat body. A valid request with diagnostics is accepted, and changing diagnostic bytes after signing is rejected by the existing signature verification.
- The Controller validates the allowlists and a fixed maximum count, then atomically upserts the report with heartbeat presence. Missing reports from older Agents clear any prior report rather than leaving stale diagnostics.
- The Admin machine DTO includes the optional latest report. The Admin UI labels each check and status in Thai and identifies the report as diagnostic information, not a rental approval. Public machine-list responses explicitly omit the report.
- Existing `CanRent` logic and the requirement for `readiness == ready` remain unchanged. Diagnostic values never grant readiness.
- The new storage schema is included in the verified SQLite backup/restore schema checks, while compatible pre-diagnostics backups remain accepted as historical schemas.

## Risks and mitigations

- An Agent can report a false status because it owns the checks. The Controller treats this data as display-only and continues to use its existing readiness gate.
- Old Agent versions may omit the new optional report. The Controller accepts missing reports and clears stale data; heartbeat compatibility is preserved.
- An overly detailed reason could disclose sensitive machine information. The wire format carries only fixed IDs and status enums.

## Acceptance

Tests prove Agent reports match the checks actually evaluated; Controller rejects malformed, oversized, or unknown diagnostics and persists valid reports atomically; signed-heartbeat verification covers diagnostic bytes; Admin renders the report; older heartbeats without diagnostics remain accepted; and no diagnostic combination changes rental eligibility.
