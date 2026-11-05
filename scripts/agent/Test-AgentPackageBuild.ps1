$ErrorActionPreference='Stop'
$build=Join-Path $PSScriptRoot 'Build-AgentPackage.ps1'
$outputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-package-build-'+[guid]::NewGuid().ToString('N'))
try {
 $output=@(& $build -OutputDirectory $outputDirectory)
 $result=@($output | Where-Object {$_ -and $_.PSObject.Properties['PackagePath']}) | Select-Object -Last 1
 if(-not $result){throw 'Builder did not return its package result.'}
 if($result.AgentVersion -notmatch '^1\.0\.0\+g[0-9a-f]{12}(\.dirty\.\d{14})?$'){throw 'Builder did not return a source-specific Agent version.'}
 $archive=$result.PackagePath
 if(-not (Test-Path -LiteralPath $archive -PathType Leaf)){throw 'Builder did not create its archive.'}
 $actual=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToUpperInvariant()
 $recorded=[IO.File]::ReadAllText($result.HashFilePath).Trim()
 if($actual -ne $recorded -or $actual -ne $result.Sha256){throw 'Generated SHA-256 does not match the archive.'}
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $zip=[IO.Compression.ZipFile]::OpenRead($archive)
 try {
  $entries=@($zip.Entries | ForEach-Object FullName)
  foreach($entry in $entries){if($entry.Contains('\') -or $entry.StartsWith('/') -or $entry.Split('/') -contains '..'){throw "Unsafe or non-portable ZIP path: $entry"}}
  foreach($required in @('Agent/Agent.exe','Install-AgentPackage.ps1','Install-Service.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1')){if($required -notin $entries){throw "Package is missing $required."}}
  if('Agent/appsettings.json' -in $entries){throw 'Package must not include the default appsettings.json; the installer publishes the protected configuration.'}
 } finally {$zip.Dispose()}
 if($PSVersionTable.PSEdition -eq 'Core'){
  $verifyDirectory=Join-Path $outputDirectory 'version-verify'
  [IO.Compression.ZipFile]::ExtractToDirectory($archive,$verifyDirectory)
  $versionProbe=Join-Path $outputDirectory 'Read-AgentVersion.ps1'
  $versionProbeSource=@'
param([Parameter(Mandatory)][string]$AssemblyPath)
$assembly=[Reflection.Assembly]::LoadFrom($AssemblyPath)
$attribute=$assembly.GetCustomAttributes([Reflection.AssemblyInformationalVersionAttribute],$false) | Select-Object -First 1
if(-not $attribute){throw 'Agent assembly has no informational version.'}
Write-Output $attribute.InformationalVersion
'@
  [IO.File]::WriteAllText($versionProbe,$versionProbeSource,[Text.Encoding]::UTF8)
  $agentDll=Join-Path $verifyDirectory 'Agent/Agent.dll'
  $reportedVersion=@(& ([Environment]::ProcessPath) -NoProfile -File $versionProbe -AssemblyPath $agentDll)
  if($LASTEXITCODE -ne 0 -or ($reportedVersion | Select-Object -Last 1) -ne $result.AgentVersion){throw 'Published Agent assembly version does not match the package version.'}
 }
 $secondOutput=@(& $build -OutputDirectory $outputDirectory)
 $secondResult=@($secondOutput | Where-Object {$_ -and $_.PSObject.Properties['PackagePath']}) | Select-Object -Last 1
 if(-not $secondResult){throw 'Repeated builder run did not return its package result.'}
 $secondHash=(Get-FileHash -LiteralPath $secondResult.PackagePath -Algorithm SHA256).Hash.ToUpperInvariant()
 if($secondHash -ne $secondResult.Sha256 -or $secondHash -ne [IO.File]::ReadAllText($secondResult.HashFilePath).Trim()){throw 'Repeated build archive SHA-256 did not match.'}
 if(@(Get-ChildItem -LiteralPath $outputDirectory -Force | Where-Object {$_.Name -like '.AgentPackage-*' -or $_.Name -like '*.bak'}).Count -ne 0){throw 'Repeated build left a package staging or backup file.'}
 Write-Output "Agent package build test passed for $($result.Runtime); SHA256=$actual."
} finally {
 $resolved=[IO.Path]::GetFullPath($outputDirectory)
 $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-package-build-*'){throw 'Unsafe package test cleanup path.'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
