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
throw 'Service installation is disabled until directory trust and failure recovery are verified. Use -Plan for inspection only.'
if(-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core'){throw 'Windows is required.'}
if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run installation as an elevated administrator.'}
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')
. (Join-Path $PSScriptRoot 'ServiceInstallTransaction.ps1')
Assert-TrustedInstallParent $PublishDirectory
Assert-TrustedInstallParent $DataDirectory
Assert-TrustedInstallParent $ConfigurationFile
Assert-EmptyInstallDirectory $DataDirectory
if(-not (Test-Path -LiteralPath $executable -PathType Leaf)){throw 'Publish executable is missing.'}
if(-not (Test-Path -LiteralPath $ConfigurationFile -PathType Leaf)){throw 'Provide a provisioned configuration file.'}
Assert-TrustedPublishTree $PublishDirectory
Assert-TrustedInstallFile $executable
Assert-TrustedInstallFile $ConfigurationFile -Private
if(-not (Test-Path -LiteralPath "Cert:\LocalMachine\Root\$TrustedRootThumbprint")){throw 'Provision and verify the certificate trust chain before installation.'}
if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw 'Service already exists; use a reviewed upgrade procedure.'}
if($PSCmdlet.ShouldProcess($serviceName,'Create service and grant private data/config ACLs')) {
 $binaryPath='"'+$executable+'" --contentRoot "'+$PublishDirectory+'"'
 Invoke-ServiceInstallTransaction {
  & sc.exe create $serviceName binPath= $binaryPath start= disabled obj= $account
  if($LASTEXITCODE -ne 0){throw 'Service creation failed.'}
 } {
 $serviceSid=([Security.Principal.NTAccount]::new($account)).Translate([Security.Principal.SecurityIdentifier])
 # No Users/Everyone grants. Data must be empty; existing descendants are never reused.
 Initialize-PrivateDataDirectory $DataDirectory $serviceSid
 Publish-PrivateConfiguration $ConfigurationFile $PublishDirectory $serviceSid
 & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
 if($LASTEXITCODE -ne 0){throw 'Recovery configuration failed.'}
 & sc.exe config $serviceName start= $startup
 if($LASTEXITCODE -ne 0){throw 'Startup configuration failed.'}
 } {
  & sc.exe delete $serviceName
  if($LASTEXITCODE -ne 0){throw 'Unable to remove the service after configuration failure.'}
 }
 Write-Output "Installed $serviceName as $account, startup=$startup. No service was started."
}
