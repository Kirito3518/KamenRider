# Agent acceptance record

Status: **NOT READY for rentals**. Task 5 installer/package checks passed after the earlier antivirus block was cleared, and Task 6 software suites pass. The installer has not been applied, no Agent or Stream Guard service is installed, the package is absent, production boot/restoration/desktop-health providers remain unavailable, Sunshine credentials/TLS are not provisioned on this host, and no rental-machine hardware acceptance has been performed.

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

## Final software and qualification update (2026-11-07)

This update supersedes the earlier antivirus status and 217/217 Agent count above.

- Final full Agent suite passed 218/218 at D:\kamenraiderplay-acceptance-20261107\agent-final. The full suite before the final Task 6 fix passed 217/217; the final focused pipe/coordinator set after that fix passed 43/43. The full Controller suite passed 151/151. These are software contract results and do not establish Windows firewall or boot enforcement.
- Task 5 PowerShell checks passed under Windows PowerShell 5.1.22621.6133 and PowerShell 7.6.9, including 53 mocked setup/rollback cases, package plan/install checks, and package build. The user Allow/restore succeeded and no antivirus block recurred. Task 5 and Task 6, including the final fix, were reviewed clean.
- Existing web verification passed TypeScript, lint (zero errors and one existing img warning), and the Webpack production build. The default Turbopack build failed when its Babel loader subprocess crashed in three existing UI imports; these build and check results are software evidence only.
- Read-only host check on 2026-11-07 found Domain, Private, and Public firewall profiles enabled with local rules allowed; no KamenRaider Agent, Controller, or Stream Guard service exists; public/agent/AgentPackage.zip is absent; and .env.local has AGENT_INSTALL_ENABLED=false. Profile settings do not prove that the managed fence is installed or effective.
- No Sunshine credentials were provisioned, no service/firewall Apply or installation was performed, no actual stream test ran, and Reboot Restore Rx was not queried or changed. Default boot, restoration, and desktop-health providers remain unavailable. Rental readiness remains NOT READY. Hardware acceptance remains NOT TESTED for effective IPv4/IPv6 blocking, existing-connection termination, Agent/Guard crash and recovery, actual Moonlight pairing, lease expiry, and cold boot/Restore Rx rollback.
