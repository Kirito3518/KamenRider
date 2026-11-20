/** DB aggregates for the admin rental dashboard (payment table is the money source). */

import type Database from "better-sqlite3";
import { enumerateRangeBuckets, type ResolvedDateRange } from "@/lib/date-range";
import type { RentalChartPoint } from "@/lib/rental-history";
import { ensureRentalSchema } from "@/lib/rental-payments";

export function readRentalRevenue(
  database: Database.Database,
  start: number,
  end: number,
): { revenueSatang: number; rentalCount: number } {
  ensureRentalSchema(database);
  const row = database
    .prepare(
      `SELECT
         COALESCE(SUM(amount_satang + extension_satang - refunded_satang), 0) AS revenueSatang,
         count(*) AS rentalCount
       FROM rental_payments
       WHERE status = 'confirmed' AND created_at >= ? AND created_at < ?`,
    )
    .get(start, end) as { revenueSatang: number; rentalCount: number };
  return {
    revenueSatang: Number(row.revenueSatang) || 0,
    rentalCount: Number(row.rentalCount) || 0,
  };
}

function patternFor(bucket: "hour" | "day" | "month") {
  if (bucket === "hour") return "%H";
  if (bucket === "month") return "%Y-%m";
  return "%Y-%m-%d";
}

export function readRentalChart(
  database: Database.Database,
  range: ResolvedDateRange,
): RentalChartPoint[] {
  if (range.start === null || range.end === null) return [];
  ensureRentalSchema(database);
  const pattern = patternFor(range.bucket);
  const rows = database
    .prepare(
      `SELECT strftime(?, created_at / 1000, 'unixepoch', '+7 hours') AS bucket,
              COALESCE(SUM(amount_satang + extension_satang - refunded_satang), 0) AS revenueSatang,
              count(*) AS count
       FROM rental_payments
       WHERE status = 'confirmed' AND created_at >= ? AND created_at < ?
       GROUP BY bucket`,
    )
    .all(pattern, range.start, range.end) as {
    bucket: string;
    revenueSatang: number;
    count: number;
  }[];
  const byKey = new Map(rows.map((row) => [row.bucket, row]));
  return enumerateRangeBuckets(range.start, range.end, range.bucket).map((bucket) => {
    const row = byKey.get(bucket.key);
    return {
      key: bucket.key,
      label: bucket.label,
      revenueSatang: Number(row?.revenueSatang) || 0,
      count: Number(row?.count) || 0,
    };
  });
}
