# Task 10 brief — VNC Controller source allowlist propagation

## Source and constraints

- Source: `docs/full-rental-roadmap.md` §§2, 8, and `docs/superpowers/plans/2026-11-06-tightvnc-agent-install.md`.
- Use main checkout `master`; preserve unrelated mixed dirty hunks. Do not install TightVNC on this development host. Do not mutate services, firewall, Sunshine, or Reboot Restore Rx.
- Read relevant installed Next.js guides before editing Next code.

## Required behavior

- The admin route only issues a command when install values and the VNC source are valid.
- `AGENT_VNC_ALLOWED_SOURCE_IP` must be one canonical RFC1918 IPv4 source address as observed by the child PC. Reject loopback (including all 127/8), public, malformed, CIDR, multicast, and IPv6.
- When Controller URL is localhost/loopback or DNS, require an explicit source IP. Never guess 127.0.0.1. A private literal IPv4 Controller URL can supply its exact address if no explicit source is configured.
- The chosen source flows through admin command → bootstrap → Agent installer → TightVNC installer. Windows Firewall rule remains inbound TCP 5900, Private profile, exact single remote source.
- Plan mode stays mutation-free; preserve PowerShell 5.1 compatibility. No package/service/firewall changes on the development host.
- Test fixtures must be deterministic when `.env.local` and host environment contain `AGENT_VNC_ALLOWED_SOURCE_IP`.

## Verification

- Run `pnpm test:machines` and explain/fix the prior `configured-vnc-source` unavailable-case failure without weakening a real configuration check.
- Run the relevant PowerShell source, Bootstrap, Agent package install-plan, and TightVNC plan tests under Windows PowerShell 5.1 and PowerShell 7 when available; run TypeScript.
- Preserve VNC target-draft UI changes and all Controller gateway/session/backup changes for separate tasks.
