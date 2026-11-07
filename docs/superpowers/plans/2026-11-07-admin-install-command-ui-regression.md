# Admin Install Command UI Regression Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add deterministic component interaction coverage for the Admin Agent install-command expiry, copy, and explicit refresh flow.

**Architecture:** Use Vitest, React Testing Library, and jsdom to render the real `MachinesManager` client component. Intercept every fetch and clipboard write in the test so no live enrollment ticket or Controller request can occur.

**Tech Stack:** Existing Next.js 16.4.3, React 19.3.0, TypeScript, plus development-only Vitest, Vite React plugin, jsdom, React Testing Library, DOM Testing Library, and tsconfig-paths plugin.

**Spec:** `docs/superpowers/specs/2026-11-07-admin-install-command-ui-design.md`; project scope remains `docs/full-rental-roadmap.md`.

## Global Constraints

- Do not change production component behavior.
- Mock every `fetch` call; never call `/api/admin/agent-install-command` on the running app or Controller because it issues a real enrollment ticket.
- Keep the test limited to Admin install-command display, expiry, copy, refresh, focus, and visibility behavior.
- Do not install services or change machine, firewall, or Sunshine state; do not read or modify Reboot Restore Rx.
- Work in the authorized main checkout on `master`; preserve existing ignored package/build artifacts and `.env.local`.

## Review Focus

- Initial display: assert the literal command and ISO UTC expiry returned by the mock are shown, and copy is enabled before the deadline.
- Expiry boundary: set the clock exactly to the mocked deadline, trigger a focus update, and assert the copy button disables.
- Explicit refresh: only clicking the refresh button may request and display the second mocked command.
- Passive events: focus and visibility changes must not issue another install-command request.
- Network isolation: fail the test on any fetch URL other than the two component endpoints and assert all route responses are supplied by mocks.

---

### Task 24: Test Admin install-command interactions

**Files:**
- Modify: `package.json` — add the UI test script and development-only test dependencies.
- Modify: `pnpm-lock.yaml` — record the test tool packages.
- Create: `vitest.config.ts` — configure React, jsdom, and TypeScript path aliases.
- Create: `tests/machines-manager.test.tsx` — exercise the real component with mocked network, clock, and clipboard boundaries.

**Interfaces:**
- Consumes: `MachinesManager` from `src/components/admin/machines-manager.tsx` and the current install-command API response (`available`, `command`, `expiresAtUtc`).
- Produces: a reusable UI test command included in `pnpm test:machines`.

- [ ] Add Vitest/React Testing Library/jsdom development dependencies and a config using the installed Next guide's React plugin and `vite-tsconfig-paths`.
- [ ] Add a component test that sets the test clock to `2026-11-07T12:00:00.000Z`, mocks `/api/admin/machines` with an empty list, and mocks `/api/admin/agent-install-command` with `first-test-command` expiring at `2026-11-07T12:34:56.789Z` followed by `second-test-command` expiring at `2026-11-07T13:34:56.789Z`; unexpected URLs must throw.
- [ ] Assert literal UTC expiry display, copy success before expiry, disabled copy at the exact deadline after a focus event, explicit refresh updating the displayed command, and no extra install-command requests after focus/visibility events.
- [ ] Add the new test to `pnpm test:machines` without changing existing integration coverage.
- [ ] Run the new component test, `pnpm test:machines`, `pnpm exec tsc --noEmit`, and `pnpm lint`; record actual results and any existing warnings.
- [ ] Commit the task with message `test(admin): cover install command interactions`.
