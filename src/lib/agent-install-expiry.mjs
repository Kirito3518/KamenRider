/** @param {string} expiresAtUtc @param {number} nowMs */
export function getInstallCommandExpiryState(expiresAtUtc, nowMs = Date.now()) {
  const expiresAtMs = Date.parse(expiresAtUtc);
  const validExpiry = Number.isFinite(expiresAtMs);
  return {
    expired: !validExpiry || expiresAtMs <= nowMs,
    displayUtc: validExpiry ? new Date(expiresAtMs).toISOString() : null,
  };
}
