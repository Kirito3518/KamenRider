$ErrorActionPreference='Stop'
$build=Join-Path $PSScriptRoot 'Build-AgentPackage.ps1'
$root=Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-package-plan-'+[guid]::NewGuid().ToString('N'))
$root=[IO.Path]::GetFullPath($root)
$default=& $build -Plan
if($default.Runtime -ne 'win-x64' -or -not $default.SelfContained){throw 'Default package plan must target self-contained win-x64.'}
if($default.AgentVersion -notmatch '^1\.0\.0\+g[0-9a-f]{12}(\.dirty\.\d{14})?$'){throw 'Agent package version must identify the source revision and mark dirty builds.'}
if($default.ArchivePath -ne [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\public\agent\AgentPackage.zip'))){throw 'Default archive path must be public/agent/AgentPackage.zip.'}
$archiveBefore=if(Test-Path -LiteralPath $default.ArchivePath){(Get-FileHash -LiteralPath $default.ArchivePath -Algorithm SHA256).Hash}else{$null}
$plan=& $build -OutputDirectory $root -Runtime 'win-arm64' -Plan
if($plan.Runtime -ne 'win-arm64' -or $plan.OutputDirectory -ne $root){throw 'Explicit package plan did not preserve requested settings.'}
if(-not [IO.Path]::IsPathRooted($plan.ProjectPath)){throw 'Agent project path must be absolute.'}
$required=@('Agent','Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Prepare-SunshineStreamGate.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1','AgentPackage.manifest.json')
if(@($plan.PackageEntries).Count -ne $required.Count){throw 'Package plan must list each required package entry.'}
foreach($entry in $required){if($entry -notin $plan.PackageEntries){throw "Package plan is missing $entry."}}
if(Test-Path -LiteralPath $root){throw 'Plan must not create the requested output directory.'}
if($archiveBefore -ne $null){if((Get-FileHash -LiteralPath $default.ArchivePath -Algorithm SHA256).Hash -ne $archiveBefore){throw 'Default Plan changed an existing archive.'}}elseif(Test-Path -LiteralPath $default.ArchivePath){throw 'Default Plan created the archive.'}
$release=& $build -ReleaseVersion '2.3.4' -Plan
if($release.AgentVersion -ne '2.3.4' -or $release.ReleaseVersion -ne '2.3.4'){throw 'Release plan must use the explicit release version.'}
foreach($entry in @('Update-AgentPackage.ps1','Update-Agent.ps1')){if($entry -notin $release.PackageEntries){throw "Release package plan is missing $entry."}}
foreach($entry in @('Update-AgentPackage.ps1','Update-Agent.ps1')){if($entry -in $default.PackageEntries){throw "Enrollment package plan must not change by including $entry."}}
if($release.ManifestPath -ne 'AgentPackage.manifest.json' -or $release.Manifest.packageFormatVersion -ne 1){throw 'Release plan must include the versioned package manifest.'}
$publicRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\public'))+[IO.Path]::DirectorySeparatorChar
if($release.ArchivePath.StartsWith($publicRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Default release output must be private and outside public/.'}
$statusLines=@(& git -C ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))) status --porcelain --untracked-files=all)
if($release.Manifest.sourceDirty -ne ($statusLines.Count -gt 0) -or -not $release.Manifest.releaseBuild){throw 'Release plan must report the source cleanliness and release-build marker.'}
$rejected=$false
try {& $build -OutputDirectory 'relative-output' -Plan} catch {$rejected=$true}
if(-not $rejected){throw 'Relative output paths must be rejected.'}
$rejected=$false
try {& $build -ReleaseVersion 'not-a-version' -Plan} catch {$rejected=$true}
if(-not $rejected){throw 'Invalid explicit release versions must be rejected.'}
$rejected=$false
try {& $build -ReleaseVersion '2.3.4' -OutputDirectory ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\public\releases')) ) -Plan} catch {$rejected=$true}
if(-not $rejected){throw 'Explicit release output under public/ must be rejected.'}
$publish=Join-Path $PSScriptRoot 'Publish-Services.ps1'
$oversizeVersion='1.0.0+g'+('a'*40)+'.dirty.20261106120000'
$rejected=$false
try {& $publish -Role Agent -OutputDirectory (Join-Path $root 'publish') -Version $oversizeVersion -Plan} catch {$rejected=$true}
if(-not $rejected){throw 'Publish versions longer than the Controller heartbeat limit must be rejected.'}
Write-Output 'Agent package plan tests passed; release mode is private and no files or services changed.'
