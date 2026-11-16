# Agent onboarding from the admin page

## Intended operator flow

1. An administrator opens `/admin/machines` and copies a PowerShell install command. The web origin used in that command comes from the required `AGENT_INSTALL_BASE_URL` setting. In local development, set it to `http://localhost:3000`; localhost works only when the browser and target Agent are on the same machine.
2. The administrator runs the command in an elevated PowerShell window on one Windows client. It downloads a hash-pinned Agent package, installs the Agent service and private per-machine identity, then waits for an enrollment receipt. The command carries an admin-issued ticket that expires after 24 hours and can enroll one new identity. If the Controller is unavailable, the service keeps retrying with that ticket and reports enrollment as pending. If it expires, the administrator copies a fresh command and reruns it on that PC to renew only the private ticket.
3. The Agent reports its generated identity, local IP, MAC address and initial Moonlight/Sunshine ports to the Controller. The Controller creates a **pending** machine. Re-running on the same client reuses its identity and does not create a second machine. A copied identity must not be reused on another client.
4. The admin page refreshes its machine list automatically. The new pending machine appears first. The administrator checks the identity fingerprint on the client, sets name, price and ports on the web, and then approves it. A pending machine cannot be rented.
5. After approval, the Agent receives the Controller configuration revision. Changing ports on the web only updates metadata; the operator configures Sunshine and router forwarding separately. Rental readiness remains gated by the stream fence, boot reconciliation and live health checks.

Before the first Moonlight pairing, prepare the client PC's Sunshine management TLS trust and credentials. The Agent calls Sunshine at `https://localhost:<SunshinePort>` and keeps normal certificate chain, name and expiry validation; provision and trust a certificate matching `localhost` through the host's approved process. After Agent installation, run this command from an elevated, interactive PowerShell window on that PC:

```powershell
& "$env:ProgramFiles\KamenRaider\Agent\Agent.exe" --provision-sunshine-credentials "$env:ProgramData\KamenRaider\Agent"
```

It prompts for the Sunshine management username and password without echo and asks to confirm the password. Do not put credentials in the install command or send them in chat. The command stores them with machine-scope DPAPI protection and a private Agent service ACL; it does not contact Sunshine. Rotate an existing credential with `--replace`, then restart `KamenRaider Agent`. Missing credentials or untrusted/mismatched Sunshine TLS leave pairing unavailable even though Agent enrollment and heartbeats can work. See [Agent operations](agent-operations.md#installation-preparation) for the credential and TLS details; the customer-facing machine page already gives the Moonlight PIN steps.

## Recover if the service stops before enrollment

The installer keeps the protected installation and generated machine identity when the Agent service stops during the enrollment wait. Before the ticket expires, fix Controller reachability or certificate trust, then run `Start-Service -Name 'KamenRaider Agent'` from elevated PowerShell. If the service is paused, use `Resume-Service -Name 'KamenRaider Agent'`. The service retries enrollment with the same identity; do not run the installer again. Confirm the service is `Running` and the pending machine appears in `/admin/machines`.

If the ticket expires before the machine enrolls, copy a newly generated command from `/admin/machines` and run it on that same PC. The installer verifies the existing service, protected binaries, configuration and identity data, then atomically replaces only the private enrollment ticket and resumes the service. It refuses partial or mismatched installations and never overwrites the machine identity.

The installer does not configure or operate Reboot Restore Rx. The operator's existing restore setup remains external to this onboarding flow.

## Delivery and security requirements

- Generate the command only for an authenticated administrator. Use an expiring, single-use enrollment ticket so a copied install command cannot register unrelated machines indefinitely. Keep the ticket out of URLs and server logs where possible.
- Publish a pinned package hash with the command. Never run unverified downloaded PowerShell or executable bytes. The downloaded installer must require elevation and place binaries under a protected directory, with service identity and secrets in private ACL-protected storage.
- Show the command only when the published package, Controller URL, TLS trust and installation path are configured. Do not show a command that points to a missing package or a guarded installer.
- The package must enroll against a reachable HTTPS Controller URL. A URL chosen from an admin browser request or `localhost` on the server is not automatically reachable from client machines.
- Show installation success on the client only after the Controller acknowledges enrollment. On the web, continue with metadata and approval; do not imply that pending enrollment grants streaming access.

## Current implementation status

The main checkout now builds a self-contained, hash-pinned Agent package and provides an authenticated admin command when `AGENT_INSTALL_BASE_URL`, package URL/hash, HTTPS Controller URL and trust thumbprint are configured. The Controller issues a random 24-hour single-use ticket for new identities and stores only its hash. The elevated Windows PowerShell bootstrap verifies the downloaded script and package, stages them under protected Program Files, installs the Agent with its virtual service account, writes private configuration/data ACLs, and waits up to 90 seconds for an enrollment receipt. The service retries enrollment if the Controller is unavailable and deletes its private ticket after the Controller acknowledges it. If the service stops or the ticket expires, the installer can verify the same service, package, configuration and identity directory, replace only the private ticket, and resume the service. The machine remains pending until an administrator sets its name and price and approves it.

The service install path has not been exercised on a client PC as part of automated checks. The current automated verification passes 127 Agent tests, 135 Controller tests, PowerShell 5.1/7 installer Plan and ACL suites, a Next.js production build, and the machine route integration matrix. These checks do not install or start a Windows Service. The admin noVNC viewer and read-only WSS gateway are implemented, but an authenticated VNC server is not yet installed/configured on a client PC. Before broad rollout, provision the trusted Controller certificate on the client, run the copied command on a disposable client, verify the new pending machine and fingerprint in the admin page, and check ticket renewal/restart behavior. The installer still does not operate Reboot Restore Rx. Rental readiness remains blocked until boot restoration proof, Sunshine stream fencing, the VNC server/firewall setup and hardware acceptance are completed.
