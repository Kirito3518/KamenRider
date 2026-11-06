import assert from "node:assert/strict";
import { test } from "node:test";
import {
  getSavedVncTargetForMetadataUpdate,
  getVncTargetForExplicitSave,
  mergeVncTargetDrafts,
  updateVncTargetDraft,
} from "../src/lib/admin-vnc-target-drafts.mjs";

test("unset VNC targets start blank despite Agent IP and drafts stay scoped", () => {
  const machines = [
    { machineId: "pc-a", localIp: "192.168.1.40", vncLocalIp: null },
    { machineId: "pc-b", localIp: "192.168.1.41", vncLocalIp: "192.168.1.6" },
  ];

  const drafts = mergeVncTargetDrafts({}, machines);
  assert.deepEqual(drafts, { "pc-a": "", "pc-b": "192.168.1.6" });

  const edited = updateVncTargetDraft(drafts, "pc-a", "192.168.1.9");
  assert.deepEqual(edited, { "pc-a": "192.168.1.9", "pc-b": "192.168.1.6" });
  assert.deepEqual(mergeVncTargetDrafts(edited, machines), edited);
});

test("metadata saves only carry an already saved VNC target", () => {
  const unconfirmedMachine = { localIp: "192.168.1.40", vncLocalIp: null };
  const unsavedDraft = updateVncTargetDraft({}, "pc-a", "192.168.1.9");
  assert.equal(unsavedDraft["pc-a"], "192.168.1.9");
  assert.equal(getSavedVncTargetForMetadataUpdate(unconfirmedMachine), null);
  assert.equal(
    getSavedVncTargetForMetadataUpdate({ vncLocalIp: "192.168.1.6" }),
    "192.168.1.6",
  );
});

test("only explicit save accepts a private LAN target and rejects unsafe drafts", () => {
  assert.equal(getVncTargetForExplicitSave(" 192.168.1.9 "), "192.168.1.9");
  assert.equal(getVncTargetForExplicitSave("10.0.0.9"), "10.0.0.9");
  assert.equal(getVncTargetForExplicitSave("fd00::9"), "fd00::9");
  assert.equal(getVncTargetForExplicitSave("FC00::9"), "fc00::9");
  assert.equal(getVncTargetForExplicitSave("  "), null);
  for (const invalid of [
    "127.0.0.1",
    "192.0.2.8",
    "192.168.1.0/24",
    "not-an-ip",
    "::1",
    "fe80::1",
    "2001:db8::8",
  ]) {
    assert.equal(getVncTargetForExplicitSave(invalid), null, invalid);
  }
});
