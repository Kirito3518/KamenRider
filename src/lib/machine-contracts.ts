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
  id: string;
  machineId: string;
  state: string;
  revision: number;
  pendingRevision: number | null;
  expiresAtUtc: string | null;
};
export type Offer = { pairingIds: string[]; validUntilUtc: string } | null;
