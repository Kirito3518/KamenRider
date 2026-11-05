# TightVNC Agent Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Install the selected TightVNC 2.8.90 Server with the Agent, block remote input, and permit VNC traffic only from the configured Controller IPv4 address.

**Architecture:** The admin command provides a validated Controller source IPv4 address to the downloaded bootstrap. The package includes a PowerShell installer that downloads the vendor MSI separately, validates its pinned SHA-256, Authenticode signer, and MSI metadata, generates a product-validated secure transform to hide the VNC password properties, installs Server-only, and creates a Private-profile source-restricted firewall rule. The local VNC credential is generated per machine and stored in a SYSTEM/Administrators-only file, then displayed once to the elevated installer.

**Tech Stack:** Windows PowerShell 5.1, Windows Installer, Windows Defender Firewall cmdlets, Next.js 16 route handler, Node test suite.

**Spec:** `docs/full-rental-roadmap.md` section 8 and prior user choice of TightVNC 2.8.90.

## Global Constraints

- Do not bundle the GPL TightVNC MSI in the KamenRaider package; download it separately from the official TightVNC HTTPS URL.
- Install only `ADDLOCAL=Server`, register it as a Windows service, set RFB port 5900, disable HTTP viewer, block remote input, and disable MSI-created firewall exceptions.
- Create an inbound TCP 5900 rule restricted to one validated Controller IPv4 address on the Private profile.
- Support localhost/loopback only for same-machine development; require an explicit private Controller IPv4 address for hostname-based production URLs.
- `-Plan` must not create files, install software/services, or change firewall rules.
- Require the Windows Firewall Private profile to be enabled; stage fresh installs with no registered service and deny-all RFB until the exact source rule is present. Use the Windows Installer install API for staging and its maintenance API for final configuration/repair; add both password property names to the MSI `MsiHiddenProperties` table value through a ProductCode-validated secure transform, keep that transform under the private Agent data directory, and reject any Windows Installer Debug policy value with the command-line logging bit `0x04` enabled.
- Do not operate or modify Reboot Restore Rx, and do not install VNC on the development host.

## Review Focus

- A non-loopback Controller hostname without an explicit source IP must not create a broad firewall rule; cover in the route and bootstrap tests.
- Public, multicast, wildcard, CIDR, IPv6, and malformed source addresses must be rejected; cover in bootstrap and installer plan tests.
- MSI bytes with a wrong size, invalid signature, or unexpected product/version/architecture must be rejected before `msiexec`; cover through isolated validation helpers and plan checks.
- Existing non-2.8.90 TightVNC installations must not be overwritten; cover the installer decision function with table-driven cases.
- MSI or firewall failure must not print or retain a password in a public/shared path; cover private credential ACL and cleanup decisions with temporary-only tests.

### Task 1: Pass a Controller source IPv4 address through the admin install command

**Files:**
- Modify: `src/app/api/admin/agent-install-command/route.ts`
- Modify: `public/agent/Bootstrap-Agent.ps1`
- Modify: `tests/machines.test.mjs`
- Modify: `scripts/agent/Test-BootstrapAgentPlan.ps1`

**Interfaces:** Bootstrap accepts `-VncAllowedSourceAddress`; the admin route sources it from `AGENT_VNC_ALLOWED_SOURCE_IP`, or derives a loopback/private literal IP from `AGENT_CONTROLLER_URL` for development. DNS hostnames require the env value.

- [x] Add route tests for loopback derivation, configured private IP, and missing/invalid production source configuration.
- [x] Run the focused machine route test and observe the expected failure.
- [x] Add `AGENT_VNC_ALLOWED_SOURCE_IP` validation and command-line emission; preserve request-time, uncached route behavior.
- [x] Extend bootstrap input validation and plan output, and pass the source IP to the package installer.
- [x] Run the focused Node and Windows PowerShell bootstrap tests.
- [x] Commit as `feat(agent): configure vnc controller source`.

### Task 2: Add a plan-only TightVNC 2.8.90 Server installer

**Files:**
- Create: `scripts/agent/Install-TightVncServer.ps1`
- Create: `scripts/agent/Test-TightVncInstallPlan.ps1`
- Modify: `scripts/agent/Build-AgentPackage.ps1`
- Modify: `scripts/agent/Test-AgentPackagePlan.ps1`
- Modify: `scripts/agent/Test-AgentPackageBuild.ps1`

**Interfaces:** Installer accepts `-AllowedSourceAddress`, `-DataDirectory`, and `-Plan`; plan returns the pinned upstream URL, expected MSI SHA-256, size and metadata, deny-all staging properties, final install properties, port, and firewall scope without exposing credentials. Fresh setup uses `MsiInstallProductW` for staging, then `MsiConfigureProductExW` with `ADDLOCAL=Server` after firewall setup.

- [x] Add tests for exact version/URL/size/SHA-256, the observed `OOO GlavSoft` Authenticode signer, orphan-install rejection, server-only selection, deny-all staging with no service, maintenance API selection, hidden password properties, disabled MSI firewall exception and HTTP viewer, input blocking, port 5900, Private profile and exact remote source IP, invalid addresses, and no plan-mode mutations.
- [x] Run the new test and observe failure because the installer is missing.
- [x] Implement address validation, bounded MSI download, size and Authenticode checks, MSI product metadata validation, install decision handling, and plan output.
- [x] Include the script in package build entries and assert it is packaged separately from the MSI.
- [x] Run PowerShell 5.1 and PowerShell 7 plan/package checks.
- [x] Download and inspect the pinned MSI, verify the exact signer and authored hidden-property list, and generate/apply the password-hiding transform to a temporary database copy; do not install the product on the development host.
- [x] Commit as `feat(agent): add pinned tightvnc server installer`.

### Task 3: Integrate VNC setup with first install and enrollment repair

**Files:**
- Modify: `scripts/agent/Install-AgentPackage.ps1`
- Modify: `scripts/agent/Test-AgentPackageInstallPlan.ps1`
- Modify: `scripts/agent/PrivateInstallFiles.ps1` only if the established ACL helper is sufficient for the secret file.

**Interfaces:** Fresh installation and enrollment repair invoke the package VNC installer only after a valid Agent enrollment receipt. If enrollment is still pending, VNC setup is skipped and the user is told to rerun the admin command after connectivity returns. The VNC password is generated only for a new compatible installation, saved under the protected Agent data directory with a SYSTEM/Administrators-only ACL, and written to the console once for the local administrator.

- [x] Add tests that the Agent plan exposes only VNC setup intent and source IP, never a password, that pending enrollment skips VNC, and that plan mode does not mutate VNC/service/firewall state.
- [x] Run the focused installer plan test and observe failure.
- [x] Integrate VNC setup after Agent enrollment; allow a repair run to complete a missing VNC install without replacing a valid existing installation or credential.
- [x] Ensure rollback/failed installation removes only KamenRaider-owned staging and firewall state created by that invocation; never remove a pre-existing TightVNC product.
- [x] Preserve the secure transform if MSI rollback fails, and distinguish a missing VNC service from a stopped or still-running service in repair warnings.
- [x] Run focused PowerShell installer tests under both PowerShell versions.
- [x] Commit as `feat(agent): install protected read-only vnc access` (`a10b8f0`).

### Task 4: Record VNC requirements and verify the complete software checks

**Files:**
- Modify: `.env.example`
- Modify: `docs/full-rental-roadmap.md`
- Modify: `docs/agent-acceptance.md`

- [x] Document the source IP env variable, local development behavior, 2.8.90 GPL limitations, and that no hardware test has yet passed.
- [x] Run all Agent PowerShell plan/package tests under Windows PowerShell 5.1 and PowerShell 7, plus relevant Node checks.
- [x] Confirm the development host has no new TightVNC service or firewall rule and no Reboot Restore Rx changes.

Verification note: All 11 Agent PowerShell checks exited successfully under Windows PowerShell 5.1 after the final changes. All 10 non-build checks exited successfully under PowerShell 7 after the final changes. The latest `Test-AgentPackageBuild.ps1` run in PowerShell 7 completed build, package and hash assertions, but antivirus blocked deletion of a temporary directory containing an extracted copy of `Install-AgentPackage.ps1`; package build and cleanup passed under Windows PowerShell 5.1. No antivirus exclusion was added. The pinned TightVNC MSI was downloaded and its signature, metadata, and hidden-property transform were verified without installing the product; client-machine service startup and VNC connection acceptance remain open.
- [x] Commit as `docs(agent): document tightvnc install acceptance`.
