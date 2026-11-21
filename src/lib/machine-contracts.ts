export type ReadinessDiagnosticCheckId =
  | "baseline"
  | "bootChallenge"
  | "restoration"
  | "streamFence"
  | "desktopHealth"
  | "configurationRevision"
  | "leaseReconciliation"
  | "runtimeHealth";

export type ReadinessDiagnosticStatus =
  | "passed"
  | "blocked"
  | "unavailable"
  | "unknown";

export type ReadinessDiagnostic = {
  checkId: ReadinessDiagnosticCheckId;
  status: ReadinessDiagnosticStatus;
};

export type Machine = {
  machineId: string;
  approvalStatus: string;
  displayName: string;
  /** Agent-reported computer name; also the source of the initial display name. */
  hostname: string | null;
  /** Agent-reported hardware identity. Display/diagnostic data only. */
  cpu: string | null;
  gpu: string | null;
  ramBytes: number;
  /** Sunshine management credential revision the Agent reports as applied. */
  appliedSunshineCredentialRevision: number;
  /** Admin opt-in for renter-supplied Parsec account login. */
  parsecEnabled?: boolean;
  /** Agent currently advertises a usable Parsec installation (parsecd.exe detected). */
  parsecAvailable?: boolean;
  localIp: string;
  vncLocalIp: string | null;
  hourlyPriceMinorUnits: number;
  moonlightPort: number;
  sunshinePort: number;
  configRevision: number;
  appliedConfigRevision: number | null;
  readiness: string;
  canRent: boolean;
  allocationHeld?: boolean;
  lastSeenAt: string | null;
  agentVersion: string | null;
  publicKeyFingerprint?: string;
  activeAdminVncSessions: number;
  readinessDiagnostics?: ReadinessDiagnostic[] | null;
  agentUpdateOperationId?: string | null;
  agentUpdateTargetVersion?: string | null;
  agentUpdateStatus?: string | null;
};
export type AgentReachability = "reachable" | "unreachable" | "unknown";
export type AgentHeartbeat = {
  reachability: AgentReachability;
  lastSeenAt: string | null;
};
export type LeasePaymentInfo = {
  hourlyPriceSatang: number;
  durationSeconds: number;
  extensionSeconds: number;
  paidSatang: number;
  refundedSatang: number;
  /** False for leases created before paid rentals existed (no payment record). */
  extendable: boolean;
};
export type Lease = {
  agent?: AgentHeartbeat;
  connection?: { address: string | null; stale: boolean };
  /** Agent-reported machine display name (hostname by default); safe to show the renter. */
  machineDisplayName?: string | null;
  /** Parsec capability for this active lease; absent when the admin has not enabled it. */
  parsec?: import("@/lib/parsec-contracts").ParsecLeaseState | null;
  id: string;
  machineId: string;
  state: string;
  revision: number;
  pendingRevision: number | null;
  startsAtUtc?: string | null;
  expiresAtUtc: string | null;
  endedAtUtc?: string | null;
  payment?: LeasePaymentInfo | null;
};
export type Offer = { pairingIds: string[]; validUntilUtc: string } | null;
export type PairingCommandStatus = {
  commandId: string;
  status: "queued" | "succeeded" | "failed" | "expired";
};
/**
 * Fleet-wide Sunshine management credential metadata as exposed to the Admin
 * browser. The password is intentionally absent: it is write-only from the web
 * and only delivered to approved Agents by the Controller.
 */
export type SunshineCredentialState = {
  configured: boolean;
  userName: string | null;
  revision: number;
  updatedAtUtc: string | null;
};
