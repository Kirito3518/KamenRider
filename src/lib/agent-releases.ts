import type { Machine } from "@/lib/machine-contracts";

export type AgentRelease = {
  version: string;
  channel: "stable" | "beta";
  releaseNotes: string;
  packageSha256: string;
  packageSizeBytes: number;
  sourceCommit: string;
  sourceDirty: boolean;
  publishedAtUtc: string;
};

export const updateStatusLabels: Record<string, string> = {
  awaitingInstaller: "รอเรียกใช้ตัวติดตั้ง",
  installing: "กำลังติดตั้ง",
  awaitingHeartbeat: "รอ heartbeat ยืนยันเวอร์ชัน",
  rollingBack: "กำลังกู้คืนเวอร์ชันเดิม",
  completed: "อัปเดตสำเร็จและยืนยันแล้ว",
  failed: "อัปเดตล้มเหลว (เครื่องยังถูกล็อก)",
  expired: "คำสั่งหมดอายุก่อนเริ่มติดตั้ง",
  rolledBack: "กู้คืนเวอร์ชันเดิมและยืนยันแล้ว",
};

export function isAgentOnline(machine: Machine, now: number) {
  const seen = machine.lastSeenAt ? Date.parse(machine.lastSeenAt) : NaN;
  return Number.isFinite(seen) && seen <= now && now - seen <= 20_000;
}
