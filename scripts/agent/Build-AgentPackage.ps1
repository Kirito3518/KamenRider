[CmdletBinding(SupportsShouldProcess=$true)]
param(
 [string]$OutputDirectory,
 [ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64',
 [string]$ReleaseVersion,
 [switch]$Plan
)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$revisionLines=@(& git -C $repo rev-parse --short=12 HEAD)
if($LASTEXITCODE -ne 0){throw 'Git source revision is required to version the Agent package.'}
$revision=($revisionLines -join '').Trim().ToLowerInvariant()
if($revision -notmatch '\A[0-9a-f]{12,40}\z'){throw 'Git returned an invalid source revision.'}
if($revision.Length -gt 12){$revision=$revision.Substring(0,12)}
$statusLines=@(& git -C $repo status --porcelain --untracked-files=all)
if($LASTEXITCODE -ne 0){throw 'Git working-tree status is required to version the Agent package.'}
$agentVersion="1.0.0+g$revision"
if($statusLines.Count -gt 0){$agentVersion+='.dirty.'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss',[Globalization.CultureInfo]::InvariantCulture)}
if($ReleaseVersion -and ($ReleaseVersion.Length -gt 64 -or $ReleaseVersion -notmatch '\A(?:\d+\.\d+\.\d+|1\.0\.0\+g[0-9a-f]{12,40}(?:\.dirty\.\d{14})?)\z')){throw 'ReleaseVersion must be a supported informational version.'}
$releaseBuild= -not [string]::IsNullOrWhiteSpace($ReleaseVersion)
if(-not $OutputDirectory){$OutputDirectory=if($releaseBuild){Join-Path $repo 'agent-release-artifacts'}else{Join-Path $repo 'public/agent'}}
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){throw 'OutputDirectory must be absolute.'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$publicRoot=[IO.Path]::GetFullPath((Join-Path $repo 'public')).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
if($releaseBuild -and ($OutputDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar).StartsWith($publicRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Release packages must be written outside public/.'}
$project=Join-Path $repo 'services/Agent/Agent.csproj'
$archive=Join-Path $OutputDirectory 'AgentPackage.zip'
$hashFile=Join-Path $OutputDirectory 'AgentPackage.sha256'
$manifestPath='AgentPackage.manifest.json'
$agentVersion=if($releaseBuild){$ReleaseVersion}else{$agentVersion}
$manifest=[pscustomobject]@{packageFormatVersion=1;agentInformationalVersion=$agentVersion;sourceCommit=$revision;sourceDirty=($statusLines.Count -gt 0);releaseBuild=$releaseBuild}
$scriptNames=@('Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Prepare-SunshineStreamGate.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1')
if($releaseBuild){$scriptNames+=@('Update-AgentPackage.ps1','Update-Agent.ps1')}
$entries=@('Agent')+$scriptNames+$manifestPath
if($Plan){
 [pscustomobject]@{ProjectPath=$project;OutputDirectory=$OutputDirectory;Runtime=$Runtime;SelfContained=$true;ArchivePath=$archive;HashFilePath=$hashFile;PackageEntries=$entries;AgentVersion=$agentVersion;ReleaseVersion=$(if($releaseBuild){$ReleaseVersion}else{$null});ReleaseBuild=$releaseBuild;ManifestPath=$manifestPath;Manifest=$manifest}
 return
}
if(-not (Test-Path -LiteralPath $project -PathType Leaf)){throw 'Agent project is missing.'}
foreach($name in $scriptNames){
 $source=if($name -eq 'Update-Agent.ps1'){Join-Path (Split-Path $PSScriptRoot -Parent) '..\public\agent\Update-Agent.ps1'}else{Join-Path $PSScriptRoot $name}
 if(-not (Test-Path -LiteralPath $source -PathType Leaf)){throw "Required installer script is missing: $name"}
}
if(-not $PSCmdlet.ShouldProcess($archive,"Publish self-contained Agent package for $Runtime")){return}
$tempRoot=[IO.Path]::GetTempPath()
$stage=Join-Path $tempRoot ('kr-agent-package-'+[guid]::NewGuid().ToString('N'))
$outputTemp=$null
$outputBackup=$null
try {
 $publishRoot=Join-Path $stage 'publish'
 $packageRoot=Join-Path $stage 'package'
 New-Item -ItemType Directory -Path $stage | Out-Null
 $publishScript=Join-Path $PSScriptRoot 'Publish-Services.ps1'
 & $publishScript -Role Agent -OutputDirectory $publishRoot -Runtime $Runtime -Version $agentVersion -ArtifactsPath (Join-Path $stage 'dotnet-artifacts')
 if($LASTEXITCODE -ne 0){throw 'Agent publish failed.'}
 $publishedAgent=Join-Path $publishRoot 'Agent'
 if(-not (Test-Path -LiteralPath (Join-Path $publishedAgent 'Agent.exe') -PathType Leaf)){throw 'Published Agent.exe is missing.'}
 New-Item -ItemType Directory -Path $packageRoot | Out-Null
 $packageAgent=Join-Path $packageRoot 'Agent'
 New-Item -ItemType Directory -Path $packageAgent | Out-Null
 foreach($item in @(Get-ChildItem -LiteralPath $publishedAgent -Force)){
  if(-not $item.PSIsContainer -and $item.Name -ieq 'appsettings.json'){continue}
  Copy-Item -LiteralPath $item.FullName -Destination $packageAgent -Recurse
 }
 foreach($name in $scriptNames){
  $source=if($name -eq 'Update-Agent.ps1'){Join-Path (Split-Path $PSScriptRoot -Parent) '..\public\agent\Update-Agent.ps1'}else{Join-Path $PSScriptRoot $name}
  Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $name)
 }
 $manifestJson=ConvertTo-Json -InputObject $manifest -Depth 4
 [IO.File]::WriteAllText((Join-Path $packageRoot $manifestPath),$manifestJson+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
 Add-Type -AssemblyName System.IO.Compression
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $stagedArchive=Join-Path $stage 'AgentPackage.zip'
 $zip=[IO.Compression.ZipFile]::Open($stagedArchive,[IO.Compression.ZipArchiveMode]::Create)
 try {
  $packageRootFull=[IO.Path]::GetFullPath($packageRoot).TrimEnd('\')
  foreach($file in @(Get-ChildItem -LiteralPath $packageRootFull -File -Recurse)){
   $entryName=$file.FullName.Substring($packageRootFull.Length).TrimStart('\').Replace('\','/')
   [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$entryName,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
  }
 } finally {$zip.Dispose()}
 $null=New-Item -ItemType Directory -Path $OutputDirectory -Force
 $outputTemp=Join-Path $OutputDirectory ('.AgentPackage-'+[guid]::NewGuid().ToString('N')+'.tmp')
 [IO.File]::Copy($stagedArchive,$outputTemp,$false)
 if(Test-Path -LiteralPath $archive){
  $outputBackup=Join-Path $OutputDirectory ('.AgentPackage-'+[guid]::NewGuid().ToString('N')+'.bak')
  [IO.File]::Replace($outputTemp,$archive,$outputBackup)
  $outputTemp=$null
  [IO.File]::Delete($outputBackup)
  $outputBackup=$null
 }else{
  [IO.File]::Move($outputTemp,$archive)
  $outputTemp=$null
 }
 $hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToUpperInvariant()
 [IO.File]::WriteAllText($hashFile,$hash+[Environment]::NewLine,[Text.Encoding]::ASCII)
 [pscustomobject]@{PackagePath=$archive;Sha256=$hash;HashFilePath=$hashFile;Runtime=$Runtime;AgentVersion=$agentVersion;ReleaseVersion=$(if($releaseBuild){$ReleaseVersion}else{$null});ReleaseBuild=$releaseBuild;ManifestPath=$manifestPath;Manifest=$manifest}
} finally {
 if($outputTemp -and (Test-Path -LiteralPath $outputTemp)){Remove-Item -LiteralPath $outputTemp -Force}
 if($outputBackup -and (Test-Path -LiteralPath $outputBackup)){Remove-Item -LiteralPath $outputBackup -Force}
 $resolved=[IO.Path]::GetFullPath($stage)
 $temp=[IO.Path]::GetFullPath($tempRoot).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-package-*'){throw 'Unsafe package staging cleanup path.'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
