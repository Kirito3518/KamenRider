# Admin console

`/admin` uses the installed `@shadcn-space/sidebar-01` layout, adapted for KamenRaiderPlay with a responsive, collapsible sidebar, active navigation, light/dark theme switch and account dropdown.

- `/admin`: live account counts, admin counts, total member balance, and the five newest members.
- `/admin/users`: up to 50 newest accounts with their profile, email and role.
- `/admin/balances`: balances for up to 50 newest accounts, plus the total across all accounts.
- Sidebar shortcuts return to the public website, account settings and rental browser.

These are read-only views. This change does not add balance adjustment, role management, payment, booking or machine-control operations. Machine availability remains demo data on the public rental page.

Only users whose current database role is exactly `admin` can enter. Guests redirect to `/login`; ordinary members redirect to `/`. Authorization is checked in the layout and again in the server data-access function. Admin data is never cached across requests. Passwords, provider tokens and session tokens are not selected by admin queries.

The public account dropdown displays “จัดการระบบ” only for an admin. An authorized database operator must explicitly assign the first admin; the application does not automatically elevate an existing user or a newly registered account.

`pnpm test:auth` checks guest/member denial on every admin route, actual admin access and server enforcement when a logged-in admin's role is revoked. Tests use only their own temporary database.
