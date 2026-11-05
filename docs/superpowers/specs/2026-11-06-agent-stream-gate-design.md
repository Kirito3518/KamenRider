# Agent Sunshine Stream Gate Design

## Intent

The rental Agent must keep Sunshine unreachable before lease confirmation, after lease expiry, during boot reconciliation, and when the Agent process loses control. A Windows firewall rule by itself does not end an already established stream, while stopping Sunshine alone does not protect against a listener that starts before Agent reconciliation. The machine needs one local control boundary that coordinates both actions.

## Goals

- Close Sunshine ingress by default at installation, machine boot, Agent disconnect, lease expiry, and any failed gate operation.
- End active Moonlight sessions by stopping and verifying the Sunshine Windows service after ingress is blocked.
- Open ingress only after the Agent has received and validated the exact active lease confirmation from Controller.
- Enforce the confirmed lease's current-boot monotonic deadline independently inside the local gate process.
- Keep the Agent service under its existing restricted virtual service account.
- Leave Reboot Restore Rx configured and operated by the machine owner. This implementation must not query, configure, or modify it.

## Non-goals

- This gate does not prove a Restore Rx rollback, desktop capture, encoder, audio, input, network forwarding, or a successful Moonlight stream.
- This gate does not install Sunshine or change the router.
- This gate does not make the development workstation a rental machine.

## Constraints

- The approved roadmap requires the existing broad Sunshine allow rules to be accounted for and requires ingress to remain closed before Sunshine can listen.
- The installed Foundation Sunshine service is named SunshineService on the inventoried host. Its service wrapper owns the Sunshine child process; stopping the wrapper is the selected session teardown mechanism and requires hardware qualification.
- A lease grant must identify rental, revision, OS boot, UTC expiry, and the Agent's monotonic deadline. An older revision or another boot must never reopen ingress.
- Existing active lease confirmations are accepted only by the current authenticated Agent sync path. The gate accepts requests only over a local named pipe whose ACL permits SYSTEM and the Agent service SID.
- All firewall profiles must be enabled and local rule changes must be permitted. Otherwise the gate stays closed and reports a fault.
- The development workstation currently has a separate 0jay-agent service and broad Sunshine firewall allows. No service, firewall, Sunshine, or Restore Rx changes may be made there for this implementation.

## Approaches

1. Keep the gate closed permanently. This preserves the current safe default but does not enable the requested rental stream.
2. Let the Agent directly toggle firewall rules and stop Sunshine. This requires granting the restricted Agent service administrator-level machine control and still needs a crash boundary independent from the Agent process.
3. Add a local Stream Guard Windows service running as SYSTEM, keep the Agent restricted, and connect them over an ACL-protected local pipe. The guard owns a program-scoped all-profile block rule and the Sunshine service lifecycle; it opens only after an exact lease grant, closes on pipe loss or deadline, and uses an SCM recovery command to fence after a guard crash. This is the selected approach because it preserves the Agent's restricted account and gives the host a separately supervised gate owner.

## Design

The Agent sends a versioned lease grant containing rental ID, lease revision, boot ID, absolute expiry, and monotonic deadline to the Stream Guard over a local named pipe. The pipe server allows only SYSTEM and the installed Agent service SID. Malformed, stale, cross-boot, expired, or oversized requests fail closed.

On startup and on every close request, the guard enables a Windows Defender Firewall block rule scoped to the resolved Sunshine executable and all profiles, protocols, addresses, and interfaces, then stops SunshineService and verifies both states. This explicit block rule must override Sunshine's existing allow rules. The guard does not call an operation successful if either state cannot be verified.

On an open request, the guard first establishes the closed state, validates the grant against its current boot ID and uptime, starts SunshineService while the block rule remains enabled, verifies the service is running, and only then disables the block rule. Any error re-enables the block and stops the service. A new grant replaces the current deadline only after the Agent has received the matching Controller confirmation.

The guard keeps the pipe session alive. Pipe loss immediately closes ingress and stops Sunshine. A monotonic timer also closes the gate at the confirmed deadline if the Agent stalls. A guard-service failure action launches a protected SYSTEM recovery command that reapplies the block and stops Sunshine before service recovery. Gate startup always begins closed; SunshineService is changed to manual start so Windows cannot expose it before the guard has applied policy.

Boot evidence and stream-health checks remain separate interfaces. The production Agent will remain fail-closed until those providers, Sunshine management credentials/TLS, and local acceptance are complete. The gate is not reported as hardware-qualified based on unit tests.

## Operations and acceptance

Installation must create the closed firewall rule before Agent enrollment can start, install the guard, verify SunshineService control, then install/start the restricted Agent service. Plan mode must be read-only. Rollback may remove only services and rules created by that transaction and must restore the prior Sunshine start mode/dependencies if setup fails before commit.

On a dedicated rental test machine, verify the rule across IPv4/IPv6, Domain/Private/Public profiles, pre-existing Sunshine allows, new and established Moonlight connections, service stop timeout, Agent crash, guard crash/recovery, offline lease expiry, and Windows reboot. Until those checks pass, the Controller must continue to report the machine not ready.
