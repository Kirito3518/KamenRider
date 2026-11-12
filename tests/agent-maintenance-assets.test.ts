// @vitest-environment node

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createHash } from "node:crypto";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AgentMaintenanceAssetsUnavailableError,
  MAX_AGENT_MAINTENANCE_HELPER_BYTES,
  verifyDeploymentAgentMaintenanceAssets,
} from "../src/lib/agent-maintenance-assets";

vi.mock("server-only", () => ({}));

function peFixture(machine: number, label: string) {
  const bytes = Buffer.alloc(512);
  bytes.writeUInt16LE(0x5a4d, 0);
  bytes.writeUInt32LE(0x80, 0x3c);
  bytes.writeUInt32LE(0x00004550, 0x80);
  bytes.writeUInt16LE(machine, 0x84);
  bytes.write(label, 0x100, "utf8");
  return bytes;
}

const wrapperBytes = Buffer.from("Write-Output 'trusted wrapper'\n", "utf8");
const x64Bytes = peFixture(0x8664, "signed x64 helper fixture");
const arm64Bytes = peFixture(0xaa64, "signed arm64 helper fixture");

function sha256(bytes: Buffer) {
  return createHash("sha256").update(bytes).digest("hex").toUpperCase();
}

function helperRecord(bytes: Buffer) {
  const digest = sha256(bytes);
  return {
    relativeUrl: `/agent/maintenance/sha256/${digest.toLowerCase()}/AgentMaintenance.exe`,
    length: bytes.length,
    sha256: digest,
  };
}

describe("deployment-local Agent maintenance assets", () => {
  let deploymentRoot: string;
  let publicAgentRoot: string;
  let manifest: {
    schemaVersion: number;
    helperProtocol: number;
    helpers: Record<string, ReturnType<typeof helperRecord>>;
    wrapper: ReturnType<typeof helperRecord>;
  };

  beforeEach(() => {
    deploymentRoot = mkdtempSync(join(tmpdir(), "agent-maintenance-assets-"));
    publicAgentRoot = join(deploymentRoot, "public", "agent");
    mkdirSync(publicAgentRoot, { recursive: true });
    vi.spyOn(process, "cwd").mockReturnValue(deploymentRoot);

    const x64 = helperRecord(x64Bytes);
    const arm64 = helperRecord(arm64Bytes);
    manifest = {
      schemaVersion: 1,
      helperProtocol: 1,
      helpers: { "win-x64": x64, "win-arm64": arm64 },
      wrapper: {
        relativeUrl: "/agent/Update-Agent.ps1",
        length: wrapperBytes.length,
        sha256: sha256(wrapperBytes),
      },
    };

    writeFileSync(join(publicAgentRoot, "agent-maintenance-assets.json"), JSON.stringify(manifest));
    writeFileSync(join(publicAgentRoot, "Update-Agent.ps1"), wrapperBytes);
    writeHelper("win-x64", x64);
    writeHelper("win-arm64", arm64);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    rmSync(deploymentRoot, { recursive: true, force: true });
  });

  function writeHelper(runtime: "win-x64" | "win-arm64", record: ReturnType<typeof helperRecord>) {
    const hash = record.sha256.toLowerCase();
    const path = join(publicAgentRoot, "maintenance", "sha256", hash, "AgentMaintenance.exe");
    mkdirSync(join(path, ".."), { recursive: true });
    writeFileSync(path, runtime === "win-x64" ? x64Bytes : arm64Bytes);
  }

  it("returns the fixed wrapper and selected helper only after checking their deployed bytes", async () => {
    const assets = await verifyDeploymentAgentMaintenanceAssets("win-x64");

    expect(assets).toEqual({
      helperProtocol: 1,
      helper: {
        relativeUrl: manifest.helpers["win-x64"].relativeUrl,
        length: x64Bytes.length,
        sha256: sha256(x64Bytes).toLowerCase(),
      },
      wrapper: {
        relativeUrl: "/agent/Update-Agent.ps1",
        length: wrapperBytes.length,
        sha256: sha256(wrapperBytes).toLowerCase(),
      },
    });
  });

  it("rejects a helper URL that attempts to escape the fixed public asset tree", async () => {
    manifest.helpers["win-x64"].relativeUrl = "/agent/maintenance/sha256/../../outside.exe";
    writeFileSync(join(publicAgentRoot, "agent-maintenance-assets.json"), JSON.stringify(manifest));

    await expect(verifyDeploymentAgentMaintenanceAssets("win-x64")).rejects.toThrow();
  });

  it("rejects helper metadata above the publisher and bootstrap 150 MiB cap", async () => {
    manifest.helpers["win-x64"].length = MAX_AGENT_MAINTENANCE_HELPER_BYTES + 1;
    writeFileSync(join(publicAgentRoot, "agent-maintenance-assets.json"), JSON.stringify(manifest));

    await expect(verifyDeploymentAgentMaintenanceAssets("win-x64")).rejects.toBeInstanceOf(
      AgentMaintenanceAssetsUnavailableError,
    );
  });

  it("rejects duplicate manifest keys even when their values agree", async () => {
    const validJson = JSON.stringify(manifest);
    const duplicateJson = validJson.replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1');
    writeFileSync(join(publicAgentRoot, "agent-maintenance-assets.json"), duplicateJson);

    await expect(verifyDeploymentAgentMaintenanceAssets("win-x64")).rejects.toThrow();
  });

  it("rejects a wrapper whose deployed bytes no longer match the manifest", async () => {
    writeFileSync(join(publicAgentRoot, "Update-Agent.ps1"), Buffer.concat([wrapperBytes, Buffer.from("tampered")]));

    await expect(verifyDeploymentAgentMaintenanceAssets("win-x64")).rejects.toThrow();
  });

  it("rejects a selected helper whose deployed bytes no longer match the manifest", async () => {
    const hash = manifest.helpers["win-x64"].sha256.toLowerCase();
    const helperPath = join(publicAgentRoot, "maintenance", "sha256", hash, "AgentMaintenance.exe");
    writeFileSync(helperPath, Buffer.concat([x64Bytes, Buffer.from("tampered")]));

    await expect(verifyDeploymentAgentMaintenanceAssets("win-x64")).rejects.toThrow();
  });

  it("rejects a helper whose PE machine type disagrees with the selected runtime", async () => {
    manifest.helpers["win-x64"] = { ...manifest.helpers["win-arm64"] };
    writeFileSync(join(publicAgentRoot, "agent-maintenance-assets.json"), JSON.stringify(manifest));

    await expect(verifyDeploymentAgentMaintenanceAssets("win-x64")).rejects.toBeInstanceOf(
      AgentMaintenanceAssetsUnavailableError,
    );
  });

  it("rejects a missing selected helper", async () => {
    const hash = manifest.helpers["win-x64"].sha256.toLowerCase();
    const helperPath = join(publicAgentRoot, "maintenance", "sha256", hash, "AgentMaintenance.exe");
    rmSync(helperPath);

    await expect(verifyDeploymentAgentMaintenanceAssets("win-x64")).rejects.toThrow();
  });

  it("rejects a missing deployment manifest", async () => {
    rmSync(join(publicAgentRoot, "agent-maintenance-assets.json"));

    await expect(verifyDeploymentAgentMaintenanceAssets("win-x64")).rejects.toThrow();
  });
});
