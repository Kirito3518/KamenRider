[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$ControllerUrl,
 [Parameter(Mandatory)][string]$PackageUrl,
 [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$PackageSha256,
 [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$TrustedRootThumbprint
)
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Open PowerShell as Administrator and run the command again.'}
foreach($value in @($ControllerUrl,$PackageUrl)){
 $uri=[uri]$value
 if($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment){throw 'Controller and package URLs must be clean HTTPS URLs.'}
}
$stage=Join-Path $env:TEMP ('kr-agent-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
 $archive=Join-Path $stage 'agent.zip'
 Invoke-WebRequest -Uri $PackageUrl -OutFile $archive -ErrorAction Stop
 if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $PackageSha256.ToUpperInvariant()){throw 'Agent package hash mismatch.'}
 $expanded=Join-Path $stage 'package'
 Expand-Archive -LiteralPath $archive -DestinationPath $expanded -ErrorAction Stop
 $installer=Join-Path $expanded 'Install-AgentPackage.ps1'
 if(-not (Test-Path -LiteralPath $installer -PathType Leaf)){throw 'Agent package has no qualified installer.'}
 & $installer -ControllerUrl $ControllerUrl -TrustedRootThumbprint $TrustedRootThumbprint
 if(-not $?){throw 'Agent package installer failed.'}
}finally{
 $resolved=[IO.Path]::GetFullPath($stage)
 $temp=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-*'){throw 'Unsafe installer cleanup path.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
