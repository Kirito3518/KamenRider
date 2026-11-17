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
export type Lease = {
  agent?: AgentHeartbeat;
  connection?: { address: string | null; stale: boolean };
  /** Agent-reported machine display name (hostname by default); safe to show the renter. */
  machineDisplayName?: string | null;
  id: string;
  machineId: string;
  state: string;
  revision: number;
  pendingRevision: number | null;
  expiresAtUtc: string | null;
};
export type Offer = { pairingIds: string[]; validUntilUtc: string } | null;
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
