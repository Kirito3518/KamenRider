import type { Machine } from "@/lib/machine-contracts";

export type PcControlLease = {
  rentalId: string;
  userId: string;
  userName: string;
  userEmail: string;
  state: "preparing" | "active" | "ending" | "failed" | "completed" | string;
  revision: number;
  pendingRevision: number | null;
  startsAtUtc: string | null;
  expiresAtUtc: string | null;
  remainingSeconds: number | null;
};

export type PcControlMachine = Machine & { lease: PcControlLease | null };

export type PcControlSnapshot = {
  serverTimeUtc: string;
  machines: PcControlMachine[];
};

export type AdminPairingOffer = {
  rentalId: string;
  leaseRevision: number;
  validUntilUtc: string;
  pairingIds: string[];
};

export type AdminPairingStatus = { commandId: string; status: string };

export const pcControlStatusLabels: Record<string, string> = {
  preparing: "กำลังเตรียมเครื่อง",
  active: "กำลังเช่า",
  ending: "กำลังสิ้นสุดการเช่า",
  failed: "การเช่าล้มเหลว",
  completed: "สิ้นสุดแล้ว",
};

export function agentOnline(machine: Machine, now: number) {
  const seen = machine.lastSeenAt ? Date.parse(machine.lastSeenAt) : NaN;
  return Number.isFinite(seen) && seen <= now && now - seen <= 20_000;
}

export function remainingLabel(remainingSeconds: number | null | undefined) {
  if (remainingSeconds === null || remainingSeconds === undefined) return "—";
  const total = Math.max(0, Math.floor(remainingSeconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  return `${hours.toString().padStart(2, "0")}:${minutes
    .toString()
    .padStart(2, "0")}:${seconds.toString().padStart(2, "0")}`;
}

export function formatPcControlDateTime(value: string | null | undefined) {
  if (!value) return "—";
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? new Date(parsed).toLocaleString("th-TH") : "—";
}
