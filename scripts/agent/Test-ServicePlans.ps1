$ErrorActionPreference = 'Stop'
$install = Join-Path $PSScriptRoot 'Install-Service.ps1'
$publish = Join-Path $PSScriptRoot 'Publish-Services.ps1'
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('kr-install-test-' + [Guid]::NewGuid().ToString('N'))))
$agent = & $install -Role Agent -PublishDirectory $root -DataDirectory (Join-Path $root 'data') `
    -ConfigurationFile (Join-Path $root 'config.json') -TrustedRootThumbprint ('A' * 40) -Plan
if ($agent.Startup -ne 'auto' -or $agent.Account -ne 'NT SERVICE\KamenRaider Agent') { throw 'Agent plan must use automatic startup and the virtual service account.' }
if ($agent.StartAfterInstall) { throw 'Service Plan must not start a service unless StartAfterInstall is explicitly selected.' }
$startPlan = & $install -Role Agent -PublishDirectory $root -DataDirectory (Join-Path $root 'data') `
    -ConfigurationFile (Join-Path $root 'config.json') -TrustedRootThumbprint ('A' * 40) -StartAfterInstall -Plan
if (-not $startPlan.StartAfterInstall) { throw 'Plan must report the requested post-install start.' }
$controller = & $install -Role Controller -PublishDirectory $root -DataDirectory (Join-Path $root 'data') `
    -ConfigurationFile (Join-Path $root 'config.json') -TrustedRootThumbprint ('A' * 40) -Plan
if ($controller.Startup -ne 'auto') { throw 'Controller automatic startup plan missing.' }
$plans = @(& $publish -Role Both -OutputDirectory $root -Plan)
if ($plans.Count -ne 2 -or $plans[0].Output -eq $plans[1].Output) { throw 'Publish roles must have distinct output directories.' }
$relativeRejected = $false
try { & $install -Role Agent -PublishDirectory '.\relative' -DataDirectory '.\data' -ConfigurationFile '.\config.json' -TrustedRootThumbprint ('A' * 40) -Plan | Out-Null }
catch { $relativeRejected = $true }
if (-not $relativeRejected) { throw 'Relative install paths must be rejected.' }
if (Test-Path -LiteralPath $root) { throw 'Plan mode must not create files.' }
Write-Output 'Installer/publisher plan checks passed; no services or files changed.'
