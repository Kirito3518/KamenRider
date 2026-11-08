param([string]$ScenarioFilter)
$ErrorActionPreference = 'Stop'
$script:publicUpdater = Join-Path $PSScriptRoot '..\..\public\agent\Update-Agent.ps1'
$script:internalUpdater = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Update-AgentPackage.ps1'))
. $script:publicUpdater -ControllerUrl 'https://controller.example/' -MachineId ('0' * 32) -OperationId ('1' * 32) -UpdateTicket ('A' * 43) -ReleaseVersion '2.3.4' -TargetVersion '2.3.4' -PackageSha256 ('A' * 64) -TrustedRootThumbprint ('B' * 40) -AdminReturnUrl 'https://admin.example/admin/agent-releases' -DefineFunctionsOnly
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Only platform boundaries are faked: Main, preflight, HTTP workflow, archive handling,
# staging and the actual transaction execute against disposable files.
function Assert-AgentUpdateHost { param($Request) }
function Assert-AgentUpdateVirtualAccountSid { param($ServiceSid) }
function Get-AgentUpdateLockParent { return Join-Path $env:ProgramData 'KamenRaider' }
function Get-AgentUpdateServiceSid { param($ServiceName) return $global:PublicUpdateFixture.ServiceSid }
function Assert-AgentUpdateTrustedTree {
    param($Path,$TrustedSids,$ServiceSid,[switch]$Private,[switch]$ReadOnly,[switch]$AncestorsOnly)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Agent installation path is missing: $Path" }
    if ($ServiceSid -and -not $Private -and -not $ReadOnly) { throw 'Agent binaries were not checked using read-only service permissions.' }
    $items = @((Get-Item -LiteralPath $Path -Force))
    if (-not $AncestorsOnly) { $items += @(Get-ChildItem -LiteralPath $Path -Force -Recurse) }
    foreach ($item in $items) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Untrusted reparse point in update tree.' }
    }
}
function Get-AgentUpdateServiceRecord {
    param($Name)
    $f = $global:PublicUpdateFixture
    $binary = ('"{0}" --contentRoot "{1}"' -f (Join-Path $f.Install 'Agent.exe'),$f.Install)
    switch ($Name) {
        'KamenRaider Agent' { return [pscustomobject]@{ Name=$Name; Account='NT SERVICE\KamenRaider Agent'; StartMode='Auto'; BinaryPath=$binary; Dependencies=@('KamenRaider Stream Guard'); State=$f.Agent } }
        'KamenRaider Stream Guard' { return [pscustomobject]@{ Name=$Name; Account='LocalSystem'; StartMode='Auto'; BinaryPath=($binary + ' --stream-guard'); Dependencies=@(); State=$f.Guard } }
        'SunshineService' { return [pscustomobject]@{ Name=$Name; StartMode='Manual'; BinaryPath=(Join-Path $env:ProgramFiles 'Sunshine\tools\sunshinesvc.exe'); Dependencies=@('KamenRaider Stream Guard'); State='Stopped' } }
        default { throw 'Unexpected fake service.' }
    }
}
function Test-AgentUpdateStreamFence { param($SunshineExecutable) return $true }
function Invoke-AgentPinnedHttpRequest {
    param($Uri,$Method,$TrustedRootThumbprint,$Headers,$JsonBody,[switch]$InMemory)
    $f = $global:PublicUpdateFixture
    if ($Uri.EndsWith('/start')) { $f.Events.Add('http:start'); return }
    if ($Uri.EndsWith('/package')) { $f.Events.Add('http:package'); $stream = [IO.MemoryStream]::new($f.ArchiveBytes); return [pscustomobject]@{ Stream=$stream; Length=$stream.Length } }
    if ($Uri.EndsWith('/status')) { $status = ($JsonBody | ConvertFrom-Json).status; $f.Events.Add('http:' + $status); return }
    throw 'Unexpected fake HTTP request.'
}
function Start-Process { param($FilePath) }

$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('kr-update-public-' + [guid]::NewGuid().ToString('N'))))
$oldProgramFiles = $env:ProgramFiles
$oldProgramData = $env:ProgramData
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    foreach ($scenario in @('success','interrupted','interrupted-stop-failure','stage-before-backup','partial-restore-no-config','partial-restore-with-config','partial-restore-corrupt-config','partial-restore-target-deps','partial-restore-stop-failure')) {
        if ($ScenarioFilter -and $scenario -ne $ScenarioFilter) { continue }
        $scenarioRoot = Join-Path $root $scenario
        $env:ProgramFiles = Join-Path $scenarioRoot 'ProgramFiles'
        $env:ProgramData = Join-Path $scenarioRoot 'ProgramData'
        $install = Join-Path $env:ProgramFiles 'KamenRaider\Agent'
        $data = Join-Path $env:ProgramData 'KamenRaider\Agent'
        $package = Join-Path $scenarioRoot 'package'
        New-Item -ItemType Directory -Path $env:ProgramFiles -Force | Out-Null
        New-Item -ItemType Directory -Path $env:ProgramData -Force | Out-Null
        $fixtureSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
        New-PrivateInstallDirectory -Path (Join-Path $env:ProgramFiles 'KamenRaider') -ServiceSid $fixtureSid -ReadOnly | Out-Null
        New-PrivateInstallDirectory -Path (Join-Path $env:ProgramData 'KamenRaider') -ServiceSid $fixtureSid -ReadOnly | Out-Null
        foreach ($directory in @($install,(Join-Path $data 'identity'),(Join-Path $package 'Agent'))) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
        [IO.File]::WriteAllText((Join-Path $install 'Agent.exe'),'old-agent')
        [IO.File]::WriteAllText((Join-Path $install 'Agent.deps.json'),'{"libraries":{"Agent/1.2.3":{"type":"project"}}}')
        [IO.File]::WriteAllText((Join-Path $install 'appsettings.json'),(@{ ControllerUrl='https://controller.example/'; DataDirectory=$data } | ConvertTo-Json))
        [IO.File]::WriteAllText((Join-Path $data 'identity\identity.json'),'{"MachineId":"00112233445566778899aabbccddeeff","PrivateKey":"keep"}')
        [IO.File]::WriteAllText((Join-Path $data 'enrollment-receipt.json'),'{"MachineId":"00112233445566778899aabbccddeeff"}')
        [IO.File]::WriteAllText((Join-Path $package 'Agent\Agent.exe'),'new-agent')
        [IO.File]::WriteAllText((Join-Path $package 'Agent\Agent.deps.json'),'{"libraries":{"Agent/2.3.4":{"type":"project"}}}')
        [IO.File]::WriteAllText((Join-Path $package 'AgentPackage.manifest.json'),'{"packageFormatVersion":1,"agentInformationalVersion":"2.3.4","sourceCommit":"0123456789ab","sourceDirty":false,"releaseBuild":true}')
        foreach ($name in @('Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Prepare-SunshineStreamGate.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $package $name) }
        Copy-Item -LiteralPath $script:publicUpdater -Destination (Join-Path $package 'Update-Agent.ps1')
        $wrapper = @'
param($Action,$InstallDirectory,$DataDirectory,$PackageRoot,$OperationId,$TargetVersion,$ServiceSid)
$transaction = @{ InstallDirectory=$InstallDirectory; DataDirectory=$DataDirectory; PackageRoot=$PackageRoot; OperationId=$OperationId; TargetVersion=$TargetVersion; ServiceSid=[Security.Principal.SecurityIdentifier]::new($ServiceSid); ServiceApi=$global:PublicUpdateFixture.ServiceApi }
. '__INTERNAL__' -DefineFunctionsOnly
Invoke-AgentUpdateTransaction @transaction
'@.Replace('__INTERNAL__',$script:internalUpdater.Replace("'","''"))
        [IO.File]::WriteAllText((Join-Path $package 'Update-AgentPackage.ps1'),$wrapper)
        $archive = Join-Path $scenarioRoot 'package.zip'
        $zip = [IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in @(Get-ChildItem -LiteralPath $package -File -Recurse)) {
                $entry = $file.FullName.Substring($package.Length).TrimStart('\').Replace('\','/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$entry) | Out-Null
            }
        } finally { $zip.Dispose() }
        $global:PublicUpdateFixture = [pscustomobject]@{
            Install=$install; Data=$data; Agent='Running'; Guard='Running'; Events=[Collections.Generic.List[string]]::new()
            ArchiveBytes=[IO.File]::ReadAllBytes($archive); ServiceSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'); ServiceApi=$null; FailStop=($scenario -in @('interrupted-stop-failure','partial-restore-stop-failure'))
        }
        $global:PublicUpdateFixture.ServiceApi = [pscustomobject]@{
            StopAgent={ $global:PublicUpdateFixture.Events.Add('stop-agent'); $global:PublicUpdateFixture.Agent='Stopped' }
            StopGuard={ $global:PublicUpdateFixture.Events.Add('stop-guard'); if ($global:PublicUpdateFixture.FailStop) { throw 'fake Guard stop uncertainty' }; $global:PublicUpdateFixture.Guard='Stopped' }
            StartGuard={ $global:PublicUpdateFixture.Events.Add('start-guard'); $global:PublicUpdateFixture.Guard='Running' }
            StartAgent={ $global:PublicUpdateFixture.Events.Add('start-agent'); $global:PublicUpdateFixture.Agent='Running' }
        }
        $operation = 'ffeeddccbbaa99887766554433221100'
        $backup = Join-Path (Split-Path $install -Parent) ('.Agent-rollback-' + $operation)
        if ($scenario -eq 'stage-before-backup') {
            foreach ($prefix in @('.Agent-stage-','.Agent-backup-stage-')) {
                $partial = Join-Path (Split-Path $install -Parent) ($prefix + $operation)
                New-PrivateInstallDirectory -Path $partial -ServiceSid $global:PublicUpdateFixture.ServiceSid -ReadOnly | Out-Null
                [IO.File]::WriteAllText((Join-Path $partial 'partial-file'),'incomplete')
            }
        }
        if ($scenario -like 'partial-restore-*') {
            New-PrivateInstallDirectory -Path $backup -ServiceSid $global:PublicUpdateFixture.ServiceSid -ReadOnly | Out-Null
            Get-ChildItem -LiteralPath $install -File | Copy-Item -Destination $backup
            Set-PrivateInstallAcl -Path (Join-Path $backup 'appsettings.json') -ServiceSid $global:PublicUpdateFixture.ServiceSid
            Remove-Item -LiteralPath $install -Recurse -Force
            New-PrivateInstallDirectory -Path $install -ServiceSid $global:PublicUpdateFixture.ServiceSid -ReadOnly | Out-Null
            [IO.File]::WriteAllText((Join-Path $install 'Agent.exe'),'partial-agent')
            if ($scenario -ne 'partial-restore-no-config') {
                Copy-Item -LiteralPath (Join-Path $backup 'Agent.deps.json') -Destination $install
                Copy-Item -LiteralPath (Join-Path $backup 'appsettings.json') -Destination $install
                Set-PrivateInstallAcl -Path (Join-Path $install 'appsettings.json') -ServiceSid $global:PublicUpdateFixture.ServiceSid
                if ($scenario -eq 'partial-restore-corrupt-config') { [IO.File]::WriteAllText((Join-Path $install 'appsettings.json'),'{incomplete') }
                if ($scenario -eq 'partial-restore-target-deps') { [IO.File]::WriteAllText((Join-Path $install 'Agent.deps.json'),'{"libraries":{"Agent/2.3.4":{"type":"project"}}}') }
            }
            $global:PublicUpdateFixture.Agent = 'Stopped'
            $global:PublicUpdateFixture.Guard = 'Stopped'
        }
        if ($scenario -like 'interrupted*') {
            New-PrivateInstallDirectory -Path $backup -ServiceSid $global:PublicUpdateFixture.ServiceSid -ReadOnly | Out-Null
            Get-ChildItem -LiteralPath $install -File | Copy-Item -Destination $backup
            Set-PrivateInstallAcl -Path (Join-Path $backup 'appsettings.json') -ServiceSid $global:PublicUpdateFixture.ServiceSid
            Remove-Item -LiteralPath $install -Recurse -Force
            $global:PublicUpdateFixture.Agent = 'Stopped'
            $global:PublicUpdateFixture.Guard = 'Stopped'
        }
        $parameters = @{
            ControllerUrl='https://controller.example/'; MachineId='00112233445566778899aabbccddeeff'; OperationId=$operation; UpdateTicket=('A' * 43)
            ReleaseVersion='2.3.4'; TargetVersion='2.3.4'; PackageSha256=(Get-FileHash $archive -Algorithm SHA256).Hash
            TrustedRootThumbprint=('B' * 40); AdminReturnUrl='https://admin.example/admin/agent-releases'
        }
        $mainError = $null
        try { Invoke-AgentUpdateMain -BoundParameters $parameters | Out-Null } catch { $mainError = $_.Exception.Message }
        $events = $global:PublicUpdateFixture.Events
        if ($scenario -eq 'interrupted-stop-failure') {
            if ($mainError -notmatch 'fake Guard stop uncertainty' -or $events -notcontains 'http:failed' -or $events -contains 'http:rollingBack' -or $events -contains 'start-agent') { throw "Recovery stop failure was not reported as uncertain: $mainError; $($events -join ',')" }
            if ((Test-Path -LiteralPath $install) -or -not (Test-Path -LiteralPath $backup -PathType Container)) { throw 'Failed recovery stop must retain the backup and not restore files.' }
            continue
        }
        if ($scenario -eq 'partial-restore-stop-failure') {
            if ($mainError -notmatch 'fake Guard stop uncertainty' -or $events -notcontains 'http:failed' -or $events -contains 'http:rollingBack' -or $events -contains 'start-agent') { throw "Partial recovery stop failure was not reported as uncertain: $mainError; $($events -join ',')" }
            if ([IO.File]::ReadAllText((Join-Path $install 'Agent.exe')) -ne 'partial-agent' -or -not (Test-Path -LiteralPath $backup -PathType Container)) { throw 'Failed recovery stop must preserve both the partial installation and backup.' }
            continue
        }
        if ($mainError) { throw $mainError }
        $expectedStatus = if ($scenario -in @('success','stage-before-backup')) { 'http:awaitingHeartbeat' } else { 'http:rollingBack' }
        $expectedBinary = if ($scenario -in @('success','stage-before-backup')) { 'new-agent' } else { 'old-agent' }
        if ([IO.File]::ReadAllText((Join-Path $install 'Agent.exe')) -ne $expectedBinary) { throw "$scenario did not leave the expected Agent binary." }
        if ($events.IndexOf('http:start') -lt 0 -or $events.IndexOf('http:package') -le $events.IndexOf('http:start') -or $events.IndexOf($expectedStatus) -le $events.IndexOf('start-agent')) { throw "$scenario did not report its outcome after service startup: $($events -join ',')" }
        if ($expectedStatus -eq 'http:rollingBack' -and $events -contains 'http:awaitingHeartbeat') { throw "$scenario reported target success while rolling back." }
        if (-not (Test-Path -LiteralPath $backup -PathType Container)) { throw "$scenario did not retain the rollback backup." }
        if ($scenario -like 'partial-restore-*' -and (Get-FileHash -LiteralPath (Join-Path $install 'appsettings.json')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $backup 'appsettings.json')).Hash) { throw "$scenario did not restore the protected configuration." }
        if (-not [IO.File]::ReadAllText((Join-Path $data 'identity\identity.json')).Contains('keep')) { throw "$scenario changed Agent identity." }
    }
    Write-Output 'Public Agent update and interrupted recovery passed with fake HTTP, fake services, and temporary files.'
} finally {
    $env:ProgramFiles = $oldProgramFiles
    $env:ProgramData = $oldProgramData
    Remove-Variable PublicUpdateFixture -Scope Global -ErrorAction SilentlyContinue
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $root.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($root) -notlike 'kr-update-public-*') { throw 'Unsafe public workflow test cleanup path.' }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
