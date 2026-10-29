$ErrorActionPreference='Stop'
$install=Join-Path $PSScriptRoot 'Install-Service.ps1'
$publish=Join-Path $PSScriptRoot 'Publish-Services.ps1'
$root=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'kr-install-test'))
$agent=& $install -Role Agent -PublishDirectory $root -DataDirectory (Join-Path $root 'data') -ConfigurationFile (Join-Path $root 'config.json') -TrustedRootThumbprint ('A'*40) -Plan
if($agent.Startup -ne 'disabled' -or $agent.Account -ne 'NT SERVICE\KamenRaider Agent'){throw 'Agent installation must remain disabled and use the virtual service account.'}
$controller=& $install -Role Controller -PublishDirectory $root -DataDirectory (Join-Path $root 'data') -ConfigurationFile (Join-Path $root 'config.json') -TrustedRootThumbprint ('A'*40) -Plan
if($controller.Startup -ne 'auto'){throw 'Controller automatic startup missing.'}
$plans=@(& $publish -Role Both -OutputDirectory $root -Plan)
if($plans.Count -ne 2 -or $plans[0].Output -eq $plans[1].Output){throw 'Publish roles must have distinct output directories.'}
if(Test-Path -LiteralPath $root){throw 'Plan must not create files.'}
Write-Output 'Installer/publisher plan checks passed; no services or files changed.'
