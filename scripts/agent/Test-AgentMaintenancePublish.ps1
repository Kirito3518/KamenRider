[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath($RepositoryRoot)
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('kr-maintenance-publish-' + [guid]::NewGuid().ToString('N'))))
$fixtureRepository = Join-Path $temporaryRoot 'repo'
$outputRoot = Join-Path $temporaryRoot 'output'

function Get-TestSha256Hex {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($algorithm.ComputeHash($Bytes)).Replace('-', '')
    }
    finally { $algorithm.Dispose() }
}

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition,[Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    $fixturePublicAgent = Join-Path $fixtureRepository 'public\agent'
    $fixtureScripts = Join-Path $fixtureRepository 'scripts\agent'
    $fixtureProject = Join-Path $fixtureRepository 'services\AgentMaintenance'
    New-Item -ItemType Directory -Path $fixturePublicAgent,$fixtureScripts,$fixtureProject -Force | Out-Null
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

    $publisherSource = Join-Path $repository 'scripts\agent\Publish-AgentMaintenance.ps1'
    $wrapperSource = Join-Path $repository 'public\agent\Update-Agent.ps1'
    if (-not (Test-Path -LiteralPath $publisherSource -PathType Leaf)) { throw 'Agent maintenance publisher is missing.' }
    if (-not (Test-Path -LiteralPath $wrapperSource -PathType Leaf)) { throw 'Deployment wrapper is missing.' }
    Copy-Item -LiteralPath $publisherSource -Destination (Join-Path $fixtureScripts 'Publish-AgentMaintenance.ps1')
    Copy-Item -LiteralPath $wrapperSource -Destination (Join-Path $fixturePublicAgent 'Update-Agent.ps1')
    [IO.File]::WriteAllText((Join-Path $fixtureProject 'AgentMaintenance.csproj'), '<Project />')

    $fakeDotNet = Join-Path $temporaryRoot 'Fake-DotNet.ps1'
    $fakeDotNetScript = @'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$DotNetArgs)
$runtimeIndex = [Array]::IndexOf($DotNetArgs, '--runtime')
$outputIndex = [Array]::IndexOf($DotNetArgs, '--output')
if ($runtimeIndex -lt 0 -or $outputIndex -lt 0 -or $outputIndex + 1 -ge $DotNetArgs.Length) { throw 'Fake dotnet received an incomplete publish command.' }
$runtime = $DotNetArgs[$runtimeIndex + 1]
$output = $DotNetArgs[$outputIndex + 1]
if ($runtime -notin @('win-x64','win-arm64')) { throw 'Fake dotnet received an unexpected runtime.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$bytes = New-Object byte[] 256
$bytes[0] = [byte]0x4d
$bytes[1] = [byte]0x5a
[Array]::Copy([BitConverter]::GetBytes([int]0x80),0,$bytes,0x3c,4)
[Array]::Copy([BitConverter]::GetBytes([uint32]0x00004550),0,$bytes,0x80,4)
$machine = if ($runtime -eq 'win-x64') { [uint16]0x8664 } else { [uint16]0xAA64 }
[Array]::Copy([BitConverter]::GetBytes($machine),0,$bytes,0x84,2)
[IO.File]::WriteAllBytes((Join-Path $output 'AgentMaintenance.exe'),$bytes)
$global:LASTEXITCODE = 0
'@
    [IO.File]::WriteAllText($fakeDotNet,$fakeDotNetScript)

    $publisher = Join-Path $fixtureScripts 'Publish-AgentMaintenance.ps1'
    & $publisher -RepositoryRoot $fixtureRepository -OutputRoot $outputRoot -DotNetPath $fakeDotNet

    $manifestPath = Join-Path $outputRoot 'public\agent\agent-maintenance-assets.json'
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'Publisher did not emit the deployment manifest.'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    Assert-True ($manifest.schemaVersion -eq 1 -and $manifest.helperProtocol -eq 1) 'Agent maintenance manifest protocol is invalid.'
    Assert-True ($manifest.wrapper.relativeUrl -ceq '/agent/Update-Agent.ps1') 'Wrapper URL is not the fixed deployment-relative path.'

    $wrapperBytes = [IO.File]::ReadAllBytes((Join-Path $fixturePublicAgent 'Update-Agent.ps1'))
    $wrapperHash = Get-TestSha256Hex $wrapperBytes
    Assert-True ($manifest.wrapper.length -eq $wrapperBytes.Length -and $manifest.wrapper.sha256 -ceq $wrapperHash) 'Wrapper length or digest does not match the deployment manifest.'

    $seenHashes = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($runtime in @('win-x64','win-arm64')) {
        $asset = $manifest.helpers.$runtime
        Assert-True ([bool]$asset) "$runtime helper is absent from the manifest."
        Assert-True ($asset.relativeUrl -match ('\A/agent/maintenance/sha256/' + [Regex]::Escape([string]$asset.sha256) + '/AgentMaintenance\.exe\z')) "$runtime helper URL is not fixed and content-addressed."
        $assetPath = Join-Path $outputRoot ('public\agent' + $asset.relativeUrl.Substring('/agent'.Length).Replace('/','\'))
        Assert-True (Test-Path -LiteralPath $assetPath -PathType Leaf) "$runtime helper asset is missing."
        $assetBytes = [IO.File]::ReadAllBytes($assetPath)
        $actualHash = Get-TestSha256Hex $assetBytes
        Assert-True ($asset.length -eq $assetBytes.Length -and $asset.sha256 -ceq $actualHash) "$runtime helper bytes do not match the manifest."
        Assert-True $seenHashes.Add([string]$asset.sha256) 'The x64 and arm64 helper assets unexpectedly have identical content hashes.'
    }

    $tamperedAsset = $manifest.helpers.'win-x64'
    $tamperedPath = Join-Path $outputRoot ('public\agent' + $tamperedAsset.relativeUrl.Substring('/agent'.Length).Replace('/','\'))
    $originalBytes = [IO.File]::ReadAllBytes($tamperedPath)
    try {
        $changedBytes = New-Object byte[] ($originalBytes.Length + 1)
        [Array]::Copy($originalBytes,$changedBytes,$originalBytes.Length)
        $changedBytes[$changedBytes.Length - 1] = [byte]0x41
        [IO.File]::WriteAllBytes($tamperedPath,$changedBytes)
        $changedHash = Get-TestSha256Hex ([IO.File]::ReadAllBytes($tamperedPath))
        Assert-True ($changedBytes.Length -ne $tamperedAsset.length -or $changedHash -cne $tamperedAsset.sha256) 'Tampered helper unexpectedly matched the pinned length and digest.'
    }
    finally { [IO.File]::WriteAllBytes($tamperedPath,$originalBytes) }

    $missingAssetPath = Join-Path $outputRoot ('public\agent' + $manifest.helpers.'win-arm64'.relativeUrl.Substring('/agent'.Length).Replace('/','\'))
    $missingAssetBackup = $missingAssetPath + '.missing'
    Move-Item -LiteralPath $missingAssetPath -Destination $missingAssetBackup
    try {
        Assert-True (-not (Test-Path -LiteralPath $missingAssetPath)) 'Missing-asset scenario did not remove the pinned helper.'
    }
    finally { Move-Item -LiteralPath $missingAssetBackup -Destination $missingAssetPath }

    $manifestBackup = $manifestPath + '.missing'
    Move-Item -LiteralPath $manifestPath -Destination $manifestBackup
    try {
        Assert-True (-not (Test-Path -LiteralPath $manifestPath)) 'Missing-manifest scenario did not remove the manifest.'
        $rejected = $false
        try { Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop } catch { $rejected = $true }
        Assert-True $rejected 'Missing manifest was not rejected.'
    }
    finally { Move-Item -LiteralPath $manifestBackup -Destination $manifestPath }

    Write-Output ('Agent maintenance publisher contract passed under PowerShell ' + $PSVersionTable.PSVersion + '.')
}
finally {
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $temporaryRoot.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($temporaryRoot) -notlike 'kr-maintenance-publish-*') {
        throw 'Unsafe publisher test cleanup path.'
    }
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
