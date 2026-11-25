import path from "node:path";
import Database from "better-sqlite3";

/**
 * Temporary compatibility bridge for Controllers that predate `LeaseDto.paymentId`.
 *
 * The money layer identifies a rental payment by the Controller create idempotency
 * key (`rental_payments.id`), while an older Controller only exposes its own random
 * lease id. Until the updated Controller is deployed (it can only be restarted on a
 * fresh database because existing databases enter restore quarantine), the web
 * process reads the Controller's own `lease_requests` table read-only to recover the
 * mapping. No Controller row is modified from here.
 *
 * Remove this module and its call sites once every Controller returns `paymentId`.
 */
const DEFAULT_TTL_MS = 1_500;

type MappingCache = {
  path: string;
  at: number;
  byLease: Map<string, string>;
};

let cache: MappingCache | null = null;
let failureAt = 0;
let failurePath = "";
const FAILURE_TTL_MS = 1_500;

function controllerDatabasePath() {
  const configured = process.env.CONTROLLER_DATABASE_PATH?.trim();
  return configured
    ? path.resolve(configured)
    : path.resolve(process.cwd(), "data", "controller.sqlite");
}

function readMapping(databasePath: string): Map<string, string> {
  const database = new Database(databasePath, {
    readonly: true,
    fileMustExist: true,
    timeout: 1_000,
  });
  try {
    const rows = database
      .prepare(
        "SELECT rental_id, request_key FROM lease_requests WHERE scope LIKE 'create:%'",
      )
      .all() as { rental_id: string; request_key: string }[];
    const byLease = new Map<string, string>();
    for (const row of rows) {
      if (typeof row.rental_id === "string" && typeof row.request_key === "string")
        byLease.set(row.rental_id, row.request_key);
    }
    return byLease;
  } finally {
    database.close();
  }
}

/** Read-only lease id -> payment id map from the Controller database. */
export function readLegacyLeasePaymentIds(
  options: { databasePath?: string; now?: number; fresh?: boolean } = {},
) {
  const databasePath = options.databasePath ?? controllerDatabasePath();
  const now = options.now ?? Date.now();
  if (
    !options.fresh &&
    cache !== null &&
    cache.path === databasePath &&
    now - cache.at < DEFAULT_TTL_MS
  )
    return cache.byLease;
  if (
    !options.fresh &&
    failurePath === databasePath &&
    now - failureAt < FAILURE_TTL_MS
  )
    return cache?.byLease ?? new Map<string, string>();

  try {
    const byLease = readMapping(databasePath);
    cache = { path: databasePath, at: now, byLease };
    return byLease;
  } catch {
    // The Controller may be between restarts or using another database path. Refunds
    // stay pending and are retried on the next customer read.
    failureAt = now;
    failurePath = databasePath;
    return cache?.path === databasePath ? cache.byLease : new Map<string, string>();
  }
}

/**
 * Fills `paymentId` on Controller leases returned by an older build. Leases that
 * already report the mapping from the Controller are returned untouched.
 */
export function fillLegacyLeasePaymentIds<T extends { id: string; paymentId?: string | null }>(
  leases: T[],
  options: { fresh?: boolean } = {},
): T[] {
  if (!leases.some((lease) => !lease.paymentId)) return leases;
  const byLease = readLegacyLeasePaymentIds({ fresh: options.fresh });
  if (byLease.size === 0) return leases;
  return leases.map((lease) =>
    lease.paymentId
      ? lease
      : { ...lease, paymentId: byLease.get(lease.id) ?? null },
  );
}

/** Resolve one lease id for cancel/extend routes that only have the URL id. */
export function legacyPaymentIdForLease(leaseId: string): string | null {
  return readLegacyLeasePaymentIds({ fresh: true }).get(leaseId) ?? null;
}
