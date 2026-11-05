# Agent acceptance record

Status: **NOT READY for rentals**. The software lifecycle test uses simulated Agent messages, command results and boot proof. It does not exercise a production Agent, Sunshine, Restore Rx, a real stream fence, or a physical machine. Production reconciliation now wires lease import into `LeaseEnforcer`; `PrepareMachine` is dispatched and fails closed when its preparation provider is unavailable, which remains the production default. Restoration evidence, an enforcing gate and desktop health checks also remain unavailable; see [agent operations](agent-operations.md) and [Sunshine compatibility](sunshine-compatibility.md).

| Hardware scenario | Result | Evidence required before acceptance |
| --- | --- | --- |
| Wake-on-LAN from shutdown | NOT TESTED | Named test client, network and cold shutdown; record wake and readiness |
| Expiry while offline | NOT TESTED | Disconnect Agent through expiry; verify access terminates locally |
| Restart Controller, web and Agent | NOT TESTED | Restart each during a lease; verify expiry and access |
| Cold boot desktop | NOT TESTED | Verify desktop, capture, input and audio before readiness |
| Restore Rx | NOT TESTED | Verify restoration on every boot and identity survives |
| Old Moonlight client denied | NOT TESTED | Pair, expire, restore and attempt reconnect, including an active stream |
| Two requests collide | NOT TESTED | Concurrent requests against the deployed system; verify one allocation |
| Custom internet ports | NOT TESTED | Connect from a real external network using configured ports |

No designated and available test client was supplied. Record machine, installed versions, date, operator, method and evidence for each future pass or failure. Fake tests must never be entered as hardware passes.

## Software checks (2026-11-06)

- The isolated .NET solution test run passed 275 tests: Agent 140/140 and Controller 135/135.
- Agent lease confirmation now opens the stream gate only after an exact active-lease confirmation; failed gate opens stay closed, record a fault and can retry. This is contract-level behavior only; no production gate is registered or hardware-qualified.
- Agent dispatches `PrepareMachine` only for a current, unleased boot and returns a failed command result when no preparation provider is available. The default provider is intentionally unavailable; no preparation hardware action ran.
- Eight installer/package/readiness test scripts passed under both Windows PowerShell 5.1 and PowerShell 7. The package-build checks wrote only to temporary artifacts; no services changed.
- Targeted ESLint and `pnpm exec tsc --noEmit` passed after the renter pairing UI update.

These checks validate software behavior and package plans only. They do not change any hardware result in the table above.
