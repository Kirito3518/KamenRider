[CmdletBinding(SupportsShouldProcess=$true)]
param(
 [Parameter(Mandatory)][ValidateSet('Controller','Agent','Both')][string]$Role,
 [Parameter(Mandatory)][string]$OutputDirectory,
 [ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64',
 [string]$Version='1.0.0',
 [switch]$Plan
)
$ErrorActionPreference='Stop'
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){throw 'OutputDirectory must be absolute.'}
if($Version.Length -gt 64){throw 'Publish version must not exceed 64 characters.'}
if($Version -notmatch '\A(?:\d+\.\d+\.\d+|1\.0\.0\+g[0-9a-f]{12,40}(?:\.dirty\.\d{14})?)\z'){throw 'Invalid publish version.'}
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$roles=if($Role -eq 'Both'){@('Controller','Agent')}else{@($Role)}
foreach($item in $roles){
 $project=Join-Path $repo "services/$item/$item.csproj"
 $output=Join-Path $OutputDirectory $item
 if($Plan){[pscustomobject]@{Project=$project;Output=$output;Runtime=$Runtime;SelfContained=$true;Version=$Version};continue}
 if($PSCmdlet.ShouldProcess($output,"Publish $item")){
  & dotnet publish $project -c Release -r $Runtime --self-contained true -o $output "-p:Version=$Version" -p:IncludeSourceRevisionInInformationalVersion=false
  if($LASTEXITCODE -ne 0){throw "Publishing $item failed."}
 }
}
