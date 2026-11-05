[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$envFile = Join-Path $repo '.env.local'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw '.env.local is missing.' }

$values = @{}
foreach ($line in Get-Content -LiteralPath $envFile) {
    if ($line -match '^\s*(CONTROLLER_INTERNAL_URL|CONTROLLER_SERVICE_CREDENTIAL|CONTROLLER_AGENT_URL|CONTROLLER_DATABASE_PATH)\s*=(.*)$') {
        $values[$Matches[1]] = $Matches[2].Trim()
    }
}
foreach ($name in @('CONTROLLER_INTERNAL_URL','CONTROLLER_SERVICE_CREDENTIAL','CONTROLLER_AGENT_URL','CONTROLLER_DATABASE_PATH')) {
    if (-not $values[$name]) { throw "Set $name in .env.local first." }
}
if ($values.CONTROLLER_INTERNAL_URL -ne 'http://127.0.0.1:5081' -or $values.CONTROLLER_AGENT_URL -ne 'https://localhost:7443') {
    throw 'This launcher is restricted to the configured localhost test endpoints.'
}
if ($values.CONTROLLER_DATABASE_PATH -notmatch '^[A-Za-z]:\\') { throw 'CONTROLLER_DATABASE_PATH must be an absolute drive path.' }

$env:InternalUrl = $values.CONTROLLER_INTERNAL_URL
$env:AgentUrl = $values.CONTROLLER_AGENT_URL
$env:DatabasePath = $values.CONTROLLER_DATABASE_PATH
$env:ServiceCredential = $values.CONTROLLER_SERVICE_CREDENTIAL
$env:DOTNET_ENVIRONMENT = 'Development'

Push-Location $repo
try {
    & dotnet run --no-launch-profile --project '.\services\Controller\Controller.csproj'
    if ($LASTEXITCODE -ne 0) { throw "Controller exited with code $LASTEXITCODE." }
} finally {
    Pop-Location
}
