import test from "node:test";
import assert from "node:assert/strict";

import { buildOneLinerInstaller, psSingleQuote } from "../src/lib/agent-install-script.mjs";

const baseOptions = {
  installUrl: "https://kamanriderplay.com/api/agent/install.ps1?k=" + "A".repeat(43),
  bootstrapUrl: "https://kamanriderplay.com/agent/Bootstrap-Agent.ps1",
  bootstrapSha256: "b".repeat(64),
  controllerUrl: "https://192.168.1.100:7443/",
  packageUrl: "https://kamanriderplay.com/agent/AgentPackage.zip",
  packageSha256: "c".repeat(64),
  trustThumbprint: "d".repeat(40),
  enrollmentTicket: "E".repeat(43),
  installReturnUrl: "https://kamanriderplay.com/admin/machines",
  vncAllowedSourceAddress: "192.168.1.100",
  controllerCertUrl: "https://kamanriderplay.com/agent/controller-root.cer",
};

test("psSingleQuote escapes embedded single quotes", () => {
  assert.equal(psSingleQuote("plain"), "'plain'");
  assert.equal(psSingleQuote("it's"), "'it''s'");
});

test("installer self-elevates instead of failing in a non-admin shell", () => {
  const script = buildOneLinerInstaller(baseOptions);
  assert.match(script, /if \(-not \(Test-IsAdministrator\)\)/);
  assert.match(script, /Start-Process -FilePath 'powershell\.exe' -Verb RunAs/);
  assert.match(script, /irm '\$safeUrl' \| iex/);
  assert.match(script, /\$safeUrl = \$InstallUrl\.Replace\("'", "''"\)/);
});

test("installer installs Sunshine automatically when the service is missing", () => {
  const script = buildOneLinerInstaller(baseOptions);
  assert.match(script, /Get-Service -Name "SunshineService"/);
  assert.match(script, /winget install --id LizardByte\.Sunshine/);
  assert.match(script, /Sunshine installation did not create SunshineService/);
});

test("installer pins the certificate thumbprint and bootstrap hash before executing", () => {
  const script = buildOneLinerInstaller(baseOptions);
  assert.match(script, /controller-root\.cer/);
  assert.ok(script.includes(`'${"D".repeat(40)}'`));
  assert.match(script, /Import-Certificate -FilePath \$controllerCertPath -CertStoreLocation 'Cert:\\LocalMachine\\Root'/);
  assert.match(script, /Get-FileHash -LiteralPath \$bootstrapPath -Algorithm SHA256/);
  assert.ok(script.includes(`'${"B".repeat(64)}'`));
  assert.match(script, /Installer script hash mismatch/);
});

test("installer passes the enrollment ticket and deployment URLs to the bootstrap", () => {
  const script = buildOneLinerInstaller(baseOptions);
  assert.ok(script.includes(`-EnrollmentTicket '${"E".repeat(43)}'`));
  assert.ok(script.includes("-ControllerUrl 'https://192.168.1.100:7443/'"));
  assert.ok(script.includes("-PackageSha256 '" + "C".repeat(64) + "'"));
  assert.ok(script.includes("-VncAllowedSourceAddress '192.168.1.100'"));
});
