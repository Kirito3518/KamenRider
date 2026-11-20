# Admin console

`/admin` uses the installed `@shadcn-space/sidebar-01` layout, adapted for KamenRaiderPlay with a responsive, collapsible sidebar, active navigation, light/dark theme switch and account dropdown.

Every admin page shares the `/admin/users` proportions: a full-width page column with 16px gaps, a compact icon + `text-xl` header, 4-up flat stat cards and `rounded-2xl` border card surfaces (shared in `src/components/admin/admin-page.tsx`). Page-specific controls stay in each page header's action area.

- `/admin`: live account counts, admin counts, total member balance, and the five newest members.
- `/admin/users`: live statistics, registration chart, date presets/custom ranges, server-side search/filter/sort and pagination (20 per page), member details, balance adjustments and role management.
- `/admin/machines`: fleet dashboard for the machine lifecycle — stats cards, a shadcn `Collapsible` pending-approval section (machine name plus approve button only), and an approved-machine table. Display name, hourly price, ports, VNC target reachability, Agent status and readiness diagnostics live in category tabs inside one settings dialog; the Agent install-command flow is a dialog too. The hourly price is entered as a whole-baht amount and stored as satang (`baht × 100`). The install command is only requested on an explicit click, and the VNC target probe gates explicit saves through `admin-vnc-target-drafts.mjs`.
- `/admin/pc-control`: live control of every registered machine (see `pc-control.md`).
- `/admin/rentals`: rental dashboard with stat cards, revenue/count charts, date-range/machine/status filters, sortable paginated table and details. Rental display states include active, completed, expired and cancelled (derived from Controller lease facts and payment records); it reads the latest 500 Controller leases, settles prorated refunds for ended rentals, and falls back to payment records with a warning when the Controller is unreachable.
- `/admin/topups`: top-up dashboard with six stat cards (requested amount, total, completed, awaiting, failed, credited), channel-stacked amount/count charts, date/channel/status filters, sortable paginated table, and a detail dialog with receiver snapshot, QR/slip images, verification attempts and provider response excerpts.
- `/admin/settings/topup`: top-up settings for TrueMoney, PromptPay QR, bank accounts/logos and rail exclusivity, EasySlip/SlipOk credentials with masked keys and connection tests, and coupon messaging. API keys are encrypted at rest and never returned to the browser. See [topup.md](topup.md) for the full contract.

The registration chart uses shadcn Chart (Recharts), site theme tokens, integer count ticks and tooltips. Registration timestamps are normalized from Better Auth ISO strings or legacy epoch-millisecond values before filtering and grouping.
- `/admin/balances`: balances for up to 50 newest accounts, plus the total across all accounts.
- Sidebar shortcuts return to the public website, account settings and rental browser.

Overview and balances pages are read-only. Member management supports manual credit/debit adjustments and role changes with a required reason, saved transactionally in `adminMemberAudit`. Mutation endpoints check origin and current admin access; the transaction rechecks the actor's role. Retrying the same UUID and payload does not repeat a change. Overdrafts, unsafe amounts and self-demotion are rejected. Rental charges and prorated refunds are performed by the rental system (see `rental.md`) and are not written to `adminMemberAudit`; online top-ups are handled by the top-up system (see `topup.md`) and do not use manual adjustments. These member pages have no admin-created booking and no machine-control operation.

`/admin/machines` owns machine identity and configuration (display name, hourly price, ports, VNC target address), approval/rejection with a typed fingerprint, Agent installation and Agent update/recovery. Live machine control, rentals, renter details and VNC tickets/viewers belong to `/admin/pc-control`; rental history and money belong to `/admin/rentals`; the machines page only links to them and never calls PC Control endpoints (`/api/admin/pc-control`, `/wake`, `/vnc-ticket`, `/power-off`, `/pairing`, `/lease-*`). Its VNC target probe (`/api/admin/vnc-target-check`) is a private-LAN reachability check that creates no ticket or session. `tests/machines-manager.test.tsx` covers the dashboard interactions and asserts this PC Control boundary.

Registration statistics use Bangkok calendar dates. The period selector controls statistics/chart; the table has separate search/role/provider/link filters. Social counts represent distinct users, not linked account counts. Details show the five most recent admin changes. Original signup method, rental count and rejected-payment count are not invented from unavailable data.

Only users whose current database role is exactly `admin` can enter. Guests redirect to `/login`; ordinary members redirect to `/`. Authorization is checked in the layout and again in the server data-access function. Admin data is never cached across requests. Passwords, provider tokens and session tokens are not selected by admin queries.

The public account dropdown displays “จัดการระบบ” only for an admin. An authorized database operator must explicitly assign the first admin; the application does not automatically elevate an existing user or a newly registered account.

`pnpm test:auth` checks guest/member denial on every admin route, actual admin access and server enforcement when a logged-in admin's role is revoked. Tests use only their own temporary database.
