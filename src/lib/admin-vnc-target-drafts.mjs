/** @param {Record<string, string>} current @param {Array<{ machineId: string, vncLocalIp: string | null }>} machines */
export function mergeVncTargetDrafts(current, machines) {
  const next = { ...current };
  for (const machine of machines) {
    if (!Object.prototype.hasOwnProperty.call(next, machine.machineId)) {
      next[machine.machineId] = machine.vncLocalIp ?? "";
    }
  }
  return next;
}

/** @param {Record<string, string>} drafts @param {string} machineId @param {string} value */
export function updateVncTargetDraft(drafts, machineId, value) {
  return { ...drafts, [machineId]: value };
}

/** @param {{ vncLocalIp: string | null }} machine */
export function getSavedVncTargetForMetadataUpdate(machine) {
  return machine.vncLocalIp || null;
}

/** @param {string} value */
function isPrivateIpv4(value) {
  const parts = value.split(".");
  if (parts.length !== 4 || parts.some((part) => !/^\d{1,3}$/.test(part))) {
    return false;
  }

  const octets = parts.map(Number);
  if (
    octets.some((octet) => octet < 0 || octet > 255) ||
    parts.some((part, index) => String(octets[index]) !== part)
  ) {
    return false;
  }

  const [first, second] = octets;
  return (
    first === 10 ||
    (first === 172 && second >= 16 && second <= 31) ||
    (first === 192 && second === 168)
  );
}

/** @param {string} value */
function privateIpv6Ula(value) {
  if (value.includes("%") || value.startsWith("[") || value.endsWith("]")) {
    return null;
  }

  try {
    const hostname = new URL(`http://[${value}]/`).hostname;
    const address = hostname.slice(1, -1).toLowerCase();
    const firstGroup = address.split(":", 1)[0];
    const firstWord = Number.parseInt(firstGroup, 16);
    if (!firstGroup || !Number.isInteger(firstWord) || (firstWord & 0xfe00) !== 0xfc00) {
      return null;
    }
    return address;
  } catch {
    return null;
  }
}

/** @param {string | undefined} value */
export function getVncTargetForExplicitSave(value) {
  const target = value?.trim() ?? "";
  if (!target) return null;
  if (isPrivateIpv4(target)) return target;
  return privateIpv6Ula(target);
}
