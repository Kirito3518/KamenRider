/**
 * Builds the one-line Agent installer served at /api/agent/install.ps1.
 *
 * Keep this module free of Next.js imports so it can be unit-tested with node:test.
 * The generated script must stay Windows PowerShell 5.1 compatible.
 */

/** Quote a value as a single-quoted PowerShell literal. */
export function psSingleQuote(value) {
  return `'${String(value).replaceAll("'", "''")}'`;
}

/**
 * @param {{
 *   installUrl: string,
 *   bootstrapUrl: string,
 *   bootstrapSha256: string,
 *   controllerUrl: string,
 *   packageUrl: string,
 *   packageSha256: string,
 *   trustThumbprint: string,
 *   enrollmentTicket: string,
 *   installReturnUrl: string,
 *   vncAllowedSourceAddress: string,
 *   controllerCertUrl: string,
 * }} options
 */
export function buildOneLinerInstaller(options) {
  const installUrl = psSingleQuote(options.installUrl);
  const bootstrapUrl = psSingleQuote(options.bootstrapUrl);
  const bootstrapSha256 = psSingleQuote(options.bootstrapSha256.toUpperCase());
  const controllerUrl = psSingleQuote(options.controllerUrl);
  const packageUrl = psSingleQuote(options.packageUrl);
  const packageSha256 = psSingleQuote(options.packageSha256.toUpperCase());
  const trustThumbprint = psSingleQuote(options.trustThumbprint.toUpperCase());
  const enrollmentTicket = psSingleQuote(options.enrollmentTicket);
  const installReturnUrl = psSingleQuote(options.installReturnUrl);
  const vncAllowedSourceAddress = psSingleQuote(options.vncAllowedSourceAddress);
  const controllerCertUrl = psSingleQuote(options.controllerCertUrl);

  return [
    "$ErrorActionPreference = 'Stop'",
    `$InstallUrl = ${installUrl}`,
    "",
    "function Test-IsAdministrator {",
    "    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()",
    "    return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)",
    "}",
    "",
    "if (-not (Test-IsAdministrator)) {",
    "    Write-Host ''",
    "    Write-Host 'KamenRaider Agent installer needs Administrator rights.' -ForegroundColor Yellow",
    "    Write-Host 'A new elevated PowerShell window will open - accept the Windows prompt.' -ForegroundColor Yellow",
    "    $safeUrl = $InstallUrl.Replace(\"'\", \"''\")",
    "    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', \"irm '$safeUrl' | iex\")",
    "    exit 0",
    "}",
    "",
    "try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }",
    "",
    "Write-Host ''",
    "Write-Host '=== KamenRaider Agent one-click installer ===' -ForegroundColor Cyan",
    "",
    "# Sunshine is required by the Stream Guard. Install it automatically when it is missing.",
    '$sunshine = Get-Service -Name "SunshineService" -ErrorAction SilentlyContinue',
    "if (-not $sunshine) {",
    "    $winget = Get-Command winget -ErrorAction SilentlyContinue",
    "    if (-not $winget) {",
    "        throw 'Sunshine is required but winget is unavailable. Install Sunshine from https://github.com/LizardByte/Sunshine/releases then run this command again.'",
    "    }",
    "    Write-Host 'Installing Sunshine (required by the Stream Guard)...' -ForegroundColor Yellow",
    "    $savedEap = $ErrorActionPreference",
    "    $ErrorActionPreference = 'Continue'",
    "    try {",
    "        & winget install --id LizardByte.Sunshine -e --silent --disable-interactivity --accept-package-agreements --accept-source-agreements 2>&1 | ForEach-Object { Write-Host \"    $_\" }",
    "    } finally {",
    "        $ErrorActionPreference = $savedEap",
    "    }",
    "    for ($i = 0; $i -lt 60; $i++) {",
    "        if (Get-Service -Name 'SunshineService' -ErrorAction SilentlyContinue) { break }",
    "        Start-Sleep -Seconds 2",
    "    }",
    "    if (-not (Get-Service -Name 'SunshineService' -ErrorAction SilentlyContinue)) {",
    "        throw 'Sunshine installation did not create SunshineService. Install Sunshine manually and run this command again.'",
    "    }",
    "}",
    "",
    "# Trust the Controller certificate (downloaded over HTTPS, thumbprint pinned).",
    "$temp = Join-Path $env:TEMP ('kamenraider-install-' + [guid]::NewGuid().ToString('N'))",
    "New-Item -ItemType Directory -Force -Path $temp | Out-Null",
    "try {",
    "    $controllerCertPath = Join-Path $temp 'controller-root.cer'",
    "    Write-Host 'Downloading the Controller certificate...'",
    `    Invoke-WebRequest -Uri ${controllerCertUrl} -OutFile $controllerCertPath -UseBasicParsing`,
    "    $downloadedCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($controllerCertPath)",
    `    if ($downloadedCert.Thumbprint -ne ${trustThumbprint}) {`,
    `        throw "Controller certificate thumbprint mismatch (expected ${trustThumbprint})."`,
    "    }",
    `    if (-not (Test-Path -LiteralPath ("Cert:\\LocalMachine\\Root\\" + ${trustThumbprint}))) {`,
    "        Import-Certificate -FilePath $controllerCertPath -CertStoreLocation 'Cert:\\LocalMachine\\Root' | Out-Null",
    "    }",
    "",
    "    # Download and hash-verify the bootstrap script before executing it.",
    "    $bootstrapPath = Join-Path $temp 'Bootstrap-Agent.ps1'",
    "    Write-Host 'Downloading the verified installer...'",
    `    Invoke-WebRequest -Uri ${bootstrapUrl} -OutFile $bootstrapPath -UseBasicParsing`,
    "    $bootstrapHash = (Get-FileHash -LiteralPath $bootstrapPath -Algorithm SHA256).Hash",
    `    if ($bootstrapHash -ne ${bootstrapSha256}) {`,
    "        throw 'Installer script hash mismatch; the download is corrupted or was modified.'",
    "    }",
    "",
    "    $bootstrapScript = [IO.File]::ReadAllText($bootstrapPath)",
    "    Write-Host 'Installing KamenRaider Agent and TightVNC...'",
    "    & ([ScriptBlock]::Create($bootstrapScript)) `",
    `        -ControllerUrl ${controllerUrl} \``,
    `        -PackageUrl ${packageUrl} \``,
    `        -PackageSha256 ${packageSha256} \``,
    `        -TrustedRootThumbprint ${trustThumbprint} \``,
    `        -EnrollmentTicket ${enrollmentTicket} \``,
    `        -InstallReturnUrl ${installReturnUrl} \``,
    `        -VncAllowedSourceAddress ${vncAllowedSourceAddress}`,
    "",
    "    Write-Host ''",
    "    Write-Host 'Installation finished. The admin page should now show this PC as pending.' -ForegroundColor Green",
    "    Write-Host 'Keep this window open if the page did not open automatically.'",
    "}",
    "finally {",
    "    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue",
    "}",
    "",
  ].join("\n");
}
