# Authentication setup

The app uses Better Auth 1.7 with a persistent SQLite database and React Hot Toast.

## Local development

Run `pnpm dev`. Username/password registration and login work without an external service. On the first auth request, the app creates the database and required tables under `data/`, plus a random development secret at `data/.auth-secret`. This folder is ignored by Git. Keep it to preserve accounts and sessions across restarts.

To configure providers, copy `.env.example` to `.env.local` and fill in the values. Restart the server after changing configuration. `BETTER_AUTH_URL` must match the browser origin, including the port.

## Google and Discord

Create OAuth applications in Google Cloud Console and Discord Developer Portal. Put the client IDs and secrets in the corresponding environment variables. Register these redirect URIs (replace the origin for production):

- Google: `http://localhost:3000/api/auth/callback/google`
- Discord: `http://localhost:3000/api/auth/callback/discord`

Google needs email/profile access. Discord needs identify/email access. Complete the provider's consent/testing configuration so intended users can authorize the application. Without both credentials for a provider, its buttons stay disabled.

Users can register using a username and password or a configured provider. After login, visit `/account` to link Google and Discord to that same user, even with different email addresses. An existing account belonging to a different user cannot be transferred. Same-email OAuth sign-in does not merge existing users: sign in with the original method first, then link explicitly. Users cannot unlink their last login method. Password reset is intentionally absent.

Provider-only users sign in using a linked provider; adding a password to those users is outside this implementation.

## Account dropdown and profile images

The navbar account dropdown shows the user's avatar, name and email, with links to account settings and machines, plus logout with toast feedback. It works on desktop and mobile and supports keyboard navigation.

Users without a photo receive one of 12 bundled Bottts SVG images, in a fixed shuffled rotation. The assigned image is saved in the user row; a SQLite counter keeps the rotation across restarts. Existing users missing photos are filled in on startup. Provider images are preserved at signup. Linking a provider syncs its name and photo while preserving the local email and username. A provider that omits its photo cannot erase an existing image. Failed external images fall back to a stable bundled avatar. No avatar API is contacted by users' browsers for the bundled images; see `public/avatars/CREDITS.md` for sources.

## Production

Users have a server-controlled `role` field: `user` (member) or `admin` (administrator). Both existing and new accounts default to `user`. Signup, profile updates and provider synchronization cannot change it. The first admin must be assigned explicitly by a trusted operator. Admins can then manage other members' roles at `/admin/users`, with a required reason and audit history. See `docs/admin.md` for access controls.

Each user's `balanceSatang` is an integer amount in satang (100 satang = 1 THB), defaulting to zero. The account page and dropdown display baht. Public signup/profile APIs cannot change it. Only authenticated admins can manually credit/debit through the audited member-management endpoint. No payment gateway or rental deduction flow is included.

Set a random `BETTER_AUTH_SECRET` of at least 32 characters and `BETTER_AUTH_URL` to the public HTTPS origin. Generate a secret with the command in `.env.example`. Preserve the secret: OAuth tokens are encrypted with it. Set `AUTH_DATABASE_PATH` to a persistent writable SQLite file, outside public directories. Schema additions are applied using Better Auth's migration generator on first request; back up the database before upgrading the auth package. Run a single Node server backed by persistent local storage. Ephemeral hosting and separate SQLite copies across multiple servers will not share users or sessions.

Use HTTPS, retain database backups, and restrict access to the database and secret files. Rate limits are stored in SQLite and enabled in development and production. Configure the reverse proxy to replace forwarded IP headers, rather than accepting client-supplied values.

`/account` and `/my-machines` validate the session on the server. Machine/rental data remains a labeled demonstration, not actual user rentals.

## Verification

Run `pnpm exec tsc --noEmit`, `pnpm lint`, and `pnpm build`.

The test runner uses native TypeScript support in Node.js 22.18+ (verified with Node.js 24).

After building, `pnpm test:auth` starts a temporary localhost production server with a fresh secret and isolated test database, runs the checks, then stops the server. Port 3001 must be free. The test-only database path is printed for inspection. Provider account fixtures test multiple-provider listing and unlink ownership; they do not simulate OAuth consent.

Run the HTTP integration checks against a separate test server/database, because they create test users:

```powershell
$env:BETTER_AUTH_URL='http://localhost:3001'
$env:BETTER_AUTH_SECRET=(node -e "console.log(require('crypto').randomBytes(48).toString('base64url'))")
$env:AUTH_DATABASE_PATH='data/auth-test.sqlite'
pnpm start --port 3001
# In another terminal:
$env:TEST_AUTH_URL='http://localhost:3001'
node --test tests/auth.test.mjs
```

The checks exercise the real HTTP endpoints, hashed-password login, session revocation, duplicate identities, origin checks, account authorization and final-method unlink protection. Provider authorization and callback round trips require real provider credentials and interactive consent.

References: [Better Auth account linking](https://better-auth.com/docs/concepts/users-accounts), [configuration options](https://better-auth.com/docs/reference/options).
