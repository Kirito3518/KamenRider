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

- The isolated .NET solution test run passed 277 tests: Agent 142/142 and Controller 135/135.
- Agent lease confirmation now opens the stream gate only after an exact active-lease confirmation; failed gate opens stay closed, record a fault and can retry. This is contract-level behavior only; no production gate is registered or hardware-qualified.
- Agent dispatches `PrepareMachine` only for a current, unleased boot and returns a failed command result when no preparation provider is available. The default provider is intentionally unavailable; no preparation hardware action ran.
- A Ready sync response is accepted only when its configuration revision matches the revision that was validated before the request. If the response advances config, Agent applies it, closes the gate, and defers lease/preparation/confirmation commands until reconciliation proves readiness against the new revision.
- Eight installer/package/readiness test scripts passed under both Windows PowerShell 5.1 and PowerShell 7. The package-build checks wrote only to temporary artifacts; no services changed.
- All 11 Agent PowerShell checks, including package build, passed under Windows PowerShell 5.1 after the final changes. All 10 checks other than package build passed under PowerShell 7 after the final changes. The latest PowerShell 7 `Test-AgentPackageBuild.ps1` run completed package and SHA-256 assertions but returned nonzero because antivirus blocked deletion of a temporary directory containing `Install-AgentPackage.ps1`; package build and cleanup passed under Windows PowerShell 5.1.
- The Agent installer now configures VNC only after a valid enrollment receipt on a fresh install or enrollment repair, then opens the admin return page. Pending enrollment skips VNC and gives a retry instruction. Its plan exposes the source IP and VNC setup intent without credentials.
- Fresh VNC setup stages the MSI with service registration and RFB disabled, applies the exact Private-profile firewall rule, then performs final configuration using the MSI maintenance API. It requires the Windows Firewall Private profile to be enabled, rejects orphan/unmanaged TightVNC installation traces, pins the official MSI SHA-256 and `OOO GlavSoft` signer, adds the password property names to `MsiHiddenProperties` through a ProductCode-validated secure transform, rejects Windows Installer Debug values with command-line logging bit `0x04`, and avoids child-process command-line exposure.
- The official MSI was downloaded and inspected read-only. Its package-authored `MsiHiddenProperties` omitted the VNC password properties; generating and applying the custom transform to a temporary database copy confirmed that both names are hidden. The MSI was not installed, so Windows service configuration and a client VNC connection remain untested.
- Host readiness inspection found Sunshine running and a separate `0jay-agent` service; no KamenRaider Agent or Controller Windows service is installed. No VNC service or KamenRaider VNC firewall rule was created by these checks.
- Targeted ESLint and `pnpm exec tsc --noEmit` passed after the renter pairing UI update.

These checks validate software behavior and package plans only. They do not change any hardware result in the table above.
