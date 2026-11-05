$ErrorActionPreference = 'Stop'
$install = Join-Path $PSScriptRoot 'Install-Service.ps1'
$installSource = [IO.File]::ReadAllText($install)
if (-not $installSource.Contains('$dataDirectoryState.Created = Initialize-PrivateDataDirectory') -or -not $installSource.Contains('$dataDirectoryState.Created -and')) { throw 'Service rollback must observe the data-directory creation result and remove its staged enrollment ticket.' }
$privateFiles = Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1'
$privateSource = [IO.File]::ReadAllText($privateFiles)
. ([ScriptBlock]::Create($privateSource))
$publish = Join-Path $PSScriptRoot 'Publish-Services.ps1'
$serviceSid = Get-ServiceSid -ServiceName 'KamenRaider Agent'
$expectedServiceSid = [regex]::Match(((& sc.exe showsid 'KamenRaider Agent') -join "`n"), 'SERVICE SID:\s*(S-1-5-80(?:-\d+){5})').Groups[1].Value
if (-not $expectedServiceSid -or $serviceSid.Value -ne $expectedServiceSid) { throw 'Service SID lookup must work before the service is created.' }
Initialize-ServiceManagerNativeType
if (-not ('KamenRaider.Install.NativeServiceManager' -as [type])) { throw 'Native service creation API did not load.' }
$binaryPath = Get-ServiceBinaryPath -Executable 'C:\Program Files\KamenRaider\Agent\Agent.exe' -ContentRoot 'C:\Program Files\KamenRaider\Agent'
if ($binaryPath -ne '"C:\Program Files\KamenRaider\Agent\Agent.exe" --contentRoot "C:\Program Files\KamenRaider\Agent"') { throw 'Service binary command line must preserve quoted paths containing spaces.' }
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('kr-install-test-' + [Guid]::NewGuid().ToString('N'))))
$agent = & $install -Role Agent -PublishDirectory $root -DataDirectory (Join-Path $root 'data') `
    -ConfigurationFile (Join-Path $root 'config.json') -TrustedRootThumbprint ('A' * 40) -Plan
if ($agent.Startup -ne 'auto' -or $agent.Account -ne 'NT SERVICE\KamenRaider Agent') { throw 'Agent plan must use automatic startup and the virtual service account.' }
if ($agent.StartAfterInstall) { throw 'Service Plan must not start a service unless StartAfterInstall is explicitly selected.' }
$startPlan = & $install -Role Agent -PublishDirectory $root -DataDirectory (Join-Path $root 'data') `
    -ConfigurationFile (Join-Path $root 'config.json') -TrustedRootThumbprint ('A' * 40) -StartAfterInstall -Plan
if (-not $startPlan.StartAfterInstall) { throw 'Plan must report the requested post-install start.' }
$ticketPlan = & $install -Role Agent -PublishDirectory $root -DataDirectory (Join-Path $root 'data') `
    -ConfigurationFile (Join-Path $root 'config.json') -TrustedRootThumbprint ('A' * 40) -EnrollmentTicket ('A' * 43) -Plan
if (-not $ticketPlan.EnrollmentTicketStored -or $ticketPlan.PSObject.Properties.Name -contains 'EnrollmentTicket') { throw 'Service plan must store the ticket without exposing its value.' }
$controller = & $install -Role Controller -PublishDirectory $root -DataDirectory (Join-Path $root 'data') `
    -ConfigurationFile (Join-Path $root 'config.json') -TrustedRootThumbprint ('A' * 40) -Plan
if ($controller.Startup -ne 'auto') { throw 'Controller automatic startup plan missing.' }
$controllerInstallRejected = $false
try {
    & $install -Role Controller -PublishDirectory $root -DataDirectory (Join-Path $root 'controller-data') `
        -ConfigurationFile (Join-Path $root 'controller.json') -TrustedRootThumbprint ('A' * 40) | Out-Null
} catch { $controllerInstallRejected = $_.Exception.Message -like '*Controller service installation is not enabled*' }
if (-not $controllerInstallRejected) { throw 'Live Controller service installation must remain guarded until separately reviewed.' }
$plans = @(& $publish -Role Both -OutputDirectory $root -Plan)
if ($plans.Count -ne 2 -or $plans[0].Output -eq $plans[1].Output) { throw 'Publish roles must have distinct output directories.' }
$relativeRejected = $false
try { & $install -Role Agent -PublishDirectory '.\relative' -DataDirectory '.\data' -ConfigurationFile '.\config.json' -TrustedRootThumbprint ('A' * 40) -Plan | Out-Null }
catch { $relativeRejected = $true }
if (-not $relativeRejected) { throw 'Relative install paths must be rejected.' }
if (Test-Path -LiteralPath $root) { throw 'Plan mode must not create files.' }
Write-Output 'Installer/publisher plan checks passed; no services or files changed.'
