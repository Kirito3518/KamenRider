# Admin member management

User approved matching the supplied screenshot with existing site tokens, including real balance adjustment and role changes.

- Add live member statistics, a Bangkok-time registration histogram and date presets/custom dates.
- Add server-side search, role/provider/link filters, date sorting and pagination; show real provider identifiers and account data.
- Add member detail and balance/role dialogs with Thai toast feedback, using existing shadcn components.
- Protect mutation endpoints with origin/session/admin checks; validate integer satang amounts, disallow overdrafts/self-demotion, make retries idempotent, and write audit rows transactionally.
- Verify query/date/filter behavior, write authorization/accounting/retry invariants, production build and browser flows against an isolated fixture database.

No invented rental/payment metrics. The existing full-width admin shell stays in place.

Completed with shadcn Chart/Recharts as explicitly selected by the user. Production build and eight tests passed, including real HTTP authorization/mutations, ISO/epoch timestamp normalization, query filters, ledger/idempotency and role guards. Browser checks covered light/dark chart rendering, tooltip, period selection, search, credit adjustment, role changes and audit history on an isolated fixture database. Full lint has no errors and one existing image warning in the public navbar.
