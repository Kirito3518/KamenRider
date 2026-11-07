import assert from "node:assert/strict";
import { test } from "node:test";

test("install command expiry stays usable before its deadline and expires at it", async () => {
  let expiryModule;
  let importError;
  try {
    expiryModule = await import("../src/lib/agent-install-expiry.mjs");
  } catch (error) {
    importError = error;
  }
  assert.ok(expiryModule, `expiry helper could not be imported: ${importError}`);

  const expiresAtUtc = "2026-11-07T12:34:56.789Z";
  const deadline = Date.parse(expiresAtUtc);
  assert.deepEqual(
    expiryModule.getInstallCommandExpiryState(expiresAtUtc, deadline - 1),
    { expired: false, displayUtc: "2026-11-07T12:34:56.789Z" },
  );
  assert.deepEqual(
    expiryModule.getInstallCommandExpiryState(expiresAtUtc, deadline),
    { expired: true, displayUtc: "2026-11-07T12:34:56.789Z" },
  );
  assert.deepEqual(
    expiryModule.getInstallCommandExpiryState(expiresAtUtc, deadline + 1),
    { expired: true, displayUtc: "2026-11-07T12:34:56.789Z" },
  );
});
