# Authentication

## Scope

Implement real authentication for the existing Thai UI: username/password registration and login, Google and Discord login, persistent sessions, logout, and server-side protection for `/my-machines` and a new `/account` page. Remove all password-reset UI and do not configure a password-reset endpoint or email delivery.

## Approach

Use the installed Better Auth and better-sqlite3 packages. Store users, hashed passwords, sessions, and provider accounts in a persistent local SQLite database. Reuse existing UI components. Replacing this stack with a hosted authentication service introduces an unnecessary external dependency; custom authentication would duplicate security-sensitive library functionality.

## Account linking

One user may own a credential account plus Google and Discord accounts. Show linked providers in `/account`; authenticated users can explicitly link or unlink them. Allow different provider emails for explicit linking. Disable implicit linking by matching email. A provider account already owned by another user must not be transferred. Keep the library's protection against unlinking the last sign-in method. Use the library's OAuth state and session validation.

## Configuration and boundaries

Require a persistent secret and canonical application URL for production. Google and Discord require externally issued OAuth client credentials and registered callback URLs. Expose only provider availability to the browser, never secrets. Unconfigured providers should be visibly unavailable. Include an environment template and setup documentation. SQLite requires persistent writable storage and a Node server deployment.

Rental and machine information remains demo data: authentication does not implement rental ownership, booking, billing, or machine control. Do not imply demo machines are real user rentals.

## Validation

Check registration, duplicate username/email rejection, invalid password, session persistence and logout, unauthenticated access, origin protection, account-list authorization, and last-method unlink protection. Run TypeScript, lint, and production build. Live Google/Discord authorization requires the user's provider credentials; explicitly report if it cannot be exercised.
