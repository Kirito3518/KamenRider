$ErrorActionPreference = 'Stop'
$publicUpdater = Join-Path $PSScriptRoot '..\..\public\agent\Update-Agent.ps1'
$packageUpdater = Join-Path $PSScriptRoot 'Update-AgentPackage.ps1'
. $publicUpdater -ControllerUrl 'https://controller.example/' -MachineId ('0' * 32) -OperationId ('1' * 32) -UpdateTicket ('A' * 43) -ReleaseVersion '2.3.4' -TargetVersion '2.3.4' -PackageSha256 ('A' * 64) -TrustedRootThumbprint ('B' * 40) -AdminReturnUrl 'https://admin.example/admin/agent-releases' -DefineFunctionsOnly
. $packageUpdater -DefineFunctionsOnly

function Assert-AgentUpdateThrows {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Message)
    $threw = $false
    try { & $Action } catch { $threw = $true }
    if (-not $threw) { throw $Message }
}

$validRequest = [pscustomobject]@{
    ControllerUrl = 'https://controller.example/'
    AssetBaseUrl = 'https://assets.example/'
    MachineId = '00112233445566778899aabbccddeeff'
    OperationId = 'ffeeddccbbaa99887766554433221100'
    ChainId = 'ffeeddccbbaa99887766554433221100'
    HeadOperationId = 'ffeeddccbbaa99887766554433221100'
    Generation = 1
    EnrollmentKeyFingerprint = 'D' * 64
    PackageLength = 1048576
    UpdateTicket = 'A' * 43
    ReleaseVersion = '2.3.4'
    TargetVersion = '2.3.4'
    TargetRuntime = 'win-x64'
    PackageSha256 = 'A' * 64
    TrustedRootThumbprint = 'B' * 40
    AdminReturnUrl = 'https://admin.example/admin/agent-releases'
    WrapperSha256 = 'E' * 64
    WrapperLength = 1024
    HelperRelativeUrl = ('/agent/maintenance/sha256/' + ('F' * 64).ToLowerInvariant() + '/AgentMaintenance.exe')
    HelperSha256 = 'F' * 64
    HelperLength = 4096
    HelperProtocol = 1
}

Test-AgentUpdateRequest -Request $validRequest | Out-Null
foreach ($replacement in @(
    @{ Name = 'hash'; Value = 'not-a-hash'; Property = 'PackageSha256' },
    @{ Name = 'machine'; Value = '..\escape'; Property = 'MachineId' },
    @{ Name = 'chain'; Value = 'not-a-chain'; Property = 'ChainId' },
    @{ Name = 'generation'; Value = 0; Property = 'Generation' },
    @{ Name = 'ticket'; Value = 'short'; Property = 'UpdateTicket' },
    @{ Name = 'controller'; Value = 'http://controller.example/'; Property = 'ControllerUrl' },
    @{ Name = 'thumbprint'; Value = 'Z' * 40; Property = 'TrustedRootThumbprint' },
    @{ Name = 'runtime'; Value = 'linux-x64'; Property = 'TargetRuntime' },
    @{ Name = 'helper-url'; Value = '/agent/maintenance/other.exe'; Property = 'HelperRelativeUrl' },
    @{ Name = 'helper-protocol'; Value = 2; Property = 'HelperProtocol' }
)) {
    $badRequest = $validRequest.PSObject.Copy()
    $badRequest.($replacement.Property) = $replacement.Value
    Assert-AgentUpdateThrows { Test-AgentUpdateRequest -Request $badRequest } "Invalid $($replacement.Name) was accepted."
}
$gitVersionRequest = $validRequest.PSObject.Copy()
$gitVersionRequest.ReleaseVersion = '1.0.0+g0123456789ab'
$gitVersionRequest.TargetVersion = '1.0.0+g0123456789ab'
Test-AgentUpdateRequest -Request $gitVersionRequest | Out-Null

$plan = Get-AgentUpdateTransactionPlan -InstallDirectory 'C:\Program Files\KamenRaider\Agent' -OperationId $validRequest.OperationId
if ($plan.BackupDirectory -ne 'C:\Program Files\KamenRaider\.Agent-rollback-ffeeddccbbaa99887766554433221100') { throw 'Rollback backup path must be deterministic and operation-bound.' }
if ($plan.StageDirectory -notlike 'C:\Program Files\KamenRaider\.Agent-stage-*') { throw 'Staging path must be a sibling on the install volume.' }

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-update-plan-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    New-Item -ItemType Directory -Path (Join-Path $temporary 'Agent') | Out-Null
    [IO.File]::WriteAllText((Join-Path $temporary 'Agent\Agent.exe'), 'fixture')
    [IO.File]::WriteAllText((Join-Path $temporary 'Agent\Agent.deps.json'), '{"libraries":{"Agent/2.3.4":{"type":"project"}}}')
    $manifest = '{"packageFormatVersion":1,"agentInformationalVersion":"2.3.4","sourceCommit":"0123456789ab","sourceDirty":false,"releaseBuild":true,"agentUpdateChallengeProtocol":1}'
    [IO.File]::WriteAllText((Join-Path $temporary 'AgentPackage.manifest.json'), $manifest)
    foreach ($name in @('Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Remove-StreamGuard.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1','Update-AgentPackage.ps1','Update-Agent.ps1')) {
        [IO.File]::WriteAllText((Join-Path $temporary $name), 'fixture')
    }
    Assert-AgentUpdatePackageRoot -PackageRoot $temporary -TargetVersion '2.3.4' | Out-Null

    # The legacy update path must no longer ship the removed Stream Guard setup
    # script and must instead ship the idempotent Guard/account migration.
    Remove-Item -LiteralPath (Join-Path $temporary 'Remove-StreamGuard.ps1') -Force
    [IO.File]::WriteAllText((Join-Path $temporary 'Prepare-SunshineStreamGate.ps1'), 'fixture')
    Assert-AgentUpdateThrows { Assert-AgentUpdatePackageRoot -PackageRoot $temporary -TargetVersion '2.3.4' } 'The update package accepted the removed Stream Guard setup script.'
    Remove-Item -LiteralPath (Join-Path $temporary 'Prepare-SunshineStreamGate.ps1') -Force
    Assert-AgentUpdateThrows { Assert-AgentUpdatePackageRoot -PackageRoot $temporary -TargetVersion '2.3.4' } 'The update package accepted a missing Guard-removal migration.'
    [IO.File]::WriteAllText((Join-Path $temporary 'Remove-StreamGuard.ps1'), 'fixture')

    [IO.File]::WriteAllText((Join-Path $temporary 'AgentPackage.manifest.json'), ($manifest.Replace('"releaseBuild":true','"releaseBuild":false')))
    Assert-AgentUpdateThrows { Assert-AgentUpdatePackageRoot -PackageRoot $temporary -TargetVersion '2.3.4' } 'The update package accepted a non-release manifest.'
    [IO.File]::WriteAllText((Join-Path $temporary 'AgentPackage.manifest.json'), ($manifest.Replace('"agentInformationalVersion":"2.3.4"','"agentInformationalVersion":"9.9.9"')))
    Assert-AgentUpdateThrows { Assert-AgentUpdatePackageRoot -PackageRoot $temporary -TargetVersion '2.3.4' } 'The update package accepted a mismatched target version.'
    Assert-AgentUpdateThrows { Get-AgentUpdateTransactionPlan -InstallDirectory 'C:\Temp\Agent' -OperationId $validRequest.OperationId } 'A non-managed installation directory was accepted.'
    Assert-AgentUpdateThrows { Get-AgentUpdateTransactionPlan -InstallDirectory 'C:\Program Files\KamenRaider\Agent' -OperationId 'not-a-guid' } 'An invalid operation id was accepted.'
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-update-plan-*') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if ($PSVersionTable.PSEdition -eq 'Core') {
    Initialize-AgentPinnedHttpType
    $pinnedClient = [KamenRaider.Update.PinnedHttpClientV1]::Create(('B' * 40))
    $pinnedClient.Dispose()
} else {
    Write-Warning 'Pinned HTTP type compilation requires PowerShell 7 in this environment; Windows PowerShell 5.1 Desktop Add-Type is skipped.'
}
Write-Output 'Agent update plan and Guard-removal package tests passed using temporary files only.'
