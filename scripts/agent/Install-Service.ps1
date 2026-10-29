[CmdletBinding(SupportsShouldProcess=$true)]
param(
 [Parameter(Mandatory)][ValidateSet('Controller','Agent')][string]$Role,
 [Parameter(Mandatory)][string]$PublishDirectory,
 [Parameter(Mandatory)][string]$DataDirectory,
 [Parameter(Mandatory)][string]$ConfigurationFile,
 [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$TrustedRootThumbprint,
 [switch]$Plan
)
$ErrorActionPreference='Stop'
foreach($path in @($PublishDirectory,$DataDirectory,$ConfigurationFile)) {
 if(-not [IO.Path]::IsPathRooted($path)){throw 'All installation paths must be absolute.'}
}
$serviceName="KamenRaider $Role"
$account="NT SERVICE\$serviceName"
$executable=Join-Path $PublishDirectory "$Role.exe"
# Agent remains disabled until real default-closed fencing, restore proof and readiness are qualified.
$startup=if($Role -eq 'Agent'){'disabled'}else{'auto'}
if($Plan){[pscustomobject]@{ServiceName=$serviceName;Account=$account;Binary=$executable;DataDirectory=$DataDirectory;Startup=$startup;TrustedRootThumbprint=$TrustedRootThumbprint;Recovery='restart after 5/15/60 seconds'};return}
if(-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core'){throw 'Windows is required.'}
if(-not (Test-Path -LiteralPath $executable -PathType Leaf)){throw 'Publish executable is missing.'}
if(-not (Test-Path -LiteralPath $ConfigurationFile -PathType Leaf)){throw 'Provide a provisioned configuration file.'}
if(-not (Test-Path -LiteralPath "Cert:\LocalMachine\Root\$TrustedRootThumbprint")){throw 'Provision and verify the certificate trust chain before installation.'}
if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw 'Service already exists; use a reviewed upgrade procedure.'}
if($PSCmdlet.ShouldProcess($serviceName,'Create service and grant private data/config ACLs')) {
 New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
 $binaryPath='"'+$executable+'" --contentRoot "'+$PublishDirectory+'"'
 & sc.exe create $serviceName binPath= $binaryPath start= disabled obj= $account
 if($LASTEXITCODE -ne 0){throw 'Service creation failed.'}
 # No Users/Everyone grants. Operators provision certificate private-key permissions separately.
 & icacls.exe $DataDirectory /reset
 if($LASTEXITCODE -ne 0){throw 'Data ACL reset failed.'}
 & icacls.exe $DataDirectory /inheritance:r /grant:r "${account}:(OI)(CI)M" 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F'
 if($LASTEXITCODE -ne 0){throw 'Data ACL failed; service has not been started.'}
 Copy-Item -LiteralPath $ConfigurationFile -Destination (Join-Path $PublishDirectory 'appsettings.json') -Force
 & icacls.exe (Join-Path $PublishDirectory 'appsettings.json') /reset
 if($LASTEXITCODE -ne 0){throw 'Configuration ACL reset failed.'}
 & icacls.exe (Join-Path $PublishDirectory 'appsettings.json') /inheritance:r /grant:r "${account}:R" 'SYSTEM:F' 'BUILTIN\Administrators:F'
 if($LASTEXITCODE -ne 0){throw 'Configuration ACL failed; service has not been started.'}
 & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
 if($LASTEXITCODE -ne 0){throw 'Recovery configuration failed.'}
 & sc.exe config $serviceName start= $startup
 if($LASTEXITCODE -ne 0){throw 'Startup configuration failed.'}
 Write-Output "Installed $serviceName as $account, startup=$startup. No service was started."
}
