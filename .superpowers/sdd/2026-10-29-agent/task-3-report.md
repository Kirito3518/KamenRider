# Task 3 report

Implemented signed `/agent/sync`, persistent heartbeat metadata with 20-second reachability expiry, authoritative revisioned controller port configuration, Agent signed sync polling, atomic metadata application, Public IP refresh/stale tracking, and explicit-interface/broadcast WOL.

## Validation

- Initial targeted Controller test run failed because the new Network namespace/API did not exist; implementation followed the tests. New Agent configuration/client tests similarly started with missing API compile failures. HTTP endpoint test then produced a behavioral red (404 instead of 401) before endpoint implementation.
- `dotnet test services/Controller.Tests --filter "FullyQualifiedName~SyncTests|FullyQualifiedName~NetworkTests"`: 3 passed.
- `dotnet test services/KamenRaider.slnx`: 34 passed (Controller 29, Agent 5), zero failures, no warnings in final output.
- Covers heartbeat expiry including persistence across database reopen, pending ineligibility, web settings overriding baseline revision, no commands, magic packet bytes, invalid/private Public IP and timeout retention, stored-key authentication rejecting another key, WOL readiness preservation and admin broadcast, atomic revision persistence, and cryptographically valid Agent request signatures.
- No live WOL packets, service installation, shutdown, or lease actions performed. HTTP/UDP boundaries mocked in tests.

## Integration details and concerns

- Configure Controller `PublicIpEndpoint` with an HTTPS plain-text IPv4 endpoint to enable five-minute background refresh. Each fetch has a ten-second linked timeout. Last known public IP is retained in memory and marked stale after failures; process restart begins unknown/stale.
- Configure `WakeInterfaceAddress` and `WakeBroadcastAddress` explicitly. There is no inferred /24 or default broadcast; missing/invalid settings yield 503 on wake. Sending a wake request does not change readiness.
- Sync uses X-Agent-Id plus the existing signed timestamp/nonce/body headers, and only the public key stored for that ID. Submitted keys are never used on this route.
- Task 2 nonce cache remains process-local. Sync has no command-result side effects and returns no commands. Future state-changing lease actions require durable replay safety.
- Agent reports unknown readiness until a future real Sunshine health probe exists; sync or WOL success cannot imply ready. Polling is five seconds after success, with capped exponential reconnect delays (10/20/40/60 seconds), HTTP cancellation and service cancellation. Scheduling/backoff itself is not covered by virtual-time tests.
- Agent only writes `applied-configuration.json` in DataDirectory; no Sunshine config writing. The worker conservatively ignores returned commands.
- Boot ID is generated per Agent worker lifecycle; no OS boot identity integration in this task.
