[CmdletBinding(SupportsShouldProcess=$true)]
param(
 [Parameter(Mandatory)][ValidateSet('Controller','Agent','Both')][string]$Role,
 [Parameter(Mandatory)][string]$OutputDirectory,
 [ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64',
 [switch]$Plan
)
$ErrorActionPreference='Stop'
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){throw 'OutputDirectory must be absolute.'}
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$roles=if($Role -eq 'Both'){@('Controller','Agent')}else{@($Role)}
foreach($item in $roles){
 $project=Join-Path $repo "services/$item/$item.csproj"
 $output=Join-Path $OutputDirectory $item
 if($Plan){[pscustomobject]@{Project=$project;Output=$output;Runtime=$Runtime;SelfContained=$true};continue}
 if($PSCmdlet.ShouldProcess($output,"Publish $item")){
  & dotnet publish $project -c Release -r $Runtime --self-contained true -o $output
  if($LASTEXITCODE -ne 0){throw "Publishing $item failed."}
 }
}
