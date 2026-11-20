import type Database from "better-sqlite3";
import { getAuth } from "@/lib/auth";

/** Returns the better-auth SQLite handle used for wallet/top-up data. */
export async function topupDatabase(): Promise<Database.Database> {
  const auth = await getAuth();
  return auth.options.database as unknown as Database.Database;
}
