import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import Database from "better-sqlite3";
import { afterEach, describe, expect, it } from "vitest";
import {
  fillLegacyLeasePaymentIds,
  legacyPaymentIdForLease,
  readLegacyLeasePaymentIds,
} from "@/lib/legacy-lease-payments";

let directories: string[] = [];
let previousPath: string | undefined;

function fixture(rows: { requestKey: string; rentalId: string }[]) {
  const directory = mkdtempSync(join(tmpdir(), "kr-legacy-lease-"));
  directories.push(directory);
  const databasePath = join(directory, "controller.sqlite");
  const database = new Database(databasePath);
  database.exec(
    "CREATE TABLE lease_requests(scope TEXT NOT NULL, request_key TEXT NOT NULL, rent_id TEXT, rental_id TEXT);",
  );
  const insert = database.prepare(
    "INSERT INTO lease_requests(scope, request_key, rental_id) VALUES (?, ?, ?)",
  );
  for (const row of rows) insert.run("create:renter-1", row.requestKey, row.rentalId);
  database.close();
  previousPath = process.env.CONTROLLER_DATABASE_PATH;
  process.env.CONTROLLER_DATABASE_PATH = databasePath;
  return databasePath;
}

afterEach(() => {
  for (const directory of directories) rmSync(directory, { recursive: true, force: true });
  directories = [];
  if (previousPath === undefined) delete process.env.CONTROLLER_DATABASE_PATH;
  else process.env.CONTROLLER_DATABASE_PATH = previousPath;
});

describe("legacy Controller lease/payment compatibility", () => {
  it("fills paymentId from lease_requests for a Controller that predates LeaseDto.paymentId", () => {
    fixture([{ requestKey: "payment-1", rentalId: "lease-aaa" }]);
    const leases: { id: string; state: string; paymentId?: string | null }[] = [
      { id: "lease-aaa", state: "completed" },
      { id: "lease-bbb", state: "completed" },
    ];
    const filled = fillLegacyLeasePaymentIds(leases, { fresh: true });
    expect(filled[0].paymentId).toBe("payment-1");
    expect(filled[1].paymentId).toBeNull();
    // The mapping table only exposes Controller ids to the money layer.
    expect([...readLegacyLeasePaymentIds({ fresh: true }).values()]).toEqual(["payment-1"]);
    expect(legacyPaymentIdForLease("lease-aaa")).toBe("payment-1");
    expect(legacyPaymentIdForLease("lease-missing")).toBeNull();
  });

  it("keeps Controller-provided paymentId and never reads the legacy mapping", () => {
    fixture([{ requestKey: "payment-1", rentalId: "lease-aaa" }]);
    const leases = [{ id: "lease-aaa", paymentId: "payment-modern" }];
    expect(fillLegacyLeasePaymentIds(leases, { fresh: true })).toEqual(leases);
  });

  it("degrades to an empty mapping when the Controller database is unavailable", () => {
    previousPath = process.env.CONTROLLER_DATABASE_PATH;
    process.env.CONTROLLER_DATABASE_PATH = join(tmpdir(), "missing-controller-db.sqlite");
    expect(readLegacyLeasePaymentIds({ fresh: true }).size).toBe(0);
    expect(fillLegacyLeasePaymentIds([{ id: "lease-aaa" }], { fresh: true })).toEqual([
      { id: "lease-aaa" },
    ]);
  });
});
