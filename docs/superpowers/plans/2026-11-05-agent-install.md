# Agent Install and Enrollment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Complete the admin-command-to-running-Agent-enrollment flow for Windows client machines.

**Architecture:** Build a self-contained Agent package, hash it, and serve a one-line PowerShell bootstrap command only to authenticated admins. The installer validates URLs and hashes, writes a protected configuration, installs and starts the Agent under its restricted virtual service account, waits up to 90 seconds for enrollment, then opens the admin machines page. The runtime remains notReady until boot and stream proof are implemented.

**Tech Stack:** Next.js 16 Route Handlers, TypeScript/Node, PowerShell 5.1, .NET 10 Windows Service.

**Spec:** docs/superpowers/specs/2026-11-05-agent-install-design.md

## Global Constraints

- Controller URL is HTTPS; HTTP is allowed only for loopback site/package URLs in local development.
- SHA-256 and trusted root thumbprint are required; no installer command is exposed unless all environment values validate.
- Existing Windows service names, non-empty data directories, reparse points, hard-linked files, or untrusted ACLs cause a safe refusal.
- Agent runs under NT SERVICE\KamenRaider Agent with protected ProgramData and configuration ACLs.
- Agent may enroll and report notReady, but cannot be rented before boot proof and a real stream fence are qualified.
- Reboot Restore Rx is externally configured and never invoked or modified.
- Automated verification may use isolated temp files and Plan mode only; it must not install/start a Windows service on the developer host.

## Review Focus

1. Malicious or malformed environment URLs must not produce a command or PowerShell injection; test quotes, credentials, queries, fragments, non-loopback HTTP, and invalid hashes.
2. Package/download hash mismatch must fail before extraction or service mutation; test mismatch and missing package content.
3. Existing service/data files and ACL/path attacks must be rejected without modifying them; test existing data, reparse paths, and rollback behavior.
4. Windows PowerShell 5.1 must not depend on PowerShell 7-only variables or command parameters; exercise script parsing and mocked/local validation paths.
5. Controller unreachable during enrollment must leave a secure retrying service and show pending state without reporting a successful enrollment receipt.

---

### Task 1: Build and hash the Agent package

**Files:**
- Create: scripts/agent/Build-AgentPackage.ps1
- Modify: .gitignore
- Test: scripts/agent/Test-AgentPackagePlan.ps1
- Test: scripts/agent/Test-AgentPackageBuild.ps1

**Interfaces:**
- Produces: Build-AgentPackage.ps1 -OutputDirectory <absolute-path> [-Runtime win-x64] [-Plan] publishes Agent files plus Install-AgentPackage.ps1, Install-Service.ps1, PrivateInstallFiles.ps1, and ServiceInstallTransaction.ps1 into AgentPackage.zip, then prints the archive SHA-256.
- Plan mode is read-only and must not create the output directory.

- [x] Step 1: Add Plan and package-build tests for absolute paths, runtime, portable ZIP paths, required files, matching SHA-256, and no Plan-mode filesystem mutation.
- [x] Step 2: Observe RED: Plan test could not resolve the missing builder; Windows PowerShell 5.1 package test then found backslash ZIP paths and a missing compression assembly reference.
- [x] Step 3: Implement self-contained win-x64/win-arm64 publish, verified TEMP staging cleanup, zip creation, SHA-256 output, and ignore generated archives.
- [x] Step 4: Run Plan and package-build tests in Windows PowerShell 5.1 and PowerShell 7.6.5. Both builds produced a TEMP ZIP with forward-slash entries, all required files, and a matching SHA-256. No services were changed.
- [x] Step 5: Commit package builder.

### Task 2: Install and start the Agent safely

**Files:**
- Modify: scripts/agent/Install-AgentPackage.ps1
- Modify: scripts/agent/Install-Service.ps1
- Modify: scripts/agent/ServiceInstallTransaction.ps1
- Modify: scripts/agent/Test-ServicePlans.ps1
- Create: scripts/agent/Test-AgentPackageInstallPlan.ps1

**Interfaces:**
- Consumes: package layout from Task 1 and the existing PrivateInstallFiles ACL/link/reparse checks.
- Produces: Install-AgentPackage.ps1 -ControllerUrl <HTTPS-origin> -TrustedRootThumbprint <40 hex> [-NetworkAdapterId <GUID>] [-InstallReturnUrl <URL>] [-Plan]. Runtime installation writes appsettings.json with ControllerUrl, DataDirectory, MoonlightPort, SunshinePort, NetworkAdapterId; invokes Install-Service.ps1 with -StartAfterInstall; polls enrollment-receipt.json for at most 90 seconds; and opens the validated return URL.
- Install-Service.ps1 adds -StartAfterInstall; for Agent the service uses startup=auto, service recovery, protected config/data ACLs, and starts only after successful setup. Plan mode remains mutation-free.

- [ ] Step 1: Add failing plan/config tests for Windows PowerShell compatibility, network adapter selection, valid local/HTTPS URLs, config values, auto startup, and receipt timeout policy.
- [ ] Step 2: Run tests to observe the disabled installer and missing config plan behavior.
- [ ] Step 3: Implement config staging, adapter selection, startup, enrollment wait, browser return, and rollback of only the service created by this invocation.
- [ ] Step 4: Add failure tests for invalid URLs, existing service/data, hash mismatch, configuration publication failure, and service start failure; verify no service is created in Plan mode.
- [ ] Step 5: Run all script tests using temporary directories; do not run live installation.
- [ ] Step 6: Commit installer.

### Task 3: Complete the admin bootstrap command

**Files:**
- Modify: src/app/api/admin/agent-install-command/route.ts
- Test: tests/machines.test.mjs
- Modify: tests/run-machines.mjs

**Interfaces:**
- Consumes: AGENT_INSTALL_ENABLED, AGENT_INSTALL_BASE_URL, AGENT_PACKAGE_URL, AGENT_PACKAGE_SHA256, AGENT_CONTROLLER_URL, and AGENT_TRUST_ROOT_THUMBPRINT.
- Produces: authenticated GET /api/admin/agent-install-command returns no-store JSON; 401/403 for non-admins; when configured returns one PowerShell command that downloads the bootstrap, verifies its SHA-256, invokes it with package/controller/trust/return URL values, and removes its temp script. HTTP loopback is allowed only for local development URLs; ControllerUrl remains HTTPS.

- [ ] Step 1: Add integration tests for logged-out/user/admin sessions and configured/unconfigured URL/hash cases.
- [ ] Step 2: Run tests to verify new route validation cases fail.
- [ ] Step 3: Generate a PowerShell 5.1-compatible command; include -UseBasicParsing and quote all generated arguments safely.
- [ ] Step 4: Verify disallowed scheme, credentials, query, fragment, bad hash, or missing environment values return unavailable/deny instead of a command.
- [ ] Step 5: Run route and admin-machine integration tests plus lint/type checks.
- [ ] Step 6: Commit route.

### Task 4: Admin install-to-pending handoff

**Files:**
- Modify: src/components/admin/machines-manager.tsx
- Modify: scripts/agent/Bootstrap-Agent.ps1
- Modify: docs/full-rental-roadmap.md

**Interfaces:**
- Consumes: Task 3 command response and install return URL.
- Produces: the admin page shows a copyable elevated-PowerShell command and clear setup-unavailable status; the client script prints enrolled MachineId/fingerprint or a retrying/pending message and opens the configured /admin/machines URL after install setup.

- [ ] Step 1: Add route/flow tests for no command when disabled and correct status for successful or pending enrollment.
- [ ] Step 2: Implement script validation that accepts HTTPS plus loopback HTTP only where specified, compatible with Windows PowerShell 5.1.
- [ ] Step 3: Keep the page's no-store polling and ensure it surfaces the new pending machine without claiming readiness.
- [ ] Step 4: Run installer plan/private ACL tests, admin-route tests, Agent/Controller suites, and app lint/type checks.
- [ ] Step 5: Update roadmap status with the exact remaining physical/hardware gates, then commit.
