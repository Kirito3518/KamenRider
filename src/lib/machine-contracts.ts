export type Machine = {
  machineId: string;
  approvalStatus: string;
  displayName: string;
  hourlyPriceMinorUnits: number;
  moonlightPort: number;
  sunshinePort: number;
  configRevision: number;
  appliedConfigRevision: number | null;
  readiness: string;
  lastSeenAt: string | null;
  publicKeyFingerprint?: string;
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
