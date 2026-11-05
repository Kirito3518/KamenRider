# Agent onboarding from the admin page

## Intended operator flow

1. An administrator opens `/admin/machines` and copies a PowerShell install command. The web origin used in that command comes from an environment setting; `BETTER_AUTH_URL` is only a local development fallback. `localhost` works only when the browser and target Agent are on the same machine.
2. The administrator runs the command in an elevated PowerShell window on one Windows client. It downloads a versioned Agent package over HTTPS, verifies its pinned SHA-256 digest, installs the Agent service and private per-machine identity, then waits for a successful enrollment receipt. A failed download, signature/hash check, service installation or enrollment must produce an error, not a success message.
3. The Agent reports its generated identity, local IP, MAC address and initial Moonlight/Sunshine ports to the Controller. The Controller creates a **pending** machine. Re-running on the same client reuses its identity and does not create a second machine. A copied identity must not be reused on another client.
4. The admin page refreshes its machine list automatically. The new pending machine appears first. The administrator checks the identity fingerprint on the client, sets name, price and ports on the web, and then approves it. A pending machine cannot be rented.
5. After approval, the Agent receives the Controller configuration revision. Changing ports on the web only updates metadata; the operator configures Sunshine and router forwarding separately. Rental readiness remains gated by the stream fence, boot reconciliation and live health checks.

The installer does not configure or operate Reboot Restore Rx. The operator's existing restore setup remains external to this onboarding flow.

## Delivery and security requirements

- Generate the command only for an authenticated administrator. Use an expiring, single-use enrollment ticket so a copied install command cannot register unrelated machines indefinitely. Keep the ticket out of URLs and server logs where possible.
- Publish a pinned package hash with the command. Never run unverified downloaded PowerShell or executable bytes. The downloaded installer must require elevation and place binaries under a protected directory, with service identity and secrets in private ACL-protected storage.
- Show the command only when the published package, Controller URL, TLS trust and installation path are configured. Do not show a command that points to a missing package or a guarded installer.
- The package must enroll against a reachable HTTPS Controller URL. A URL chosen from an admin browser request or `localhost` on the server is not automatically reachable from client machines.
- Show installation success on the client only after the Controller acknowledges enrollment. On the web, continue with metadata and approval; do not imply that pending enrollment grants streaming access.

## Current implementation status

The Controller already accepts signed Agent enrollment and stores new machines as pending. The Agent generates a unique private identity and retries enrollment. The admin page supports name, price, ports and fingerprint approval and now polls automatically. The downloadable installer, ticket issuance, package distribution, enrollment receipt and live service installation are still required. `Install-Service.ps1` deliberately rejects live installation pending its security and recovery qualification; do not advertise an install command until that guard can be removed safely.
