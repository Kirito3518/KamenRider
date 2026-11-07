# Admin install command interaction test design

## Intent

Protect the administrator's install-command flow from regressions in expiry, copy, and explicit refresh behavior. This is test coverage only; the production UI behavior stays unchanged.

## Goals and limits

- Render the real `MachinesManager` client component and verify the command and UTC expiry shown to an administrator.
- Verify copying works before expiry and the copy button disables at the deadline.
- Verify a replacement command is fetched only after the administrator explicitly clicks refresh; focus and visibility updates must not request another ticket.
- Mock every `fetch` call, including the initial install-command request, so no live Controller endpoint or enrollment ticket is used.
- Do not install services, alter firewall/Sunshine state, or inspect or operate Reboot Restore Rx.

## Approaches

1. Keep the existing pure-helper and route tests only. This avoids test tooling but does not cover the rendered component interactions; those tests already exist and the reviewer recorded this gap.
2. Add a browser end-to-end flow against the authenticated Admin page. This exercises more infrastructure but depends on browser/auth setup and risks reaching the ticket endpoint unless every request is intercepted.
3. Add Vitest with React Testing Library and jsdom, then render `MachinesManager` with every fetch and clipboard write intercepted. This exercises the real component deterministically without connecting to the Controller; it is the selected approach because Next.js 16's installed testing guide recommends Vitest and React Testing Library for synchronous Client Components.

## Constraints and decisions

- Use the repository's existing React and TypeScript versions; add testing packages only as development dependencies.
- Pin the test tooling to a Node-compatible version line that does not raise Next.js 16.4.3's supported Node minimum of `>=20.9.0`.
- Keep test expectations as literal commands and UTC timestamps independent of the expiry helper.
- Fail the mocked fetch on any unexpected URL, and assert install-command request counts around focus, visibility, and explicit refresh.
- The component's five-second machine-list timer is cleaned up by unmounting the rendered component; the test must not wait for or contact a real service.

## Acceptance

The component interaction test passes with the mocked initial request, successful copy before expiry, disabled copy at expiry, successful explicit refresh, and no extra install-command requests from focus/visibility events. Existing machine route tests continue to cover the authenticated response and command composition.
