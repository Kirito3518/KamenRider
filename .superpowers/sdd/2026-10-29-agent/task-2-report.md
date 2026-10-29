# Task 2 report — enrollment, approval and durable identity

Implemented contracts for machine identity, public enrollment result, editable administrator metadata and machine DTO; SQLite registry with transactional idempotent schema creation, constrained ports, parameterized writes, revision-checked metadata updates and transactional approval/audit writes. SQLite connection pooling is disabled so disposal actually closes the database and restart tests exercise durable state.

The existing isolated listener/service-credential middleware protects internal machine routes. Approval additionally requires X-Authenticated-Actor-Id supplied by the trusted Next server. Approval records actor and UTC time; blank name/nonpositive price cannot be approved. Pending machines expose CanRent=false; the lease task must still enforce approval transactionally and combine readiness/connectivity when implementing actual rental eligibility.

Enrollment requires HTTPS through existing middleware and an ECDSA proof of possession over machine ID, method, path, Unix timestamp, nonce and SHA-256 body digest. Duplicate enrollment preserves administrator configuration, approval and revision. A different key cannot replace an existing machine ID. Enrollment never grants an authentication token or approved-machine privilege. Future signed agent routes must load the stored key using ControllerDatabase.PublicKey rather than trust a submitted key. AgentAuthentication enforces a 60-second timestamp window and an in-memory 120-second nonce cache.

DeviceIdentity creates a durable P-256 key and random machine ID; Windows directory and file ACLs disable inheritance and allow the running service identity, LocalSystem and Administrators. DataDirectory must be a dedicated service directory, run under the intended service account. Initial enrollment starts with Agent host, selects configured network adapter, signs local metadata and retries unavailable enrollment every 5 seconds. HTTP is rejected; HttpClient retains platform certificate/hostname validation. No services were installed and no certificate validation bypass was added.

Validation:
- Tests were authored before production APIs. Initial targeted run failed to compile for the missing Data/Identity/Security APIs; this is a TDD process limitation (compile-red rather than behavioral-red).
- EnrollmentClientTests likewise initially failed for the missing Enrollment API.
- Full solution: dotnet test services/KamenRaider.slnx passed 18 tests (Controller 15, Agent 3), no warnings.
- Required filter: dotnet test services/KamenRaider.slnx --filter "FullyQualifiedName~EnrollmentTests|FullyQualifiedName~IdentityTests" passed 7 tests.
- HTTP integration validates unsigned rejection, signed enrollment, replay rejection, actor requirement, revision update and stopping/disposal/rebuilding Controller with a temp SQLite file preserves approval/name/price/actor.
- Identity tests validate key/ID persistence, tampered body rejection, nonce replay rejection and expired timestamp rejection.
- dotnet list services/Controller/Controller.csproj package --vulnerable --include-transitive reports no vulnerable packages. Microsoft.Data.Sqlite 10.0.0's default SQLitePCLRaw 2.x native library was flagged; explicit bundle 3.0.2 resolves it.

Concerns and deferred boundaries:
- Installation must provision the controller HTTPS certificate/private key and trust root and secure the controller service credential/database directory. Task 2 does not install certificates/services or alter the Task 1 Kestrel certificate configuration.
- Nonce cache is process-local; controller restart clears it. A previously signed enrollment may replay within the 60-second timestamp window after restart, but enrollment remains idempotent and cannot grant/update approval or configuration.
- First enrollment and admin approval require an operator to verify the physical machine/public-key fingerprint through installation/inventory; no human-entered pairing code is used. A cloned key represents a cloned machine identity and must not be deployed to multiple machines.
- Atomic CreateNew prevents overwriting an existing identity. Concurrent first startup against one directory is unsupported; partial/corrupt identities fail closed rather than regenerate an approved ID.
- Polling, heartbeat, boot ID, runtime readiness and configuration acknowledgement are Task 3; MachineDto uses null/unknown until then. Metadata enrollment does not update observed addresses on subsequent enrollment.
- No public key/private key/service secret is returned from internal machine list or logged. The initial machine public key is available from DeviceIdentity.PublicKey and ControllerDatabase.PublicKey for trusted verification flows.
