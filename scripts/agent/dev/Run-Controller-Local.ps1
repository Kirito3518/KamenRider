[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$envFile = Join-Path $repo '.env.local'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { throw '.env.local is missing.' }

$values = @{}
foreach ($line in Get-Content -LiteralPath $envFile) {
    if ($line -match '^\s*(CONTROLLER_INTERNAL_URL|CONTROLLER_SERVICE_CREDENTIAL|CONTROLLER_AGENT_URL|CONTROLLER_DATABASE_PATH|CONTROLLER_WAKE_INTERFACE_ADDRESS|CONTROLLER_WAKE_BROADCAST_ADDRESS|CONTROLLER_PUBLIC_IP_ENDPOINT|CONTROLLER_SUNSHINE_CREDENTIAL_KEY)\s*=(.*)$') {
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

$wakeInterfaceAddress = $values['CONTROLLER_WAKE_INTERFACE_ADDRESS']
$wakeBroadcastAddress = $values['CONTROLLER_WAKE_BROADCAST_ADDRESS']
$hasWakeInterfaceAddress = -not [string]::IsNullOrWhiteSpace($wakeInterfaceAddress)
$hasWakeBroadcastAddress = -not [string]::IsNullOrWhiteSpace($wakeBroadcastAddress)
if ($hasWakeInterfaceAddress -ne $hasWakeBroadcastAddress) {
    throw 'Set both CONTROLLER_WAKE_INTERFACE_ADDRESS and CONTROLLER_WAKE_BROADCAST_ADDRESS, or omit both.'
}
if ($hasWakeInterfaceAddress) {
    foreach ($name in @('CONTROLLER_WAKE_INTERFACE_ADDRESS', 'CONTROLLER_WAKE_BROADCAST_ADDRESS')) {
        $parsedAddress = $null
        if (-not [System.Net.IPAddress]::TryParse($values[$name], [ref]$parsedAddress) -or
            $parsedAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
            throw "$name must be an IPv4 address."
        }
    }
}

# Optional public-IP lookup consumed by PublicIpJob (renter connect address). The Controller only
# starts the job for an absolute HTTPS endpoint.
$publicIpEndpoint = $values['CONTROLLER_PUBLIC_IP_ENDPOINT']
if (-not [string]::IsNullOrWhiteSpace($publicIpEndpoint)) {
    $parsedEndpoint = $null
    if (-not [System.Uri]::TryCreate($publicIpEndpoint, [System.UriKind]::Absolute, [ref]$parsedEndpoint) -or
        $parsedEndpoint.Scheme -ne 'https') {
        throw 'CONTROLLER_PUBLIC_IP_ENDPOINT must be an absolute HTTPS URL.'
    }
}

# Optional fleet-wide Sunshine management credential key: base64 of exactly 32 random bytes.
# An empty value disables credential storage; any other malformed value must fail startup.
$sunshineCredentialKey = $values['CONTROLLER_SUNSHINE_CREDENTIAL_KEY']
if (-not [string]::IsNullOrWhiteSpace($sunshineCredentialKey) -and
    $sunshineCredentialKey -notmatch '^[A-Za-z0-9+/]{43}=$') {
    throw 'CONTROLLER_SUNSHINE_CREDENTIAL_KEY must be base64 of exactly 32 bytes.'
}

$env:InternalUrl = $values.CONTROLLER_INTERNAL_URL
$env:AgentUrl = $values.CONTROLLER_AGENT_URL
$env:DatabasePath = $values.CONTROLLER_DATABASE_PATH
$env:ServiceCredential = $values.CONTROLLER_SERVICE_CREDENTIAL
if ($hasWakeInterfaceAddress) {
    $env:WakeInterfaceAddress = $wakeInterfaceAddress
    $env:WakeBroadcastAddress = $wakeBroadcastAddress
}
if (-not [string]::IsNullOrWhiteSpace($publicIpEndpoint)) {
    $env:PublicIpEndpoint = $publicIpEndpoint
}
if (-not [string]::IsNullOrWhiteSpace($sunshineCredentialKey)) {
    $env:SunshineCredentialKey = $sunshineCredentialKey
}
$env:DOTNET_ENVIRONMENT = 'Development'

Push-Location $repo
try {
    & dotnet run --no-launch-profile --project '.\services\Controller\Controller.csproj'
    if ($LASTEXITCODE -ne 0) { throw "Controller exited with code $LASTEXITCODE." }
} finally {
    Pop-Location
}
