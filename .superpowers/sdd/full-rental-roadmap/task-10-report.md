# Task 10 report — VNC Controller source allowlist

## Result

The source address is validated consistently from the admin-generated installer command through Bootstrap, the Agent package installer, and the TightVNC installer. Only a canonical RFC1918 IPv4 source is accepted. Loopback, including all `127/8`, public or malformed IPv4, CIDR, and IPv6 values are rejected. Loopback and DNS Controller URLs require an explicit `AGENT_VNC_ALLOWED_SOURCE_IP`; a private literal Controller IPv4 can supply the source when that setting is blank. The generated firewall rule remains inbound TCP 5900 on the Private profile and is scoped to that exact source address. Plan mode remains mutation-free and the PowerShell code remains compatible with Windows PowerShell 5.1.

## Prior failure and resolution

The first `pnpm test:machines` run against the existing `.next-local` build failed at `tests/machines.test.mjs:284` in `configured-vnc-source`: the `loopback-controller-without-vnc-source` case returned `true` instead of `false`. This was a stale build artifact, not environment contamination. The compiled route in `.next-local` still defaulted localhost to `127.0.0.1`, while the current source requires an explicit private source address.

To avoid the active `.next` junction used by the development server, the app was freshly built into the isolated `NEXT_DIST_DIR=.next-vnc-source-allowlist-validation` output. That build passed. Running `pnpm test:machines` against that isolated build then passed all scenarios, including the missing-source, loopback-source rejection, private-literal fallback, configured-source, and unavailable-configuration cases. No test condition was weakened and no host service, firewall rule, Sunshine, TightVNC, or Reboot Restore Rx installation was changed.

## Verification

- `pnpm test:machines` using the isolated fresh build: passed (exit 0).
- `Test-AgentVncSourceAddress.ps1`, `Test-BootstrapAgentPlan.ps1`, `Test-AgentPackageInstallPlan.ps1`, and `Test-TightVncInstallPlan.ps1` under Windows PowerShell 5.1: passed. The plan suites reported no services or MSI installed; Bootstrap reported no files outside TEMP changed.
- The same four PowerShell suites under PowerShell 7: passed.
- `pnpm exec tsc --noEmit`: passed (exit 0).

The isolated build output remains in the workspace as `.next-vnc-source-allowlist-validation`; it was not the active development build and is not part of the Task 10 commit.
