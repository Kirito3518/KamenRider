[CmdletBinding()]
param(
    [ValidateSet('Apply')][string]$Action = 'Apply',
    [string]$InstallDirectory,
    [string]$DataDirectory,
    [string]$PackageRoot,
    [string]$OperationId,
    [string]$TargetVersion,
    [string]$ServiceSid,
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'

function Get-AgentUpdateTransactionPlan {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallDirectory, [Parameter(Mandatory)][string]$OperationId)
    if ($OperationId -notmatch '\A[0-9a-fA-F]{32}\z') { throw 'OperationId must be a 32-character hexadecimal GUID.' }
    if (-not [IO.Path]::IsPathRooted($InstallDirectory)) { throw 'InstallDirectory must be absolute.' }
    $install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $parent = [IO.Path]::GetDirectoryName($install)
    if ([IO.Path]::GetFileName($install) -ine 'Agent' -or [IO.Path]::GetFileName($parent) -ine 'KamenRaider') { throw 'InstallDirectory must be the managed KamenRaider Agent directory.' }
    $id = $OperationId.ToLowerInvariant()
    [pscustomobject]@{
        InstallDirectory = $install
        ParentDirectory = $parent
        BackupDirectory = Join-Path $parent ('.Agent-rollback-' + $id)
        BackupStageDirectory = Join-Path $parent ('.Agent-backup-stage-' + $id)
        StageDirectory = Join-Path $parent ('.Agent-stage-' + $id)
        PriorDirectory = Join-Path $parent ('.Agent-prior-' + $id)
        OperationId = $id
    }
}

function Test-AgentUpdatePackageManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PackageRoot, [Parameter(Mandatory)][string]$TargetVersion)
    $manifestPath = Join-Path $PackageRoot 'AgentPackage.manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Agent package manifest is missing.' }
    try { $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -ErrorAction Stop } catch { throw 'Agent package manifest is invalid JSON.' }
    if ($manifest.packageFormatVersion -ne 1 -or $manifest.agentInformationalVersion -cne $TargetVersion -or
        $manifest.sourceCommit -notmatch '\A[0-9a-f]{12}\z' -or $manifest.sourceDirty -ne $false -or $manifest.releaseBuild -ne $true) {
        throw 'Agent package manifest does not identify the requested clean release.'
    }
    return $manifest
}

function Get-AgentUpdatePackageVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$AgentDirectory)
    $depsPath = Join-Path $AgentDirectory 'Agent.deps.json'
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) { throw 'Agent dependency manifest is missing.' }
    try { $deps = [IO.File]::ReadAllText($depsPath) | ConvertFrom-Json -ErrorAction Stop } catch { throw 'Agent dependency manifest is invalid.' }
    foreach ($property in $deps.libraries.PSObject.Properties) {
        if ($property.Name -like 'Agent/*' -and $property.Value.type -eq 'project') { return $property.Name.Substring(6) }
    }
    throw 'Agent dependency manifest does not contain the Agent informational version.'
}

function Test-AgentUpdateTreesEquivalent {
    param([Parameter(Mandatory)][string]$First,[Parameter(Mandatory)][string]$Second)
    foreach ($root in @($First,$Second)) {
        $rootItem = Get-Item -LiteralPath $root -Force -ErrorAction Stop
        if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
        foreach ($item in @(Get-ChildItem -LiteralPath $root -Force -Recurse -ErrorAction Stop)) { if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false } }
    }
    $firstRoot = [IO.Path]::GetFullPath($First).TrimEnd('\')
    $secondRoot = [IO.Path]::GetFullPath($Second).TrimEnd('\')
    $firstFiles = @(Get-ChildItem -LiteralPath $firstRoot -File -Force -Recurse | Sort-Object { $_.FullName.Substring($firstRoot.Length) })
    $secondFiles = @(Get-ChildItem -LiteralPath $secondRoot -File -Force -Recurse | Sort-Object { $_.FullName.Substring($secondRoot.Length) })
    if ($firstFiles.Count -ne $secondFiles.Count) { return $false }
    for ($index = 0; $index -lt $firstFiles.Count; $index++) {
        $firstRelative = $firstFiles[$index].FullName.Substring($firstRoot.Length).TrimStart('\')
        $secondRelative = $secondFiles[$index].FullName.Substring($secondRoot.Length).TrimStart('\')
        if (-not [string]::Equals($firstRelative,$secondRelative,[StringComparison]::OrdinalIgnoreCase) -or $firstFiles[$index].Length -ne $secondFiles[$index].Length) { return $false }
        if ((Get-FileHash -LiteralPath $firstFiles[$index].FullName -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $secondFiles[$index].FullName -Algorithm SHA256).Hash) { return $false }
    }
    return $true
}

function Assert-AgentUpdatePackageRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PackageRoot, [Parameter(Mandatory)][string]$TargetVersion)
    if (-not [IO.Path]::IsPathRooted($PackageRoot)) { throw 'PackageRoot must be absolute.' }
    $root = Get-Item -LiteralPath $PackageRoot -Force -ErrorAction Stop
    if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'PackageRoot must be a real directory.' }
    $manifest = Test-AgentUpdatePackageManifest -PackageRoot $root.FullName -TargetVersion $TargetVersion
    $allowedRootFiles = @('Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Prepare-SunshineStreamGate.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1','Update-AgentPackage.ps1','Update-Agent.ps1','AgentPackage.manifest.json')
    $files = @(Get-ChildItem -LiteralPath $root.FullName -Force -Recurse -ErrorAction Stop)
    if ($files.Count -gt 5000) { throw 'Expanded Agent package exceeds 5,000 entries.' }
    $totalBytes = [long]0
    foreach ($item in $files) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Agent package contains a reparse point.' }
        $relative = $item.FullName.Substring($root.FullName.TrimEnd('\').Length).TrimStart('\').Replace('\','/')
        if ($relative -match '(^|/)\.\.?(/|$)' -or $relative.StartsWith('/') -or $relative.Contains(':')) { throw 'Agent package contains an unsafe path.' }
        if (-not $item.PSIsContainer) {
            $totalBytes += $item.Length
            if ($relative -notlike 'Agent/*' -and $relative -notin $allowedRootFiles) { throw "Unexpected Agent package file: $relative" }
        }
    }
    if ($totalBytes -gt 524288000) { throw 'Expanded Agent package exceeds 500 MiB.' }
    foreach ($required in @('Agent\Agent.exe','Agent\Agent.deps.json','PrivateInstallFiles.ps1','Prepare-SunshineStreamGate.ps1','Update-AgentPackage.ps1','AgentPackage.manifest.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $root.FullName $required) -PathType Leaf)) { throw "Agent package is missing $required." }
    }
    if (Test-Path -LiteralPath (Join-Path $root.FullName 'Agent\appsettings.json')) { throw 'Agent package must not replace local appsettings.json.' }
    if ((Get-AgentUpdatePackageVersion -AgentDirectory (Join-Path $root.FullName 'Agent')) -cne $TargetVersion) { throw 'Agent binary version does not match the release manifest.' }
    return $manifest
}

function Copy-AgentUpdateTree {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination, [Security.Principal.SecurityIdentifier]$ServiceSid, [switch]$ReadOnlyRoot)
    if (Test-Path -LiteralPath $Destination) { throw "Update destination already exists: $Destination" }
    if ($ServiceSid) { New-PrivateInstallDirectory -Path $Destination -ServiceSid $ServiceSid -ReadOnly:$ReadOnlyRoot | Out-Null }
    else { [void](New-Item -ItemType Directory -Path $Destination) }
    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    foreach ($item in @(Get-ChildItem -LiteralPath $sourceRoot -Force -Recurse | Sort-Object { $_.FullName.Length })) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Agent update tree contains a reparse point.' }
        $relative = $item.FullName.Substring($sourceRoot.Length).TrimStart('\')
        if ($relative -match '(^|\\)\.\.?($|\\)' -or $relative.Contains(':')) { throw 'Agent update tree contains a traversal path.' }
        $target = Join-Path $destinationRoot $relative
        if ($item.PSIsContainer) {
            if (-not (Test-Path -LiteralPath $target)) { [void](New-Item -ItemType Directory -Path $target) }
        } else {
            [IO.File]::Copy($item.FullName, $target, $false)
        }
    }
}

function Assert-AgentUpdatePrivateTree {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid)
    $trusted = Get-TrustedInstallSids @($ServiceSid)
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push([IO.Path]::GetFullPath($Path))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Private Agent update tree contains a reparse point.' }
        if (-not (Test-TrustedInstallAcl (Get-Acl -LiteralPath $current) $trusted -Private)) { throw 'Private Agent update tree has an untrusted owner or ACL.' }
        if ($item.PSIsContainer) { foreach ($child in @(Get-ChildItem -LiteralPath $current -Force -ErrorAction Stop)) { $pending.Push($child.FullName) } }
    }
}

function Get-AgentUpdateLiveServiceApi {
    param([Parameter(Mandatory)][string]$AgentServiceName, [Parameter(Mandatory)][string]$GuardServiceName)
    return [pscustomobject]@{
        StopAgent = { $service = Get-Service -Name $AgentServiceName -ErrorAction Stop; if ($service.Status -ne 'Stopped') { Stop-Service -Name $AgentServiceName -ErrorAction Stop; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30)) } }.GetNewClosure()
        StopGuard = { $service = Get-Service -Name $GuardServiceName -ErrorAction Stop; if ($service.Status -ne 'Stopped') { Stop-Service -Name $GuardServiceName -Force -ErrorAction Stop; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30)) } }.GetNewClosure()
        StartGuard = { $service = Get-Service -Name $GuardServiceName -ErrorAction Stop; if ($service.Status -ne 'Running') { Start-Service -Name $GuardServiceName -ErrorAction Stop; $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(30)) } }.GetNewClosure()
        StartAgent = { $service = Get-Service -Name $AgentServiceName -ErrorAction Stop; if ($service.Status -ne 'Running') { Start-Service -Name $AgentServiceName -ErrorAction Stop; $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(30)) } }.GetNewClosure()
    }
}

function Invoke-AgentUpdateServiceStart {
    param([Parameter(Mandatory)]$ServiceApi)
    & $ServiceApi.StartGuard
    & $ServiceApi.StartAgent
}

function Invoke-AgentUpdateTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallDirectory,
        [Parameter(Mandatory)][string]$DataDirectory,
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string]$OperationId,
        [Parameter(Mandatory)][string]$TargetVersion,
        [Parameter(Mandatory)]$ServiceApi,
        [Security.Principal.SecurityIdentifier]$ServiceSid
    )
    $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $InstallDirectory -OperationId $OperationId
    $install = $plan.InstallDirectory
    $data = [IO.Path]::GetFullPath($DataDirectory)
    $package = [IO.Path]::GetFullPath($PackageRoot)
    if (-not (Test-Path -LiteralPath $data -PathType Container)) { throw 'Existing Agent data directory is missing.' }
    if ([IO.Path]::GetPathRoot($install) -ine [IO.Path]::GetPathRoot($data)) { throw 'Agent binaries and rollback state must be on their existing installation volume.' }
    Assert-AgentUpdatePackageRoot -PackageRoot $package -TargetVersion $TargetVersion | Out-Null
    $helperPath = Join-Path $package 'PrivateInstallFiles.ps1'
    if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) { throw 'Private install helpers are missing from the package.' }
    . ([ScriptBlock]::Create([IO.File]::ReadAllText($helperPath)))
    if (-not $ServiceSid) { $ServiceSid = Get-ServiceSid -ServiceName 'KamenRaider Agent' }
    if (-not (Test-Path -LiteralPath (Join-Path $data 'identity\identity.json') -PathType Leaf)) { throw 'Agent identity file is missing.' }
    if ((Test-Path -LiteralPath $plan.StageDirectory) -and -not (Test-Path -LiteralPath $plan.BackupDirectory)) { throw 'A previous Agent update stage is present; refusing ambiguous recovery.' }
    if (-not (Test-Path -LiteralPath $install -PathType Container) -and -not (Test-Path -LiteralPath $plan.BackupDirectory -PathType Container)) { throw 'Existing Agent installation and its rollback backup are both missing.' }

    if (Test-Path -LiteralPath $plan.BackupDirectory -PathType Container) {
        if (-not (Test-Path -LiteralPath $install -PathType Container)) {
            Copy-AgentUpdateTree -Source $plan.BackupDirectory -Destination $install -ServiceSid $ServiceSid -ReadOnlyRoot
            Set-PrivateInstallAcl -Path (Join-Path $install 'appsettings.json') -ServiceSid $ServiceSid
            Assert-AgentUpdatePrivateTree -Path $install -ServiceSid $ServiceSid
            if (Test-Path -LiteralPath $plan.StageDirectory) { Remove-Item -LiteralPath $plan.StageDirectory -Recurse -Force }
            if (Test-Path -LiteralPath $plan.PriorDirectory) { Remove-Item -LiteralPath $plan.PriorDirectory -Recurse -Force }
            Invoke-AgentUpdateServiceStart -ServiceApi $ServiceApi
            return [pscustomobject]@{ Outcome = 'rolledBack'; InstallDirectory = $install; BackupDirectory = $plan.BackupDirectory; Message = 'Interrupted Agent swap restored from its protected backup.' }
        }
        $installedVersion = Get-AgentUpdatePackageVersion -AgentDirectory $install
        if ($installedVersion -ceq $TargetVersion) {
            Invoke-AgentUpdateServiceStart -ServiceApi $ServiceApi
            return [pscustomobject]@{ Outcome = 'applied'; InstallDirectory = $install; BackupDirectory = $plan.BackupDirectory; Message = 'The requested Agent version was already swapped; backup retained pending heartbeat.' }
        }
        if (-not (Test-AgentUpdateTreesEquivalent -First $install -Second $plan.BackupDirectory)) { throw 'A protected rollback backup exists but the installed version does not match that backup.' }
        if (Test-Path -LiteralPath $plan.StageDirectory) { Remove-Item -LiteralPath $plan.StageDirectory -Recurse -Force }
        if (Test-Path -LiteralPath $plan.PriorDirectory) { Remove-Item -LiteralPath $plan.PriorDirectory -Recurse -Force }
    }

    $configuration = Join-Path $install 'appsettings.json'
    if (-not (Test-Path -LiteralPath $configuration -PathType Leaf)) { throw 'Existing appsettings.json is missing.' }
    $configurationAcl = Get-Acl -LiteralPath $configuration -ErrorAction Stop
    $stage = $plan.StageDirectory
    $prior = $plan.PriorDirectory
    $backupCreated = $false
    $swapStarted = $false
    $servicesTouched = $false
    try {
        Copy-AgentUpdateTree -Source (Join-Path $package 'Agent') -Destination $stage -ServiceSid $ServiceSid -ReadOnlyRoot
        [IO.File]::Copy($configuration, (Join-Path $stage 'appsettings.json'), $false)
        if ($ServiceSid) { Set-PrivateInstallAcl -Path (Join-Path $stage 'appsettings.json') -ServiceSid $ServiceSid }
        else { Set-Acl -LiteralPath (Join-Path $stage 'appsettings.json') -AclObject $configurationAcl -ErrorAction Stop }
        if ((Get-AgentUpdatePackageVersion -AgentDirectory $stage) -cne $TargetVersion) { throw 'Staged Agent version does not match the requested target.' }
        Assert-AgentUpdatePrivateTree -Path $stage -ServiceSid $ServiceSid
        if (-not (Test-Path -LiteralPath $plan.BackupDirectory)) {
            if (Test-Path -LiteralPath $plan.BackupStageDirectory) { Remove-Item -LiteralPath $plan.BackupStageDirectory -Recurse -Force }
            Copy-AgentUpdateTree -Source $install -Destination $plan.BackupStageDirectory -ServiceSid $ServiceSid -ReadOnlyRoot
            Set-PrivateInstallAcl -Path (Join-Path $plan.BackupStageDirectory 'appsettings.json') -ServiceSid $ServiceSid
            Assert-AgentUpdatePrivateTree -Path $plan.BackupStageDirectory -ServiceSid $ServiceSid
            [IO.Directory]::Move($plan.BackupStageDirectory,$plan.BackupDirectory)
        }
        $backupCreated = $true
        if (Test-Path -LiteralPath $prior) { throw 'An interrupted prior directory exists; refusing to overwrite it.' }
        $servicesTouched = $true
        & $ServiceApi.StopAgent
        & $ServiceApi.StopGuard
        [IO.Directory]::Move($install, $prior)
        $swapStarted = $true
        [IO.Directory]::Move($stage, $install)
        if (Test-Path -LiteralPath $prior) {
            $resolvedPrior = [IO.Path]::GetFullPath($prior)
            if ([IO.Path]::GetDirectoryName($resolvedPrior) -ne $plan.ParentDirectory) { throw 'Unsafe update prior path.' }
            Remove-Item -LiteralPath $resolvedPrior -Recurse -Force
        }
        Invoke-AgentUpdateServiceStart -ServiceApi $ServiceApi
        return [pscustomobject]@{ Outcome = 'applied'; InstallDirectory = $install; BackupDirectory = $plan.BackupDirectory; Message = 'Agent update swapped and services started; protected backup retained pending heartbeat.' }
    } catch {
        $failure = $_.Exception.Message
        $rollbackSucceeded = $false
        if ($backupCreated -and ($swapStarted -or $servicesTouched)) {
            try {
                if ($swapStarted) {
                    try { & $ServiceApi.StopAgent } catch { }
                    try { & $ServiceApi.StopGuard } catch { }
                    if (Test-Path -LiteralPath $install) {
                        if (Test-Path -LiteralPath $stage) { throw 'Both failed install and stage paths exist; preserving all files for manual recovery.' }
                        [IO.Directory]::Move($install, $stage)
                    }
                    Copy-AgentUpdateTree -Source $plan.BackupDirectory -Destination $install -ServiceSid $ServiceSid -ReadOnlyRoot
                    Set-PrivateInstallAcl -Path (Join-Path $install 'appsettings.json') -ServiceSid $ServiceSid
                    Assert-AgentUpdatePrivateTree -Path $install -ServiceSid $ServiceSid
                    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
                    if (Test-Path -LiteralPath $prior) { Remove-Item -LiteralPath $prior -Recurse -Force }
                }
                Invoke-AgentUpdateServiceStart -ServiceApi $ServiceApi
                $rollbackSucceeded = $true
            } catch { $failure += ' Rollback error: ' + $_.Exception.Message }
        }
        if ($rollbackSucceeded) {
            return [pscustomobject]@{ Outcome = 'rolledBack'; InstallDirectory = $install; BackupDirectory = $plan.BackupDirectory; Message = $failure }
        }
        if ($backupCreated) { return [pscustomobject]@{ Outcome = 'failed'; InstallDirectory = $install; BackupDirectory = $plan.BackupDirectory; Message = $failure } }
        throw
    } finally {
        if ((Test-Path -LiteralPath $plan.BackupStageDirectory) -and -not (Test-Path -LiteralPath $plan.BackupDirectory)) {
            $resolvedBackupStage = [IO.Path]::GetFullPath($plan.BackupStageDirectory)
            if ([IO.Path]::GetDirectoryName($resolvedBackupStage) -ne $plan.ParentDirectory -or [IO.Path]::GetFileName($resolvedBackupStage) -ne [IO.Path]::GetFileName($plan.BackupStageDirectory)) { throw 'Unsafe rollback staging cleanup path.' }
            Remove-Item -LiteralPath $resolvedBackupStage -Recurse -Force
        }
        if (-not $swapStarted -and (Test-Path -LiteralPath $stage)) {
            $resolvedStage = [IO.Path]::GetFullPath($stage)
            if ([IO.Path]::GetDirectoryName($resolvedStage) -ne $plan.ParentDirectory -or [IO.Path]::GetFileName($resolvedStage) -ne [IO.Path]::GetFileName($plan.StageDirectory)) { throw 'Unsafe Agent staging cleanup path.' }
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}

if ($DefineFunctionsOnly) { return }
if ($Action -ne 'Apply') { throw 'Unsupported Agent update action.' }
if (-not $InstallDirectory -or -not $DataDirectory -or -not $PackageRoot -or -not $OperationId -or -not $TargetVersion) { throw 'InstallDirectory, DataDirectory, PackageRoot, OperationId, and TargetVersion are required.' }
if (-not $ServiceSid) { $parsedServiceSid = $null } else { $parsedServiceSid = [Security.Principal.SecurityIdentifier]::new($ServiceSid) }
$serviceApi = Get-AgentUpdateLiveServiceApi -AgentServiceName 'KamenRaider Agent' -GuardServiceName 'KamenRaider Stream Guard'
$result = Invoke-AgentUpdateTransaction -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory -PackageRoot $PackageRoot -OperationId $OperationId -TargetVersion $TargetVersion -ServiceSid $parsedServiceSid -ServiceApi $serviceApi
$result
