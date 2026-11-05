# Agent Install and Enrollment Flow

## Intent

An administrator copies one PowerShell command from the admin machines page and runs it in an elevated PowerShell session on a client PC. The command downloads a hash-verified package, installs the Agent as a Windows service, starts enrollment, and opens the configured web page when installation reaches a usable state.

## Approaches

- Keep installation disabled until the real Sunshine fence and boot environment are qualified. This prevents enrollment testing and does not satisfy the requested install-to-pending flow.
- Install the service but leave it disabled for the administrator to start manually. This adds steps and still does not enroll after the pasted command completes.
- Install and start the service under its restricted virtual service account while keeping reconciliation fail-closed. This lets the Agent enroll and report notReady, but it cannot be rented until independent boot, stream, and health evidence is implemented. This is the selected approach because it meets the requested installation flow without claiming rental readiness.

## Goals

- Build a repeatable self-contained Agent package and report its SHA-256 hash.
- Keep the admin command behind an authenticated admin route and require explicit environment configuration.
- Allow HTTP only for loopback package and site URLs in local development; require HTTPS for Controller URLs and all non-loopback package/site URLs.
- Verify both the bootstrap script and package before installation.
- Write Agent configuration with the Controller URL, private data directory, selected network adapter, and initial Moonlight/Sunshine ports.
- Install the Agent with its restricted Windows virtual service account, protected ACLs, automatic startup, and recovery actions; start it after configuration is securely published.
- Wait briefly for an enrollment receipt, show machine ID/fingerprint when received, and open the configured web page on the client PC.
- Leave a running Agent notReady until boot proof and a real stream fence are qualified.
- Support Windows PowerShell 5.1, which is present by default on the target PCs.

## Non-goals

- No installation or command execution for Reboot Restore Rx.
- No rental readiness, lease, Sunshine management, VNC installation, or firewall opening from this installer milestone.
- No service upgrade or repair path; existing services and non-empty data directories are rejected.
- No actual Windows service installation on the developer machine as part of automated verification.

## Constraints

- ControllerUrl remains HTTPS, including the local development endpoint https://localhost:7443.
- Package SHA-256 and trusted root thumbprint are required; installation remains disabled when environment values are missing or invalid.
- Publish files must pass the existing owner, ACL, reparse point, and hard-link checks. Data and private configuration remain restricted to the virtual service account, SYSTEM, administrators, and the elevated installer.
- Failed service creation or start must roll back any service created by this invocation. Pre-existing machine data is never overwritten.
- Enrollment retries in the service if the Controller is temporarily unavailable; timeout does not remove a healthy installed service.
- The package builder emits ignored/generated package artifacts; source files and secrets stay in version control, generated zip does not.
- Do not modify or invoke Reboot Restore Rx.

## Remaining implementation calls

- Package output defaults to public/agent/AgentPackage.zip so the local development web server can serve it; the package builder prints the SHA-256 that must match AGENT_PACKAGE_SHA256.
- A missing or ambiguous active network adapter is resolved interactively by selecting its interface GUID; installer does not guess between multiple adapters.
- The enrollment wait is bounded at 90 seconds. If no receipt arrives, the service remains installed and retrying, the script reports the pending state, and still opens the configured web page.
- Successful setup opens /admin/machines on the configured base URL. Authentication remains the website's responsibility.
