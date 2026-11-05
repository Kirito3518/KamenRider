# Agent Presence During Boot Reconciliation

## Intent

An installed Agent should keep reporting that its machine is reachable while it is waiting for safe boot reconciliation. This lets the administrator see a newly enrolled or temporarily blocked machine without mistaking it for ready.

## Approaches

- Stop all sync until reconciliation succeeds. This is safe for command processing, but hides a working Agent as stale and prevents authoritative configuration updates.
- Continue normal sync and process returned commands. This makes the machine visible, but can apply a lease or pairing command before restoration, stream fencing, and health checks are established.
- Continue heartbeat/config sync with readiness forced to notReady, observe Controller time, and discard command and active-lease fields until reconciliation succeeds. This is the selected implementation: it keeps administration current and preserves the existing fail-closed boundary.

## Design

The Agent runs the sync request after each reconciliation attempt, including NotReady and Fault outcomes. It reports ready only when reconciliation is Ready; otherwise it reports notReady. It may apply the Controller's current port configuration, but it must ignore commands and active lease confirmations. It still observes authenticated Controller time so a persisted lease cannot gain time when the local wall clock is behind. A presence-only response does not process or acknowledge command results; the Agent retains the batch until a Ready sync lets the Controller process it. The Agent processes blocked response time before attempting local config persistence.

## Constraints

- Pending and approved-but-not-ready machines remain ineligible for rental.
- An Agent without successful reconciliation must not apply lease or pairing commands or confirm a lease.
- A Controller outage remains notReady; a heartbeat is not evidence of power, desktop health, restoration, or stream readiness.
- A notReady/unknown heartbeat updates presence and configuration ACK metadata only; it cannot retire boot history, change lease or command state, process results, deliver commands, or confirm a lease.
- Rental availability requires the applied configuration revision to equal the current Controller revision.
- Reboot Restore Rx remains an externally configured system; this change does not call or modify it.

## Scope

This is an enabling Agent-runtime change for the full rental roadmap. Production stream fencing, qualified boot evidence, Windows service installation, VNC/noVNC, and physical acceptance remain separate roadmap tasks.
