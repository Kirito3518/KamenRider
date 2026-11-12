import "server-only";

import { createHash } from "node:crypto";
import { constants } from "node:fs";
import { lstat, open, realpath } from "node:fs/promises";
import { isAbsolute, join, relative, resolve, sep } from "node:path";

export type AgentMaintenanceRuntime = "win-x64" | "win-arm64";

export type VerifiedMaintenanceAsset = Readonly<{
  relativeUrl: string;
  length: number;
  sha256: string;
}>;

export type VerifiedAgentMaintenanceAssets = Readonly<{
  helperProtocol: 1;
  helper: VerifiedMaintenanceAsset;
  wrapper: VerifiedMaintenanceAsset;
}>;

export class AgentMaintenanceAssetsUnavailableError extends Error {
  constructor() {
    super("Agent maintenance assets are unavailable.");
    this.name = "AgentMaintenanceAssetsUnavailableError";
  }
}

const manifestName = "agent-maintenance-assets.json";
const maximumManifestBytes = 64 * 1024;
const maximumAssetBytes = 512 * 1024 * 1024;
const hashPattern = /^[0-9a-fA-F]{64}$/;

type ParsedAsset = {
  relativeUrl: string;
  length: number;
  sha256: string;
};

type ParsedManifest = {
  schemaVersion: 1;
  helperProtocol: 1;
  helpers: Record<AgentMaintenanceRuntime, ParsedAsset>;
  wrapper: ParsedAsset;
};

type PathSegment = string;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function assertExactKeys(value: Record<string, unknown>, expected: readonly string[]) {
  const actual = Object.keys(value);
  if (actual.length !== expected.length || expected.some((key) => !Object.hasOwn(value, key))) {
    throw new Error("Invalid manifest shape.");
  }
}

function assertNoDuplicateJsonKeys(jsonText: string) {
  const containers: Array<{ kind: "object"; keys: Set<string> } | { kind: "array" }> = [];

  for (let index = 0; index < jsonText.length; index += 1) {
    const current = jsonText[index];
    if (current === "{") {
      containers.push({ kind: "object", keys: new Set<string>() });
      continue;
    }
    if (current === "[") {
      containers.push({ kind: "array" });
      continue;
    }
    if (current === "}" || current === "]") {
      containers.pop();
      continue;
    }
    if (current !== '"') continue;

    const start = index;
    index += 1;
    while (index < jsonText.length) {
      if (jsonText[index] === "\\") {
        index += 2;
        continue;
      }
      if (jsonText[index] === '"') break;
      index += 1;
    }

    const end = index;
    let next = end + 1;
    while (next < jsonText.length && /\s/.test(jsonText[next])) next += 1;
    const container = containers.at(-1);
    if (jsonText[next] === ":" && container?.kind === "object") {
      const key = JSON.parse(jsonText.slice(start, end + 1)) as unknown;
      if (typeof key !== "string" || container.keys.has(key)) {
        throw new Error("Duplicate manifest key.");
      }
      container.keys.add(key);
    }
  }
}

function parseAsset(value: unknown, expectedUrl: (hash: string) => string): ParsedAsset {
  if (!isRecord(value)) throw new Error("Invalid asset entry.");
  assertExactKeys(value, ["relativeUrl", "length", "sha256"]);

  const { relativeUrl, length, sha256 } = value;
  if (
    typeof relativeUrl !== "string" ||
    typeof length !== "number" ||
    !Number.isSafeInteger(length) ||
    length < 1 ||
    length > maximumAssetBytes ||
    typeof sha256 !== "string" ||
    !hashPattern.test(sha256)
  ) {
    throw new Error("Invalid asset metadata.");
  }

  const normalizedHash = sha256.toLowerCase();
  if (relativeUrl !== expectedUrl(normalizedHash)) throw new Error("Invalid asset URL.");
  return { relativeUrl, length, sha256: normalizedHash };
}

function parseManifest(jsonText: string): ParsedManifest {
  assertNoDuplicateJsonKeys(jsonText);
  const value: unknown = JSON.parse(jsonText);
  if (!isRecord(value)) throw new Error("Invalid manifest.");
  assertExactKeys(value, ["schemaVersion", "helperProtocol", "helpers", "wrapper"]);
  if (value.schemaVersion !== 1 || value.helperProtocol !== 1 || !isRecord(value.helpers)) {
    throw new Error("Unsupported manifest protocol.");
  }
  assertExactKeys(value.helpers, ["win-x64", "win-arm64"]);

  const helperUrl = (hash: string) => `/agent/maintenance/sha256/${hash}/AgentMaintenance.exe`;
  const wrapper = parseAsset(value.wrapper, () => "/agent/Update-Agent.ps1");
  const helpers: Record<AgentMaintenanceRuntime, ParsedAsset> = {
    "win-x64": parseAsset(value.helpers["win-x64"], helperUrl),
    "win-arm64": parseAsset(value.helpers["win-arm64"], helperUrl),
  };

  return { schemaVersion: 1, helperProtocol: 1, helpers, wrapper };
}

function isWithinDirectory(parent: string, child: string) {
  const childRelativePath = relative(parent, child);
  return childRelativePath === "" || (
    childRelativePath !== ".." &&
    !childRelativePath.startsWith(`..${sep}`) &&
    !isAbsolute(childRelativePath)
  );
}

async function checkedPath(root: string, segments: readonly PathSegment[]) {
  let currentPath = root;
  for (let index = 0; index < segments.length; index += 1) {
    currentPath = join(currentPath, segments[index]);
    const info = await lstat(currentPath);
    if (info.isSymbolicLink()) throw new Error("Asset path contains a symbolic link.");

    const isLast = index === segments.length - 1;
    if (isLast ? !info.isFile() || info.nlink !== 1 : !info.isDirectory()) {
      throw new Error("Asset path has an unexpected node type.");
    }
  }

  const canonicalPath = await realpath(currentPath);
  if (!isWithinDirectory(root, canonicalPath)) throw new Error("Asset path escaped its deployment root.");
  return { path: currentPath, canonicalPath };
}

async function readManifestBytes(root: string) {
  const checked = await checkedPath(root, [manifestName]);
  const file = await open(checked.path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  try {
    const info = await file.stat();
    if (!info.isFile() || info.nlink !== 1 || info.size < 1 || info.size > maximumManifestBytes) {
      throw new Error("Manifest size or file type is invalid.");
    }
    const chunks: Buffer[] = [];
    let totalBytes = 0;
    let position = 0;
    while (true) {
      const buffer = Buffer.allocUnsafe(Math.min(16 * 1024, maximumManifestBytes + 1 - totalBytes));
      const { bytesRead } = await file.read(buffer, 0, buffer.length, position);
      if (bytesRead === 0) break;
      totalBytes += bytesRead;
      if (totalBytes > maximumManifestBytes) throw new Error("Manifest exceeds its size limit.");
      chunks.push(buffer.subarray(0, bytesRead));
      position += bytesRead;
    }
    if (totalBytes !== info.size) throw new Error("Manifest changed while being read.");
    const afterRead = await file.stat();
    const canonicalAfterRead = await realpath(checked.path);
    if (
      afterRead.size !== info.size ||
      afterRead.nlink !== 1 ||
      canonicalAfterRead !== checked.canonicalPath ||
      !isWithinDirectory(root, canonicalAfterRead)
    ) {
      throw new Error("Manifest path changed while being read.");
    }
    return Buffer.concat(chunks, totalBytes);
  } finally {
    await file.close();
  }
}

async function readExactlyAt(file: Awaited<ReturnType<typeof open>>, position: number, length: number) {
  const buffer = Buffer.alloc(length);
  let offset = 0;
  while (offset < length) {
    const { bytesRead } = await file.read(buffer, offset, length - offset, position + offset);
    if (bytesRead === 0) throw new Error("Helper PE header is truncated.");
    offset += bytesRead;
  }
  return buffer;
}

async function verifyHelperPeMachine(
  file: Awaited<ReturnType<typeof open>>,
  fileLength: number,
  runtime: AgentMaintenanceRuntime,
) {
  if (fileLength < 256) throw new Error("Helper PE image is too short.");
  const dosHeader = await readExactlyAt(file, 0, 64);
  if (dosHeader.readUInt16LE(0) !== 0x5a4d) throw new Error("Helper has no DOS header.");

  const peOffset = dosHeader.readUInt32LE(0x3c);
  if (peOffset < 64 || peOffset > fileLength - 6) throw new Error("Helper PE header offset is invalid.");
  const peHeader = await readExactlyAt(file, peOffset, 6);
  if (peHeader.readUInt32LE(0) !== 0x00004550) throw new Error("Helper has no PE signature.");

  const expectedMachine = runtime === "win-x64" ? 0x8664 : 0xaa64;
  if (peHeader.readUInt16LE(4) !== expectedMachine) throw new Error("Helper PE architecture does not match.");
}

async function verifyAssetFile(
  root: string,
  segments: readonly PathSegment[],
  expected: ParsedAsset,
  helperRuntime?: AgentMaintenanceRuntime,
) {
  const checked = await checkedPath(root, segments);
  const file = await open(checked.path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  try {
    const info = await file.stat();
    if (
      !info.isFile() ||
      info.nlink !== 1 ||
      info.size !== expected.length ||
      info.size > maximumAssetBytes
    ) {
      throw new Error("Asset size or file type does not match the manifest.");
    }

    const hash = createHash("sha256");
    const buffer = Buffer.allocUnsafe(64 * 1024);
    let totalBytes = 0;
    let position = 0;
    while (true) {
      const { bytesRead } = await file.read(buffer, 0, buffer.length, position);
      if (bytesRead === 0) break;
      totalBytes += bytesRead;
      if (totalBytes > expected.length) throw new Error("Asset grew while being read.");
      hash.update(buffer.subarray(0, bytesRead));
      position += bytesRead;
    }

    const actualHash = hash.digest("hex");
    if (helperRuntime) await verifyHelperPeMachine(file, expected.length, helperRuntime);
    const afterRead = await file.stat();
    const canonicalAfterRead = await realpath(checked.path);
    if (
      totalBytes !== expected.length ||
      actualHash !== expected.sha256 ||
      afterRead.size !== info.size ||
      afterRead.nlink !== 1 ||
      canonicalAfterRead !== checked.canonicalPath ||
      !isWithinDirectory(root, canonicalAfterRead)
    ) {
      throw new Error("Asset bytes changed or do not match the manifest.");
    }
  } finally {
    await file.close();
  }
}

function parseUtf8(bytes: Buffer) {
  return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
}

/**
 * Reads the deployment-local fixed manifest and verifies the fixed wrapper and
 * selected native-runtime helper against its length and SHA-256 before a route
 * may include those values in a machine command.
 */
export async function verifyDeploymentAgentMaintenanceAssets(
  runtime: AgentMaintenanceRuntime,
): Promise<VerifiedAgentMaintenanceAssets> {
  try {
    if (runtime !== "win-x64" && runtime !== "win-arm64") throw new Error("Unsupported helper runtime.");

    const publicAgentRoot = resolve(process.cwd(), "public", "agent");
    const root = await realpath(publicAgentRoot);
    const manifestBytes = await readManifestBytes(root);
    const manifest = parseManifest(parseUtf8(manifestBytes));
    const helper = manifest.helpers[runtime];

    const helperSegments = [
      "maintenance",
      "sha256",
      helper.sha256,
      "AgentMaintenance.exe",
    ];
    const wrapperSegments = ["Update-Agent.ps1"];

    await Promise.all([
      verifyAssetFile(root, helperSegments, helper, runtime),
      verifyAssetFile(root, wrapperSegments, manifest.wrapper),
    ]);

    return Object.freeze({
      helperProtocol: manifest.helperProtocol,
      helper: Object.freeze({ ...helper }),
      wrapper: Object.freeze({ ...manifest.wrapper }),
    });
  } catch {
    throw new AgentMaintenanceAssetsUnavailableError();
  }
}
