[CmdletBinding(SupportsShouldProcess=$true)]
param(
 [string]$OutputDirectory,
 [ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64',
 [switch]$Plan
)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(-not $OutputDirectory){$OutputDirectory=Join-Path $repo 'public/agent'}
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){throw 'OutputDirectory must be absolute.'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$project=Join-Path $repo 'services/Agent/Agent.csproj'
$archive=Join-Path $OutputDirectory 'AgentPackage.zip'
$hashFile=Join-Path $OutputDirectory 'AgentPackage.sha256'
$scriptNames=@('Install-AgentPackage.ps1','Install-Service.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1')
$entries=@('Agent')+$scriptNames
if($Plan){
 [pscustomobject]@{ProjectPath=$project;OutputDirectory=$OutputDirectory;Runtime=$Runtime;SelfContained=$true;ArchivePath=$archive;HashFilePath=$hashFile;PackageEntries=$entries}
 return
}
if(-not (Test-Path -LiteralPath $project -PathType Leaf)){throw 'Agent project is missing.'}
foreach($name in $scriptNames){if(-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)){throw "Required installer script is missing: $name"}}
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
 & $publishScript -Role Agent -OutputDirectory $publishRoot -Runtime $Runtime
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
 foreach($name in $scriptNames){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $packageRoot $name)}
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
 [pscustomobject]@{PackagePath=$archive;Sha256=$hash;HashFilePath=$hashFile;Runtime=$Runtime}
} finally {
 if($outputTemp -and (Test-Path -LiteralPath $outputTemp)){Remove-Item -LiteralPath $outputTemp -Force}
 if($outputBackup -and (Test-Path -LiteralPath $outputBackup)){Remove-Item -LiteralPath $outputBackup -Force}
 $resolved=[IO.Path]::GetFullPath($stage)
 $temp=[IO.Path]::GetFullPath($tempRoot).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-package-*'){throw 'Unsafe package staging cleanup path.'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
