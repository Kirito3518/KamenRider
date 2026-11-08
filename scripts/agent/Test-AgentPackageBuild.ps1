$ErrorActionPreference='Stop'
$build=Join-Path $PSScriptRoot 'Build-AgentPackage.ps1'
$outputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-package-build-'+[guid]::NewGuid().ToString('N'))
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sourceStatus=@(& git -C $repo status --porcelain --untracked-files=all)
$expectedSourceDirty=$sourceStatus.Count -gt 0
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
  foreach($required in @('Agent/Agent.exe','Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Prepare-SunshineStreamGate.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1')){if($required -notin $entries){throw "Package is missing $required."}}
  $defaultManifestEntry=$zip.GetEntry('AgentPackage.manifest.json')
  if(-not $defaultManifestEntry){throw 'Enrollment package is missing its manifest.'}
  $defaultManifestReader=[IO.StreamReader]::new($defaultManifestEntry.Open())
  try {$defaultManifest=$defaultManifestReader.ReadToEnd() | ConvertFrom-Json} finally {$defaultManifestReader.Dispose()}
  if($defaultManifest.packageFormatVersion -ne 1 -or $defaultManifest.agentInformationalVersion -ne $result.AgentVersion -or $defaultManifest.sourceCommit -notmatch '\A[0-9a-f]{12}\z' -or $defaultManifest.sourceDirty -ne $expectedSourceDirty -or $defaultManifest.releaseBuild){throw 'Enrollment package manifest values are invalid.'}
  if(@($entries | Where-Object {$_ -match '(?i)(^|/)tightvnc[^/]*\.msi$'}).Count -ne 0){throw 'The separately licensed TightVNC MSI must not be bundled with the KamenRaider Agent package.'}
  if('Agent/appsettings.json' -in $entries){throw 'Package must not include the default appsettings.json; the installer publishes the protected configuration.'}
 } finally {$zip.Dispose()}
 if($PSVersionTable.PSEdition -eq 'Core'){
  $verifyDirectory=Join-Path $outputDirectory 'version-verify'
  [IO.Compression.ZipFile]::ExtractToDirectory($archive,$verifyDirectory)
  $dependencyManifest=Join-Path $verifyDirectory 'Agent/Agent.deps.json'
  if(-not (Test-Path -LiteralPath $dependencyManifest -PathType Leaf)){throw 'Published Agent dependency manifest is missing.'}
  $dependencies=[IO.File]::ReadAllText($dependencyManifest) | ConvertFrom-Json
  $expectedLibraryId="Agent/$($result.AgentVersion)"
  $projectLibrary=$dependencies.libraries.PSObject.Properties[$expectedLibraryId]
  if(-not $projectLibrary -or $projectLibrary.Value.type -ne 'project'){throw 'Published Agent dependency identity does not match the package version.'}
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
 $releaseOutput=@(& $build -OutputDirectory (Join-Path $outputDirectory 'release') -ReleaseVersion '2.3.4')
 $releaseResult=@($releaseOutput | Where-Object {$_ -and $_.PSObject.Properties['PackagePath']}) | Select-Object -Last 1
 if(-not $releaseResult -or $releaseResult.AgentVersion -ne '2.3.4' -or $releaseResult.ReleaseVersion -ne '2.3.4'){throw 'Release package did not use the explicit version.'}
 if([IO.Path]::GetFullPath($releaseResult.PackagePath).StartsWith([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\public'))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Release package was written under public/.'}
 $releaseZip=[IO.Compression.ZipFile]::OpenRead($releaseResult.PackagePath)
 try {
  $manifestEntry=$releaseZip.GetEntry('AgentPackage.manifest.json')
  if(-not $manifestEntry){throw 'Release archive is missing its manifest.'}
  $manifestReader=[IO.StreamReader]::new($manifestEntry.Open())
  try {$manifest=$manifestReader.ReadToEnd() | ConvertFrom-Json} finally {$manifestReader.Dispose()}
  if($manifest.packageFormatVersion -ne 1 -or $manifest.agentInformationalVersion -ne '2.3.4' -or $manifest.sourceCommit -notmatch '\A[0-9a-f]{12}\z' -or $manifest.sourceDirty -ne $expectedSourceDirty -or -not $manifest.releaseBuild){throw 'Release manifest values are invalid.'}
  if($manifestEntry.Name -cne 'AgentPackage.manifest.json'){throw 'Manifest path must be stable and case-sensitive.'}
 } finally {$releaseZip.Dispose()}
 if($PSVersionTable.PSEdition -eq 'Core'){
  $releaseVerifyDirectory=Join-Path $outputDirectory 'release-version-verify'
  [IO.Compression.ZipFile]::ExtractToDirectory($releaseResult.PackagePath,$releaseVerifyDirectory)
  $versionProbe=Join-Path $outputDirectory 'Read-AgentVersion.ps1'
  $versionProbeSource=@'
param([Parameter(Mandatory)][string]$AssemblyPath)
$assembly=[Reflection.Assembly]::LoadFrom($AssemblyPath)
$attribute=$assembly.GetCustomAttributes([Reflection.AssemblyInformationalVersionAttribute],$false) | Select-Object -First 1
if(-not $attribute){throw 'Agent assembly has no informational version.'}
Write-Output $attribute.InformationalVersion
'@
  [IO.File]::WriteAllText($versionProbe,$versionProbeSource,[Text.Encoding]::UTF8)
  $releaseAgentDll=Join-Path $releaseVerifyDirectory 'Agent/Agent.dll'
  $releaseReportedVersion=@(& ([Environment]::ProcessPath) -NoProfile -File $versionProbe -AssemblyPath $releaseAgentDll)
  if($LASTEXITCODE -ne 0 -or ($releaseReportedVersion | Select-Object -Last 1) -ne $releaseResult.AgentVersion){throw 'Release Agent assembly informational version does not match the selected release version.'}
 }
 if(@(Get-ChildItem -LiteralPath $outputDirectory -Force | Where-Object {$_.Name -like '.AgentPackage-*' -or $_.Name -like '*.bak'}).Count -ne 0){throw 'Repeated build left a package staging or backup file.'}
 Write-Output "Agent package build test passed for $($result.Runtime); SHA256=$actual."
} finally {
 $resolved=[IO.Path]::GetFullPath($outputDirectory)
 $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-package-build-*'){throw 'Unsafe package test cleanup path.'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
