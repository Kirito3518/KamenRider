# Controller lease readiness guard

## Intent

Keep a machine from receiving a new lease unless it satisfies the same current rental-readiness conditions shown by the Controller inventory. This closes a gap where the inventory correctly reported a machine as unavailable, but the lease creation transaction checked only approval and allocation.

## Goals

- Evaluate eligibility inside the same Controller database transaction that creates a lease.
- Require approval, a fresh heartbeat no older than 20 seconds, reported readiness `ready`, an applied configuration revision equal to the current machine revision, and no held allocation.
- Reject an ineligible request without creating a lease or recording its idempotency key.
- Preserve successful lease preparation, durable idempotent retries, and single-allocation concurrency behavior.

## Non-goals

- Change admin approval or machine metadata flows.
- Infer readiness from service state or hardware probes.
- Modify Agent behavior, shutdown, stream fencing, or Reboot Restore Rx.
- Change the existing HTTP contract.

## Approaches

1. Keep the current Controller check of approval/allocation and rely on renter UI filtering. Rejected because direct API calls and stale clients can bypass UI checks.
2. Add a preflight check in the Next.js route. Rejected because it would race with machine state changes and would duplicate business rules outside the transaction that allocates the machine.
3. Reuse the Controller's rental-eligibility predicate in the lease transaction. Selected by the implementation agent because it enforces the existing `/api/machines` contract at the allocation boundary and closes the race.

## Constraints

- Eligibility must be checked using the injected `TimeProvider` and the same SQLite transaction used for allocation.
- Heartbeat freshness uses the existing 20-second cutoff.
- Failed eligibility remains an `InvalidOperationException`, which the existing endpoint maps to HTTP 409.
- Existing allocation uniqueness and idempotent-retry semantics remain unchanged.
- No Reboot Restore Rx operation is part of this work.

## Acceptance

- Lease creation fails for pending/unapproved, `notReady` or `unknown`, stale heartbeat, and stale configuration acknowledgment.
- A fresh, approved, ready machine with the current config acknowledgment can still receive one lease.
- Existing concurrency and durable idempotency tests pass.
