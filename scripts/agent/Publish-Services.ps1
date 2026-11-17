[CmdletBinding(SupportsShouldProcess=$true)]
param(
 [Parameter(Mandatory)][ValidateSet('Controller','Agent','Both')][string]$Role,
 [Parameter(Mandatory)][string]$OutputDirectory,
 [ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64',
 [string]$Version='1.0.0',
 [ValidatePattern('^net[0-9]+\.0$')][string]$Framework='',
 [string]$ArtifactsPath,
 [switch]$Plan
)
$ErrorActionPreference='Stop'
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){throw 'OutputDirectory must be absolute.'}
if($Version.Length -gt 64){throw 'Publish version must not exceed 64 characters.'}
if($Version -notmatch '\A(?:\d+\.\d+\.\d+|1\.0\.0\+g[0-9a-f]{12,40}(?:\.dirty\.\d{14})?)\z'){throw 'Invalid publish version.'}
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$roles=if($Role -eq 'Both'){@('Controller','Agent')}else{@($Role)}
$ownsArtifactsPath=[string]::IsNullOrWhiteSpace($ArtifactsPath)
if($ownsArtifactsPath){$ArtifactsPath=Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-publish-'+[guid]::NewGuid().ToString('N'))}
if(-not [IO.Path]::IsPathRooted($ArtifactsPath)){throw 'ArtifactsPath must be absolute.'}
$ArtifactsPath=[IO.Path]::GetFullPath($ArtifactsPath)
try {
 foreach($item in $roles){
  $project=Join-Path $repo "services/$item/$item.csproj"
  $output=Join-Path $OutputDirectory $item
  if($Plan){[pscustomobject]@{Project=$project;Output=$output;ArtifactsPath=$ArtifactsPath;Runtime=$Runtime;SelfContained=$true;Version=$Version};continue}
  if($PSCmdlet.ShouldProcess($output,"Publish $item")){
   $frameworkArgs=@(); if(-not [string]::IsNullOrWhiteSpace($Framework)){$frameworkArgs=@('-f',$Framework)}
   & dotnet publish $project -c Release -r $Runtime --self-contained true -o $output --artifacts-path $ArtifactsPath @frameworkArgs "-p:Version=$Version" -p:IncludeSourceRevisionInInformationalVersion=false
   if($LASTEXITCODE -ne 0){throw "Publishing $item failed."}
  }
 }
} finally {
 if($ownsArtifactsPath -and (Test-Path -LiteralPath $ArtifactsPath)){
  $resolvedArtifacts=[IO.Path]::GetFullPath($ArtifactsPath)
  $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
  if(-not $resolvedArtifacts.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedArtifacts) -notlike 'kr-agent-publish-*'){throw 'Unsafe publish-artifact cleanup path.'}
  Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
 }
}
