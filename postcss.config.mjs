// The project uses Turbopack for `next dev` (see next.config.ts) and webpack for
// the production build (`pnpm build --webpack`). Both need the Tailwind v4
// pipeline; Turbopack uses the loader configured in next.config.ts, while webpack
// discovers this PostCSS config.
const config = {
  plugins: {
    "@tailwindcss/postcss": {},
  },
};

export default config;
