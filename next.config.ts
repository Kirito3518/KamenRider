import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  /* config options here */
  distDir: process.env.NEXT_DIST_DIR || ".next-local",
  cacheComponents: true,
  partialPrefetching: true,
  reactCompiler: true,
  // SQLite databases, WAL files and runtime locks are machine-local and must
  // never be pulled into a production trace (a locked DB would abort the build).
  outputFileTracingExcludes: {
    "/*": ["./data/**/*", "./data/*"],
    "/**": ["./data/**/*", "./data/*"],
  },
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
