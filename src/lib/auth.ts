import { betterAuth, type BetterAuthOptions } from "better-auth";
import { username } from "better-auth/plugins";
import { getMigrations } from "better-auth/db/migration";
import Database from "better-sqlite3";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { randomBytes } from "node:crypto";
import { connection } from "next/server";
import { createAvatarAllocator } from "@/lib/avatar-assignment";
import { defaultAvatars } from "@/lib/avatars";
import { roleField } from "@/lib/auth-schema";

export const enabledProviders = {
  google: !!(process.env.GOOGLE_CLIENT_ID && process.env.GOOGLE_CLIENT_SECRET),
  discord: !!(
    process.env.DISCORD_CLIENT_ID && process.env.DISCORD_CLIENT_SECRET
  ),
};

// Initialize only at request time so builds never create databases or need secrets.
let instance: ReturnType<typeof initialize> | undefined;
function initialize() {
  const production = process.env.NODE_ENV === "production";
  let secret = process.env.BETTER_AUTH_SECRET;
  const baseURL = process.env.BETTER_AUTH_URL || "http://localhost:3000";
  if (
    production &&
    (!secret || secret.length < 32 || !process.env.BETTER_AUTH_URL)
  ) {
    throw new Error(
      "Set BETTER_AUTH_URL and BETTER_AUTH_SECRET (at least 32 characters) before starting production.",
    );
  }
  const dbPath = resolve(
    /* turbopackIgnore: true */ process.env.AUTH_DATABASE_PATH ||
      "data/auth.sqlite",
  );
  mkdirSync(dirname(dbPath), { recursive: true });
  if (!secret) {
    const secretPath = resolve(dirname(dbPath), ".auth-secret");
    try {
      secret = readFileSync(secretPath, "utf8");
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
      try {
        writeFileSync(secretPath, randomBytes(48).toString("base64url"), {
          flag: "wx",
          mode: 0o600,
        });
      } catch (writeError) {
        if ((writeError as NodeJS.ErrnoException).code !== "EEXIST")
          throw writeError;
      }
      secret = readFileSync(secretPath, "utf8");
    }
  }
  const database = new Database(dbPath);
  database.pragma("journal_mode = WAL");
  database.pragma("foreign_keys = ON");
  const assignAvatar = createAvatarAllocator(database, defaultAvatars);
  const options = {
    appName: "KamenRaiderPlay",
    baseURL,
    secret,
    database,
    user: {
      additionalFields: {
        balanceSatang: { type: "number", required: true, defaultValue: 0, input: false },
        role: roleField,
      },
    },
    emailAndPassword: {
      enabled: true,
      minPasswordLength: 8,
      maxPasswordLength: 128,
    },
    socialProviders: {
      ...(enabledProviders.google
        ? {
            google: {
              clientId: process.env.GOOGLE_CLIENT_ID!,
              clientSecret: process.env.GOOGLE_CLIENT_SECRET!,
            },
          }
        : {}),
      ...(enabledProviders.discord
        ? {
            discord: {
              clientId: process.env.DISCORD_CLIENT_ID!,
              clientSecret: process.env.DISCORD_CLIENT_SECRET!,
            },
          }
        : {}),
    },
    account: {
      encryptOAuthTokens: true,
      accountLinking: {
        enabled: true,
        disableImplicitLinking: true,
        allowDifferentEmails: true,
        allowUnlinkingAll: false,
        updateUserInfoOnLink: true,
      },
    },
    session: { expiresIn: 60 * 60 * 24 * 7, updateAge: 60 * 60 * 24 },
    databaseHooks: {
      user: {
        create: { before: async user => ({ data: { ...user, image: assignAvatar(user.image) } }) },
        // A provider with no photo must not erase an existing profile image.
        update: { before: async user => ({ data: { ...user, image: user.image || undefined } }) },
      },
    },
    rateLimit: { enabled: true, storage: "database", window: 60, max: 100 },
    plugins: [username()],
  } satisfies BetterAuthOptions;
  return getMigrations(options).then(async migration => {
    await migration.runMigrations();
    database.transaction(() => {
      const users = database.prepare("SELECT id FROM user WHERE image IS NULL OR trim(image) = '' ORDER BY createdAt, id").all() as { id: string }[];
      const update = database.prepare("UPDATE user SET image = ? WHERE id = ?");
      for (const user of users) update.run(assignAvatar(null), user.id);
    })();
    return betterAuth(options);
  });
}

export async function getAuth() {
  await connection();
  instance ??= initialize();
  return instance;
}
