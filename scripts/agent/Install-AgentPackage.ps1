[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidatePattern('^https://')][string]$ControllerUrl,
 [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$TrustedRootThumbprint,
 [ValidateRange(1,65535)][int]$MoonlightPort=47989,
 [ValidateRange(1,65535)][int]$SunshinePort=47990,
 [string]$NetworkAdapterId,
 [switch]$Plan
)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Windows is required.'}
$controller=[Uri]$ControllerUrl
if($controller.Scheme -ne 'https' -or -not $controller.IsAbsoluteUri -or $controller.UserInfo -or $controller.Fragment){throw 'ControllerUrl must be an HTTPS origin.'}
if(-not $NetworkAdapterId){
 $candidates=@(Get-NetIPConfiguration | Where-Object {$_.IPv4Address -and $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up'})
 if($candidates.Count -ne 1){throw 'Select the active network adapter explicitly with -NetworkAdapterId.'}
 $NetworkAdapterId=$candidates[0].NetAdapter.InterfaceGuid.ToString()
}
$publish=Join-Path $PSScriptRoot 'Agent'
$installer=Join-Path $PSScriptRoot 'Install-Service.ps1'
if(-not (Test-Path -LiteralPath (Join-Path $publish 'Agent.exe') -PathType Leaf)){throw 'Agent.exe is missing from the package.'}
if(-not (Test-Path -LiteralPath $installer -PathType Leaf)){throw 'Install-Service.ps1 is missing from the package.'}
$installDirectory='C:\Program Files\KamenRaider\Agent'
$dataDirectory='C:\ProgramData\KamenRaider\Agent'
if($Plan){
 [pscustomobject]@{ControllerUrl=$controller.AbsoluteUri;PublishDirectory=$publish;InstallDirectory=$installDirectory;DataDirectory=$dataDirectory;MoonlightPort=$MoonlightPort;SunshinePort=$SunshinePort;NetworkAdapterId=$NetworkAdapterId;ServiceStartup='disabled'}
 return
}
throw 'Agent package installation is disabled while Install-Service.ps1 remains guarded. No files or services were changed.'
