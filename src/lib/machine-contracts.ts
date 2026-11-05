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
  lastSeenAt: string | null;
  agentVersion: string | null;
  publicKeyFingerprint?: string;
  activeAdminVncSessions: number;
};
export type Lease = {
  connection?: { address: string | null; stale: boolean };
  id: string;
  machineId: string;
  state: string;
  revision: number;
  pendingRevision: number | null;
  expiresAtUtc: string | null;
};
export type Offer = { pairingIds: string[]; validUntilUtc: string } | null;
