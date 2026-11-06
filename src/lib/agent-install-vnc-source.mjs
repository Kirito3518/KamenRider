/** @param {string} hostname */
export function isLoopbackHostname(hostname) {
  const host = hostname.replace(/^\[(.*)\]$/, "$1").toLowerCase();
  if (host === "localhost" || host === "::1") return true;
  const octets = host.split(".").map(Number);
  return octets.length === 4 && octets[0] === 127 && octets.every((part) => Number.isInteger(part) && part >= 0 && part <= 255);
}

/** @param {string} value */
export function isPrivateVncSourceAddress(value) {
  const octets = value.split(".");
  if (octets.length !== 4 || octets.some((part) => !/^\d{1,3}$/.test(part))) return false;
  const [first, second, third, fourth] = octets.map(Number);
  if ([first, second, third, fourth].some((part) => part < 0 || part > 255)) return false;
  if (octets.some((part) => String(Number(part)) !== part)) return false;
  return first === 10 || (first === 172 && second >= 16 && second <= 31) || (first === 192 && second === 168);
}

/** @param {string} controllerHostname @param {string | undefined} configured */
export function resolveVncSourceAddress(controllerHostname, configured) {
  const source = configured?.trim();
  if (source) return isPrivateVncSourceAddress(source) ? source : null;
  return isPrivateVncSourceAddress(controllerHostname) ? controllerHostname : null;
}
