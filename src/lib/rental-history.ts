/**
 * Client-safe rental-history contracts and formatting helpers.
 *
 * The server merges Controller lease facts with the rental payment records kept
 * in the auth database and returns these shapes. Nothing here touches the
 * database, so it is safe to import from client components.
 */

export type RentalHistoryState =
  | "pending"
  | "preparing"
  | "active"
  | "ending"
  | "completed"
  | "failed"
  | "rejected"
  | "unknown";

export type RentalHistoryEntry = {
  rentalId: string;
  machineId: string;
  machineName: string;
  state: RentalHistoryState;
  startsAtUtc: string | null;
  expiresAtUtc: string | null;
  endedAtUtc: string | null;
  createdAtUtc: string;
  /** Seconds the renter paid for when the lease was created. */
  bookedSeconds: number;
  /** Additional paid seconds from confirmed extensions. */
  extensionSeconds: number;
  /** Whole-baht hourly rate snapshot; null for leases created before payments. */
  hourlyPriceSatang: number | null;
  /** Amount charged for the initial booking plus extensions; null when unknown. */
  paidSatang: number | null;
  refundedSatang: number;
  /** paidSatang - refundedSatang; null when the amount is unknown. */
  netSatang: number | null;
  /** Prorated refund note from the payment record, when present. */
  refundNote?: string | null;
  /** Reason a pending rental payment was rejected/refunded, when present. */
  rejectReason?: string | null;
};

export type RentalDisplayState =
  | "active"
  | "completed"
  | "expired"
  | "cancelled"
  | "pending"
  | "unknown";

export type RentalHistorySort =
  | "newest"
  | "oldest"
  | "amount_desc"
  | "amount_asc";

export type RentalHistoryPeriod =
  | "today"
  | "7d"
  | "30d"
  | "90d"
  | "custom"
  | "all";

export type RentalHistoryFilters = {
  q: string;
  state: RentalDisplayState | "all";
  machineId: string;
  period: RentalHistoryPeriod;
  from: string;
  to: string;
  sort: RentalHistorySort;
};

export const rentalDisplayStateLabels: Record<RentalDisplayState, string> = {
  active: "กำลังเช่า",
  completed: "เสร็จสิ้น",
  expired: "หมดอายุ",
  cancelled: "ยกเลิก/ไม่สำเร็จ",
  pending: "รอยืนยัน",
  unknown: "ไม่ทราบสถานะ",
};

/**
 * Display-level state for filters and badges. The underlying Controller state is
 * kept intact; "expired" means the lease ended at/after its scheduled expiry and
 * "cancelled" covers Controller failures/rejected payments.
 */
export function rentalDisplayState(
  entry: Pick<
    RentalHistoryEntry,
    "state" | "expiresAtUtc" | "endedAtUtc"
  >,
): RentalDisplayState {
  if (entry.state === "preparing" || entry.state === "active" || entry.state === "ending") {
    return "active";
  }
  if (entry.state === "completed") {
    if (entry.endedAtUtc && entry.expiresAtUtc) {
      const ended = Date.parse(entry.endedAtUtc);
      const expires = Date.parse(entry.expiresAtUtc);
      if (Number.isFinite(ended) && Number.isFinite(expires) && ended >= expires) {
        return "expired";
      }
    }
    return "completed";
  }
  if (entry.state === "failed" || entry.state === "rejected") return "cancelled";
  if (entry.state === "pending") return "pending";
  return "unknown";
}

export function rentalDisplayStateLabel(state: RentalDisplayState) {
  return rentalDisplayStateLabels[state];
}

export function rentalDisplayStateTone(state: RentalDisplayState): RentalStateTone {
  switch (state) {
    case "active":
    case "completed":
      return "available";
    case "expired":
      return "secondary";
    case "cancelled":
      return "destructive";
    case "pending":
      return "busy";
    default:
      return "outline";
  }
}

export function rentalEntryCreatedAtMs(entry: RentalHistoryEntry): number {
  const parsed = Date.parse(entry.createdAtUtc);
  return Number.isFinite(parsed) ? parsed : 0;
}

export function rentalEntryAmountSatang(entry: RentalHistoryEntry): number {
  return entry.netSatang ?? 0;
}

export type AdminRentalHistoryEntry = RentalHistoryEntry & {
  userId: string;
  userName: string;
  userEmail: string;
};

export type RentalHistorySummary = {
  totalRentals: number;
  activeRentals: number;
  /** Gross amount charged for confirmed rentals. */
  paidSatang: number;
  refundedSatang: number;
  /** paidSatang - refundedSatang (shop revenue). */
  netSatang: number;
};

export type RentalHistoryPagination = {
  page: number;
  pageSize: number;
  total: number;
  pageCount: number;
};

export type RentalHistoryResponse = {
  balanceSatang: number;
  controllerAvailable: boolean;
  summary: RentalHistorySummary;
  entries: RentalHistoryEntry[];
  pagination: RentalHistoryPagination;
  filters: RentalHistoryFilters;
  rangeError: string;
  machines: { machineId: string; displayName: string }[];
};

export type RentalChartPoint = {
  key: string;
  label: string;
  revenueSatang: number;
  count: number;
};

export type AdminRentalStats = {
  totalRentals: number;
  activeRentals: number;
  completedRentals: number;
  expiredRentals: number;
  revenueSatang: number;
  rentalCount: number;
};

export type AdminRentalHistoryResponse = {
  controllerAvailable: boolean;
  summary: RentalHistorySummary;
  stats: AdminRentalStats;
  previous: AdminRentalStats;
  chart: RentalChartPoint[];
  chartPrevious: { revenueSatang: number; count: number };
  entries: AdminRentalHistoryEntry[];
  pagination: RentalHistoryPagination;
  filters: RentalHistoryFilters;
  rangeError: string;
  machines: { machineId: string; displayName: string }[];
};

export const rentalStateLabels: Record<string, string> = {
  pending: "กำลังยืนยันการเช่า",
  preparing: "กำลังเตรียมเครื่อง",
  active: "กำลังเช่า",
  ending: "กำลังสิ้นสุดการเช่า",
  completed: "สิ้นสุดแล้ว",
  failed: "ไม่สำเร็จ",
  rejected: "ไม่สำเร็จ (คืนเงินแล้ว)",
  unknown: "ไม่ทราบสถานะ",
};

export type RentalStateTone =
  | "default"
  | "available"
  | "busy"
  | "maintenance"
  | "secondary"
  | "destructive"
  | "outline";

export function rentalStateLabel(state: string) {
  return rentalStateLabels[state] ?? state;
}

export function rentalStateTone(state: string): RentalStateTone {
  switch (state) {
    case "active":
      return "available";
    case "preparing":
    case "ending":
    case "pending":
      return "busy";
    case "failed":
    case "rejected":
      return "destructive";
    case "completed":
      return "secondary";
    default:
      return "outline";
  }
}

export function rentalNetSatang(entry: Pick<RentalHistoryEntry, "paidSatang" | "refundedSatang">) {
  if (entry.paidSatang === null) return null;
  return entry.paidSatang - entry.refundedSatang;
}

const bangkokDateTime = new Intl.DateTimeFormat("th-TH", {
  dateStyle: "medium",
  timeStyle: "short",
  timeZone: "Asia/Bangkok",
});

export function formatRentalDateTime(value: string | null | undefined) {
  if (!value) return "—";
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? bangkokDateTime.format(parsed) : "—";
}

/** Formats a duration in seconds as whole hours, keeping minutes when present. */
export function formatRentalDuration(seconds: number | null | undefined) {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds < 0) return "—";
  const totalMinutes = Math.floor(seconds / 60);
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  if (hours === 0) return `${minutes} นาที`;
  return minutes === 0 ? `${hours} ชม.` : `${hours} ชม. ${minutes} นาที`;
}

export function formatRentalHours(hours: number) {
  return `${hours.toLocaleString("th-TH")} ชม.`;
}
