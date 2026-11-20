$ErrorActionPreference = 'Stop'
$removeScript = Join-Path $PSScriptRoot 'Remove-StreamGuard.ps1'
. $removeScript -DefineFunctionsOnly

$deduped = @(Get-StreamGuardRemovalDependencies -Existing @('VendorService', 'kamenraider stream guard', 'vendorService') -RemovedDependency 'KamenRaider Stream Guard')
if ($deduped.Count -ne 1 -or $deduped[0] -ne 'VendorService') {
    throw 'Dependency filtering must remove the Guard dependency case-insensitively and de-duplicate the remaining services.'
}
$filtered = @(Get-StreamGuardRemovalDependencies -Existing @('VendorService', 'KamenRaider Stream Guard', 'SecondVendor') -RemovedDependency 'KamenRaider Stream Guard')
if ($filtered.Count -ne 2 -or $filtered[0] -ne 'VendorService' -or $filtered[1] -ne 'SecondVendor') {
    throw 'Dependency filtering must preserve every unrelated dependency in order.'
}

function New-LegacyState {
    [pscustomobject]@{
        GuardServicePresent = $true
        GuardState = 'Running'
        GuardConfiguration = [pscustomobject]@{ Account = 'LocalSystem'; StartType = 2; Dependencies = @(); BinaryPath = '"C:\Program Files\KamenRaider\Agent\Agent.exe" --contentRoot "C:\Program Files\KamenRaider\Agent" --stream-guard' }
        AgentServicePresent = $true
        AgentState = 'Running'
        AgentConfiguration = [pscustomobject]@{ Account = 'NT SERVICE\KamenRaider Agent'; StartType = 2; Dependencies = @('KamenRaider Stream Guard', 'VendorService') }
        SunshineServicePresent = $true
        SunshineState = 'Stopped'
        SunshineConfiguration = [pscustomobject]@{ Account = 'LocalSystem'; StartType = 3; Dependencies = @('VendorService', 'KamenRaider Stream Guard') }
        FirewallRulePresent = $true
        FirewallRuleCount = 1
        FirewallRuleSourceType = 'Local'
    }
}
function New-CleanState {
    [pscustomobject]@{
        GuardServicePresent = $false
        GuardState = $null
        GuardConfiguration = $null
        AgentServicePresent = $true
        AgentState = 'Running'
        AgentConfiguration = [pscustomobject]@{ Account = 'LocalSystem'; StartType = 2; Dependencies = @('VendorService') }
        SunshineServicePresent = $true
        SunshineState = 'Running'
        SunshineConfiguration = [pscustomobject]@{ Account = 'LocalSystem'; StartType = 2; Dependencies = @('VendorService') }
        FirewallRulePresent = $false
        FirewallRuleCount = 0
        FirewallRuleSourceType = $null
    }
}

$backupRoot = Join-Path ([IO.Path]::GetTempPath()) ('kr-stream-guard-removal-' + [guid]::NewGuid().ToString('N'))
$backupDirectory = Join-Path $backupRoot 'backup'
try {
    $legacyPlan = New-StreamGuardRemovalPlan -State (New-LegacyState) -BackupDirectory $backupDirectory
    $expectedActions = @(
        'backup-service-and-firewall-configuration',
        'remove-agent-guard-dependency',
        'reconfigure-agent-local-system-account',
        'remove-sunshine-guard-dependency-and-restore-automatic-startup',
        'stop-guard-service',
        'delete-guard-service',
        'delete-firewall-rule',
        'start-sunshine-service',
        'restart-agent-service',
        'verify-removal-and-sunshine'
    )
    if ($legacyPlan.BlockedReason -or $legacyPlan.AlreadyClean) { throw 'A legacy Stream Guard installation must be removable and not reported clean.' }
    if (($legacyPlan.Actions -join ',') -ne ($expectedActions -join ',')) { throw "Legacy removal action order is wrong: $($legacyPlan.Actions -join ',')" }
    if (-not $legacyPlan.GuardService.Present -or -not $legacyPlan.FirewallRule.Present -or -not $legacyPlan.FirewallRule.Delete) { throw 'Legacy plan must remove the Guard service and its managed local firewall rule.' }
    if ($legacyPlan.AgentService.PriorAccount -ne 'NT SERVICE\KamenRaider Agent' -or $legacyPlan.AgentService.TargetAccount -ne 'LocalSystem' -or -not $legacyPlan.AgentService.AccountChange) {
        throw 'Legacy plan must migrate the Agent service to LocalSystem and record its prior account.'
    }
    if (($legacyPlan.AgentService.RemainingDependencies -join ',') -ne 'VendorService') { throw 'Legacy plan must preserve unrelated Agent service dependencies.' }
    if (($legacyPlan.SunshineService.RemainingDependencies -join ',') -ne 'VendorService' -or $legacyPlan.SunshineService.TargetStartType -ne 2 -or -not $legacyPlan.SunshineService.StartAfterRemoval) {
        throw 'Legacy plan must restore SunshineService dependencies, automatic startup and running state.'
    }
    if ($legacyPlan.Backup.Directory -ne [IO.Path]::GetFullPath($backupDirectory)) { throw 'Removal plan must report the protected backup location.' }

    $cleanPlan = New-StreamGuardRemovalPlan -State (New-CleanState) -BackupDirectory $backupDirectory
    if (-not $cleanPlan.AlreadyClean -or $cleanPlan.BlockedReason -or ($cleanPlan.Actions -join ',') -ne 'verify-removal-and-sunshine') {
        throw 'A machine without Guard remnants and with a LocalSystem Agent must be reported clean without another backup.'
    }

    $blockedRule = New-LegacyState
    $blockedRule.FirewallRuleSourceType = 'GroupPolicy'
    $blockedPlan = New-StreamGuardRemovalPlan -State $blockedRule -BackupDirectory $backupDirectory
    if ($blockedPlan.BlockedReason -notmatch 'Group Policy' -or $blockedPlan.Actions.Count -ne 0 -or $blockedPlan.FirewallRule.Delete) {
        throw 'A Group Policy owned managed rule must block removal without actions.'
    }

    $blockedSunshine = New-LegacyState
    $blockedSunshine.SunshineServicePresent = $false
    $blockedSunshine.SunshineState = $null
    $blockedSunshine.SunshineConfiguration = $null
    $blockedSunshinePlan = New-StreamGuardRemovalPlan -State $blockedSunshine -BackupDirectory $backupDirectory
    if ($blockedSunshinePlan.BlockedReason -notmatch 'SunshineService' -or $blockedSunshinePlan.Actions.Count -ne 0) {
        throw 'A machine without SunshineService must be blocked before any Guard mutation.'
    }

    # Mock every OS boundary. Nothing below touches the real SCM or firewall.
    $script:events = [Collections.Generic.List[string]]::new()
    $script:services = @{}
    $script:startup = @{}
    $script:configs = @{}
    $script:rulePresent = $true
    $script:failDelete = $false
    function Reset-ScgFixture {
        $script:events.Clear()
        $script:rulePresent = $true
        $script:configs = @{
            'KamenRaider Stream Guard' = [pscustomobject]@{ Account = 'LocalSystem'; StartType = 2; Dependencies = @(); BinaryPath = 'guard' }
            'KamenRaider Agent' = [pscustomobject]@{ Account = 'NT SERVICE\KamenRaider Agent'; StartType = 2; Dependencies = @('KamenRaider Stream Guard', 'VendorService') }
            'SunshineService' = [pscustomobject]@{ Account = 'LocalSystem'; StartType = 3; Dependencies = @('VendorService', 'KamenRaider Stream Guard') }
        }
        $script:services = @{ 'KamenRaider Stream Guard' = 'Running'; 'KamenRaider Agent' = 'Running'; 'SunshineService' = 'Stopped' }
        $script:startup = @{ 'KamenRaider Stream Guard' = 'Automatic'; 'KamenRaider Agent' = 'Automatic'; 'SunshineService' = 'Manual' }
        $script:failDelete = $false
    }
    function Get-Service {
        param($Name, $ErrorAction)
        if (-not $script:services.ContainsKey($Name)) { return $null }
        [pscustomobject]@{ Name = $Name; Status = $script:services[$Name]; StartType = $script:startup[$Name] }
    }
    function Stop-Service {
        param($Name, $ErrorAction)
        $script:events.Add("stop:$Name"); $script:services[$Name] = 'Stopped'
    }
    function Start-Service {
        param($Name, $ErrorAction)
        $script:events.Add("start:$Name"); $script:services[$Name] = 'Running'
    }
    function Set-Service {
        param($Name, $StartupType, $ErrorAction)
        $script:events.Add("startup:$Name=$StartupType"); $script:startup[$Name] = [string]$StartupType
    }
    function Get-StreamGuardRemovalServiceConfiguration {
        param($Name)
        $script:configs[$Name]
    }
    function Set-StreamGuardRemovalServiceConfiguration {
        param($Name, $StartType, $Dependencies)
        $script:events.Add("config:$Name")
        $script:configs[$Name] = [pscustomobject]@{ Account = $script:configs[$Name].Account; StartType = $StartType; Dependencies = @($Dependencies); BinaryPath = $script:configs[$Name].BinaryPath }
    }
    function Set-StreamGuardRemovalServiceAccount {
        param($ServiceName, $Account)
        $script:events.Add("account:$ServiceName=$Account")
        $script:configs[$ServiceName] = [pscustomobject]@{ Account = $Account; StartType = $script:configs[$ServiceName].StartType; Dependencies = @($script:configs[$ServiceName].Dependencies); BinaryPath = $script:configs[$ServiceName].BinaryPath }
    }
    function Remove-StreamGuardWindowsService {
        param($ServiceName)
        if ($script:failDelete) { throw 'injected delete failure' }
        $script:events.Add("delete:$ServiceName"); $script:services.Remove($ServiceName); $script:configs.Remove($ServiceName)
    }
    function Get-StreamGuardManagedFirewallRule {
        if ($script:rulePresent) { return @([pscustomobject]@{ PolicyStoreSourceType = 'Local'; DisplayName = 'KamenRaider Stream Guard - Sunshine' }) }
        return @()
    }
    function Remove-StreamGuardManagedFirewallRule {
        $script:events.Add('remove-firewall'); $script:rulePresent = $false
    }
    function Ensure-SunshineServiceAutomaticAndRunning {
        param($ServiceName, $TimeoutSeconds)
        $script:events.Add("sunshine-ready:$ServiceName")
        $script:startup[$ServiceName] = 'Automatic'; $script:services[$ServiceName] = 'Running'
        return [pscustomobject]@{ ServiceName = $ServiceName; Startup = 'Automatic'; State = 'Running' }
    }

    Reset-ScgFixture
    $state = Get-StreamGuardRemovalLiveState
    $removalPlan = New-StreamGuardRemovalPlan -State $state -BackupDirectory $backupDirectory
    $result = Invoke-StreamGuardRemoval -State $state -Plan $removalPlan -BackupDirectory $backupDirectory
    if ($result.Status -ne 'removed' -or -not $result.Verified) { throw 'Mocked Stream Guard removal did not verify.' }
    if (-not (Test-Path -LiteralPath $result.BackupPath -PathType Leaf)) { throw 'Removal must publish its service and firewall backup before mutating state.' }
    $backup = [IO.File]::ReadAllText($result.BackupPath) | ConvertFrom-Json
    if ($backup.agentService.priorAccount -ne 'NT SERVICE\KamenRaider Agent' -or $backup.guardService.present -ne $true -or
        $backup.sunshineService.configuration.StartType -ne 3 -or $backup.firewallRule.sourceType -ne 'Local') {
        throw 'Removal backup must capture the prior Guard/Agent/Sunshine configuration and firewall rule.'
    }
    if ($script:services.ContainsKey('KamenRaider Stream Guard') -or $script:rulePresent) { throw 'Mocked removal must delete the Guard service and firewall rule.' }
    if ($script:configs['KamenRaider Agent'].Account -ne 'LocalSystem' -or ($script:configs['KamenRaider Agent'].Dependencies -join ',') -ne 'VendorService') {
        throw 'Mocked removal must migrate the Agent to LocalSystem and drop only the Guard dependency.'
    }
    if ($script:configs['SunshineService'].StartType -ne 2 -or ($script:configs['SunshineService'].Dependencies -join ',') -ne 'VendorService') {
        throw 'Mocked removal must restore SunshineService automatic startup and remove only the Guard dependency.'
    }
    if ($script:services['SunshineService'] -ne 'Running' -or $script:services['KamenRaider Agent'] -ne 'Running') {
        throw 'Mocked removal must leave SunshineService and the migrated Agent running.'
    }
    $mutationOrder = @($script:events | Where-Object { $_ -notlike 'sunshine-ready:*' })
    $expectedOrder = @(
        'config:KamenRaider Agent',
        'stop:KamenRaider Agent',
        'account:KamenRaider Agent=LocalSystem',
        'config:SunshineService',
        'stop:KamenRaider Stream Guard',
        'delete:KamenRaider Stream Guard',
        'remove-firewall',
        'start:KamenRaider Agent'
    )
    if (($mutationOrder -join ',') -ne ($expectedOrder -join ',')) { throw "Unexpected mocked removal mutation order: $($mutationOrder -join ',')" }
    $journal = @(Get-ChildItem -LiteralPath $backupDirectory -Filter 'stream-guard-removal-succeeded-*.json')
    if ($journal.Count -ne 1) { throw 'Removal must publish one success journal next to the backup.' }

    $secondPlan = New-StreamGuardRemovalPlan -State (Get-StreamGuardRemovalLiveState) -BackupDirectory $backupDirectory
    if (-not $secondPlan.AlreadyClean) { throw 'A completed removal must be idempotent on a second run.' }

    Reset-ScgFixture
    $state = Get-StreamGuardRemovalLiveState
    $removalPlan = New-StreamGuardRemovalPlan -State $state -BackupDirectory $backupDirectory
    $script:failDelete = $true
    $failed = $false
    try { Invoke-StreamGuardRemoval -State $state -Plan $removalPlan -BackupDirectory $backupDirectory | Out-Null } catch { $failed = $_.Exception -is [AggregateException] }
    if (-not $failed) { throw 'A failing Guard delete must surface an AggregateException.' }
    $failureJournal = @(Get-ChildItem -LiteralPath $backupDirectory -Filter 'stream-guard-removal-failed-*.json')
    if ($failureJournal.Count -ne 1) { throw 'A failed removal must retain a failure journal and its backup for recovery.' }
    $failure = [IO.File]::ReadAllText($failureJournal[0].FullName) | ConvertFrom-Json
    if ($failure.status -ne 'failed' -or $failure.error -notmatch 'injected delete failure' -or -not $failure.backupPath) {
        throw 'The failure journal must record the error and the retained backup path.'
    }

    $source = [IO.File]::ReadAllText($removeScript)
    foreach ($required in @('KamenRaider Stream Guard - Sunshine', 'Stop-Service', 'scPath delete', 'Remove-StreamGuardManagedFirewallRule',
        'Set-StreamGuardRemovalServiceAccount', 'obj= $Account', 'TargetStartType', 'Ensure-SunshineServiceAutomaticAndRunning', 'Write-StreamGuardRemovalBackup')) {
        if (-not $source.Contains($required)) { throw "Remove-StreamGuard.ps1 is missing required operation: $required" }
    }
    Write-Output 'Legacy Stream Guard removal plan, mocked migration, idempotency and safe-failure tests passed; no services or firewall rules changed.'
} finally {
    if (Test-Path -LiteralPath $backupRoot) {
        $resolved = [IO.Path]::GetFullPath($backupRoot)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-stream-guard-removal-*') { throw 'Unsafe Stream Guard removal test cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
