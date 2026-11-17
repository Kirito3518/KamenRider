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
    @{ Name = 'ticket'; Value = 'short'; Property = 'UpdateTicket' },
    @{ Name = 'controller'; Value = 'http://controller.example/'; Property = 'ControllerUrl' },
    @{ Name = 'thumbprint'; Value = 'Z' * 40; Property = 'TrustedRootThumbprint' }
)) {
    $badRequest = $validRequest.PSObject.Copy()
    $badRequest.($replacement.Property) = $replacement.Value
    Assert-AgentUpdateThrows { Test-AgentUpdateRequest -Request $badRequest } "Invalid $($replacement.Name) was accepted."
}
$gitVersionRequest = $validRequest.PSObject.Copy()
$gitVersionRequest.ReleaseVersion = '1.0.0+g0123456789ab'
$gitVersionRequest.TargetVersion = '1.0.0+g0123456789ab'
Test-AgentUpdateRequest -Request $gitVersionRequest | Out-Null

$mutationLog = [Collections.Generic.List[string]]::new()
$expectedInstall = Join-Path (Join-Path $env:ProgramFiles 'KamenRaider') 'Agent'
$expectedData = Join-Path (Join-Path $env:ProgramData 'KamenRaider') 'Agent'
$expectedBinary = Join-Path $expectedInstall 'Agent.exe'
$expectedServicePath = ('"{0}" --contentRoot "{1}"' -f [IO.Path]::GetFullPath($expectedBinary),[IO.Path]::GetFullPath($expectedInstall))
$workflow = [pscustomobject]@{
    Start = { $mutationLog.Add('start'); return $true }.GetNewClosure()
    Download = { $mutationLog.Add('download'); return [pscustomobject]@{ Path = 'unused'; Sha256 = $validRequest.PackageSha256 } }.GetNewClosure()
    Apply = { $mutationLog.Add('apply'); return [pscustomobject]@{ Outcome = 'applied' } }.GetNewClosure()
    Report = { param($request,$status) $mutationLog.Add("report:$status") }.GetNewClosure()
}
$validState = [pscustomobject]@{
    IdentityMachineId = $validRequest.MachineId
    InstallDirectory = $expectedInstall
    DataDirectory = $expectedData
    ConfigurationDataDirectory = $expectedData
    ConfigurationControllerUrl = 'https://controller.example/'
    TrustedRootPresent = $true
    AgentService = [pscustomobject]@{ Name = 'KamenRaider Agent'; Account = 'NT SERVICE\KamenRaider Agent'; StartMode = 'Auto'; BinaryPath = $expectedServicePath; Dependencies = @('KamenRaider Stream Guard') }
    GuardService = [pscustomobject]@{ Name = 'KamenRaider Stream Guard'; Account = 'LocalSystem'; StartMode = 'Auto'; BinaryPath = $expectedServicePath + ' --stream-guard' }
    SunshineService = [pscustomobject]@{ Name = 'SunshineService'; StartMode = 'Manual'; Dependencies = @('KamenRaider Stream Guard') }
    InstallPathSafe = $true
    DataPathSafe = $true
    StreamFenceClosed = $true
}

foreach ($case in @(
    @{ Name = 'wrong machine'; Change = { param($state) $state.IdentityMachineId = '11112222333344445555666677778888' } },
    @{ Name = 'unsafe installation path'; Change = { param($state) $state.InstallDirectory = 'C:\Windows\Agent' } },
    @{ Name = 'wrong Agent identity'; Change = { param($state) $state.AgentService.Account = 'LocalSystem' } },
    @{ Name = 'wrong Agent path'; Change = { param($state) $state.AgentService.BinaryPath = 'C:\Windows\Agent.exe' } },
    @{ Name = 'unsafe ACL'; Change = { param($state) $state.InstallPathSafe = $false } },
    @{ Name = 'reparse path'; Change = { param($state) $state.DataPathSafe = $false } },
    @{ Name = 'open stream fence'; Change = { param($state) $state.StreamFenceClosed = $false } }
)) {
    $state = $validState | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    & $case.Change $state
    $mutationLog.Clear()
    Assert-AgentUpdateThrows {
        Invoke-AgentUpdateWorkflow -Request $validRequest -LocalState $state -Actions $workflow
    } "Preflight accepted $($case.Name)."
    if ($mutationLog.Count -ne 0) { throw "$($case.Name) caused a Controller, service, or file mutation before validation." }
}

$badHashRequest = $validRequest.PSObject.Copy()
$badHashRequest.PackageSha256 = 'invalid-hash'
$mutationLog.Clear()
Assert-AgentUpdateThrows { Invoke-AgentUpdateWorkflow -Request $badHashRequest -LocalState $validState -Actions $workflow } 'Workflow accepted an invalid package hash.'
if ($mutationLog.Count -ne 0) { throw 'Invalid package hash caused a Controller, service, or file mutation.' }

$plan = Get-AgentUpdateTransactionPlan -InstallDirectory 'C:\Program Files\KamenRaider\Agent' -OperationId $validRequest.OperationId
if ($plan.BackupDirectory -ne 'C:\Program Files\KamenRaider\.Agent-rollback-ffeeddccbbaa99887766554433221100') { throw 'Rollback backup path must be deterministic and operation-bound.' }
if ($plan.StageDirectory -notlike 'C:\Program Files\KamenRaider\.Agent-stage-*') { throw 'Staging path must be a sibling on the install volume.' }

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-update-plan-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archivePath = Join-Path $temporary 'traversal.zip'
    $zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('../outside.txt')
        $stream = $entry.Open(); try { $bytes = [Text.Encoding]::UTF8.GetBytes('unsafe'); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
    $traversalHash = (Get-FileHash $archivePath -Algorithm SHA256).Hash
    Assert-AgentUpdateThrows { Test-AgentUpdatePackageArchive -ArchivePath $archivePath -ExpectedSha256 $traversalHash -TargetVersion '2.3.4' } 'Package traversal was accepted.'
    if (Test-Path -LiteralPath (Join-Path $temporary 'outside.txt')) { throw 'Package traversal wrote outside its temporary archive directory.' }
    $traversalRequest = $validRequest.PSObject.Copy()
    $traversalRequest.PackageSha256 = $traversalHash
    $traversalBytes = [IO.File]::ReadAllBytes($archivePath)
    Remove-Item -LiteralPath $archivePath -Force
    $traversalStream = [IO.MemoryStream]::new($traversalBytes)
    $traversalLog = [Collections.Generic.List[string]]::new()
    $traversalActions = [pscustomobject]@{
        Start = { $traversalLog.Add('start') }.GetNewClosure()
        Download = { $traversalLog.Add('download'); return [pscustomobject]@{ Stream = $traversalStream; Length = $traversalStream.Length } }.GetNewClosure()
        Validate = { param($request,$package) Test-AgentUpdatePackageArchive -ArchiveStream $package.Stream -ExpectedSha256 $request.PackageSha256 -TargetVersion $request.TargetVersion | Out-Null }.GetNewClosure()
        Apply = { $traversalLog.Add('apply'); throw 'Apply must not run for traversal.' }.GetNewClosure()
        Report = { param($request,$status) $traversalLog.Add("report:$status") }.GetNewClosure()
        Cleanup = { $traversalStream.Dispose(); $traversalLog.Add('cleanup') }.GetNewClosure()
    }
    $traversalError = $null
    try { Invoke-AgentUpdateWorkflow -Request $traversalRequest -LocalState $validState -Actions $traversalActions } catch { $traversalError = $_.Exception.Message }
    if ($traversalError -notmatch 'unsafe path') { throw "Traversal must be rejected for its unsafe path, got: $traversalError" }
    if ($traversalLog -contains 'apply' -or $traversalLog -notcontains 'report:failed') { throw 'Traversal archive was not rejected before binary or service mutation.' }
    if (Test-Path -LiteralPath $archivePath) { throw 'Traversal validation wrote the in-memory archive to disk before rejection.' }
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-update-plan-*') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Initialize-AgentPinnedHttpType
$pinnedClient = [KamenRaider.Update.PinnedHttpClientV1]::Create(('B' * 40))
$pinnedClient.Dispose()
Write-Output 'Agent update plan tests passed using temporary files and fake actions only.'
