[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [string]$InstallScriptRoot = $PSScriptRoot,
    [string]$BackupDirectory,
    [switch]$Plan,
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'
$script:GuardServiceName = 'KamenRaider Stream Guard'
$script:AgentServiceName = 'KamenRaider Agent'
$script:SunshineServiceName = 'SunshineService'
$script:StreamRuleName = 'KamenRaider Stream Guard - Sunshine'
$script:SunshineStartType = [uint32]2
$script:RemovalBackupDirectory = $null

function Get-StreamGuardRemovalBackupDirectory {
    if ($script:RemovalBackupDirectory) { return $script:RemovalBackupDirectory }
    return Join-Path (Join-Path $env:ProgramData 'KamenRaider') 'StreamGuardMigration'
}

function Get-StreamGuardRemovalServiceConfiguration {
    param([Parameter(Mandatory)][string]$Name)
    [KamenRaider.Install.NativeServiceManagerV2]::QueryConfiguration($Name)
}

function Set-StreamGuardRemovalServiceConfiguration {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][uint32]$StartType,
        [string[]]$Dependencies = @()
    )
    [KamenRaider.Install.NativeServiceManagerV2]::SetStartAndDependencies($Name, $StartType, $Dependencies)
}

function Remove-StreamGuardWindowsService {
    param([Parameter(Mandatory)][string]$ServiceName)
    $scPath = Join-Path $env:SystemRoot 'System32\sc.exe'
    $output = @(& $scPath delete $ServiceName 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Deleting the legacy '$ServiceName' service failed: $($output -join ' ')" }
}

function Set-StreamGuardRemovalServiceAccount {
    param([Parameter(Mandatory)][string]$ServiceName, [Parameter(Mandatory)][string]$Account)
    $scPath = Join-Path $env:SystemRoot 'System32\sc.exe'
    $output = @(& $scPath config $ServiceName obj= $Account 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Reconfiguring the '$ServiceName' service account failed: $($output -join ' ')" }
}

function Get-StreamGuardManagedFirewallRule {
    if (-not (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue)) { return @() }
    return @(Get-NetFirewallRule -PolicyStore ActiveStore -Name $script:StreamRuleName -ErrorAction SilentlyContinue)
}

function Remove-StreamGuardManagedFirewallRule {
    Remove-NetFirewallRule -PolicyStore PersistentStore -Name $script:StreamRuleName -ErrorAction Stop
}

function Wait-StreamGuardServiceState {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$State, [int]$TimeoutSeconds = 30)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if (-not $service) { throw "Service '$Name' disappeared while waiting for $State." }
        if ($service.Status.ToString() -eq $State) { return }
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "Service '$Name' did not reach $State within $TimeoutSeconds seconds."
}

function Wait-StreamGuardServiceAbsent {
    param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSeconds = 15)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "Service '$Name' was not removed from the Service Control Manager within $TimeoutSeconds seconds."
}

function Get-StreamGuardRemovalDependencies {
    param([string[]]$Existing, [Parameter(Mandatory)][string]$RemovedDependency)
    $result = [Collections.Generic.List[string]]::new()
    foreach ($name in @($Existing)) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ([string]::Equals($name, $RemovedDependency, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $result.Exists([Predicate[string]]{ param($item) [string]::Equals($item, $name, [StringComparison]::OrdinalIgnoreCase) })) { $result.Add($name) }
    }
    return $result.ToArray()
}

function Get-StreamGuardRemovalLiveState {
    $guardService = Get-Service -Name $script:GuardServiceName -ErrorAction SilentlyContinue
    $agentService = Get-Service -Name $script:AgentServiceName -ErrorAction SilentlyContinue
    $sunshineService = Get-Service -Name $script:SunshineServiceName -ErrorAction SilentlyContinue
    $firewallRule = @(Get-StreamGuardManagedFirewallRule)
    $guardConfiguration = $null
    $agentConfiguration = $null
    $sunshineConfiguration = $null
    if ($guardService) { $guardConfiguration = Get-StreamGuardRemovalServiceConfiguration -Name $script:GuardServiceName }
    if ($agentService) { $agentConfiguration = Get-StreamGuardRemovalServiceConfiguration -Name $script:AgentServiceName }
    if ($sunshineService) { $sunshineConfiguration = Get-StreamGuardRemovalServiceConfiguration -Name $script:SunshineServiceName }
    [pscustomobject]@{
        GuardServicePresent = [bool]$guardService
        GuardState = if ($guardService) { $guardService.Status.ToString() } else { $null }
        GuardConfiguration = $guardConfiguration
        AgentServicePresent = [bool]$agentService
        AgentState = if ($agentService) { $agentService.Status.ToString() } else { $null }
        AgentConfiguration = $agentConfiguration
        SunshineServicePresent = [bool]$sunshineService
        SunshineState = if ($sunshineService) { $sunshineService.Status.ToString() } else { $null }
        SunshineConfiguration = $sunshineConfiguration
        FirewallRulePresent = $firewallRule.Count -gt 0
        FirewallRuleCount = $firewallRule.Count
        FirewallRuleSourceType = if ($firewallRule.Count -eq 1) { [string]$firewallRule[0].PolicyStoreSourceType } else { $null }
    }
}

function New-StreamGuardRemovalPlan {
    param([Parameter(Mandatory)]$State, [string]$BackupDirectory)
    $guardName = $script:GuardServiceName
    $agentDependencyPresent = $false
    $agentRemaining = @()
    if ($State.AgentServicePresent -and $State.AgentConfiguration) {
        $existing = @([string[]]$State.AgentConfiguration.Dependencies)
        $agentDependencyPresent = @($existing | Where-Object { [string]::Equals($_, $guardName, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
        $agentRemaining = @(Get-StreamGuardRemovalDependencies -Existing $existing -RemovedDependency $guardName)
    }
    $sunshineDependencyPresent = $false
    $sunshineRemaining = @()
    $sunshineStartType = $null
    if ($State.SunshineServicePresent -and $State.SunshineConfiguration) {
        $existing = @([string[]]$State.SunshineConfiguration.Dependencies)
        $sunshineDependencyPresent = @($existing | Where-Object { [string]::Equals($_, $guardName, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
        $sunshineRemaining = @(Get-StreamGuardRemovalDependencies -Existing $existing -RemovedDependency $guardName)
        $sunshineStartType = [uint32]$State.SunshineConfiguration.StartType
    }
    $agentAccount = if ($State.AgentServicePresent -and $State.AgentConfiguration) { [string]$State.AgentConfiguration.Account } else { $null }
    $agentAccountChange = [bool]($State.AgentServicePresent -and $agentAccount -and
        -not [string]::Equals($agentAccount, 'LocalSystem', [StringComparison]::OrdinalIgnoreCase))
    $agentRestartRequired = $agentAccountChange -and [string]$State.AgentState -eq 'Running'
    $blockedReason = $null
    if (-not $State.SunshineServicePresent) {
        $blockedReason = "Windows service '$($script:SunshineServiceName)' is not installed; it must exist before the legacy Stream Guard can be removed."
    } elseif ($State.FirewallRulePresent -and [string]$State.FirewallRuleSourceType -eq 'GroupPolicy') {
        $blockedReason = "The managed Stream Guard firewall rule is owned by Group Policy; remove rule '$($script:StreamRuleName)' from domain policy instead."
    } elseif ($State.FirewallRuleCount -gt 1) {
        $blockedReason = "Multiple active rules use the managed Stream Guard firewall rule name '$($script:StreamRuleName)'."
    }

    $actions = [Collections.Generic.List[string]]::new()
    $alreadyClean = $false
    if (-not $blockedReason) {
        $sunshineNeedsConfiguration = $sunshineDependencyPresent -or ([uint32]$sunshineStartType -ne $script:SunshineStartType)
        $alreadyClean = -not $State.GuardServicePresent -and -not $State.FirewallRulePresent -and
            -not $agentDependencyPresent -and -not $sunshineNeedsConfiguration -and
            -not $agentAccountChange -and [string]$State.SunshineState -eq 'Running'
        if (-not $alreadyClean) {
            $actions.Add('backup-service-and-firewall-configuration')
            if ($agentDependencyPresent) { $actions.Add('remove-agent-guard-dependency') }
            if ($agentAccountChange) { $actions.Add('reconfigure-agent-local-system-account') }
            if ($sunshineNeedsConfiguration) { $actions.Add('remove-sunshine-guard-dependency-and-restore-automatic-startup') }
            if ($State.GuardServicePresent) {
                $actions.Add('stop-guard-service')
                $actions.Add('delete-guard-service')
            }
            if ($State.FirewallRulePresent) { $actions.Add('delete-firewall-rule') }
            $actions.Add('start-sunshine-service')
            if ($agentRestartRequired) { $actions.Add('restart-agent-service') }
        }
        $actions.Add('verify-removal-and-sunshine')
    }

    [pscustomobject]@{
        SchemaVersion = 1
        GuardService = [pscustomobject]@{
            Name = $guardName
            Present = [bool]$State.GuardServicePresent
            State = $State.GuardState
            Delete = [bool]$State.GuardServicePresent
        }
        FirewallRule = [pscustomobject]@{
            Name = $script:StreamRuleName
            Present = [bool]$State.FirewallRulePresent
            SourceType = $State.FirewallRuleSourceType
            Delete = [bool]($State.FirewallRulePresent -and -not $blockedReason)
        }
        AgentService = [pscustomobject]@{
            Name = $script:AgentServiceName
            Present = [bool]$State.AgentServicePresent
            PriorAccount = $agentAccount
            TargetAccount = 'LocalSystem'
            AccountChange = $agentAccountChange
            RestartRequired = $agentRestartRequired
            DependencyPresent = $agentDependencyPresent
            RemovedDependency = $guardName
            RemainingDependencies = @($agentRemaining)
        }
        SunshineService = [pscustomobject]@{
            Name = $script:SunshineServiceName
            Present = [bool]$State.SunshineServicePresent
            State = $State.SunshineState
            StartType = $sunshineStartType
            TargetStartType = $script:SunshineStartType
            DependencyPresent = $sunshineDependencyPresent
            RemainingDependencies = @($sunshineRemaining)
            StartAfterRemoval = $true
        }
        Backup = [pscustomobject]@{
            Directory = if ($BackupDirectory) { [IO.Path]::GetFullPath($BackupDirectory) } else { Get-StreamGuardRemovalBackupDirectory }
            Contents = 'Guard/Agent/Sunshine service configuration and the managed firewall rule shape'
        }
        AlreadyClean = $alreadyClean
        BlockedReason = $blockedReason
        Actions = @($actions)
    }
}

function Write-StreamGuardRemovalJournal {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][string]$OperationId,
        [Parameter(Mandatory)]$State,
        [string]$BackupPath,
        [string[]]$Steps = @(),
        [string]$Error = $null
    )
    $journalPath = Join-Path ([IO.Path]::GetFullPath($Directory)) ('stream-guard-removal-' + $Status + '-' + $OperationId + '.json')
    $document = [ordered]@{
        schemaVersion = 1
        status = $Status
        operationId = $OperationId
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O',[Globalization.CultureInfo]::InvariantCulture)
        backupPath = $BackupPath
        steps = @($Steps)
        error = $Error
        observed = [ordered]@{
            guardServicePresent = [bool]$State.GuardServicePresent
            guardState = $State.GuardState
            agentAccount = if ($State.AgentConfiguration) { [string]$State.AgentConfiguration.Account } else { $null }
            agentDependencies = if ($State.AgentConfiguration) { @([string[]]$State.AgentConfiguration.Dependencies) } else { @() }
            sunshineDependencies = if ($State.SunshineConfiguration) { @([string[]]$State.SunshineConfiguration.Dependencies) } else { @() }
            sunshineStartType = if ($State.SunshineConfiguration) { [uint32]$State.SunshineConfiguration.StartType } else { $null }
            sunshineState = $State.SunshineState
            firewallRulePresent = [bool]$State.FirewallRulePresent
            firewallRuleSourceType = $State.FirewallRuleSourceType
        }
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $document -Depth 6))
    [IO.File]::WriteAllBytes($journalPath, $bytes)
    return $journalPath
}

function Write-StreamGuardRemovalBackup {
    param([Parameter(Mandatory)]$State, [Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$OperationId)
    if (-not (Test-Path -LiteralPath $Directory)) { New-Item -ItemType Directory -Path $Directory -Force | Out-Null }
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw 'Stream Guard removal backup location is not a directory.' }
    $path = Join-Path ([IO.Path]::GetFullPath($Directory)) ('stream-guard-removal-backup-' + $OperationId + '.json')
    $document = [ordered]@{
        schemaVersion = 1
        operationId = $OperationId
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O',[Globalization.CultureInfo]::InvariantCulture)
        guardService = [ordered]@{
            name = $script:GuardServiceName
            present = [bool]$State.GuardServicePresent
            state = $State.GuardState
            configuration = $State.GuardConfiguration
        }
        agentService = [ordered]@{
            name = $script:AgentServiceName
            present = [bool]$State.AgentServicePresent
            priorAccount = if ($State.AgentConfiguration) { [string]$State.AgentConfiguration.Account } else { $null }
            configuration = $State.AgentConfiguration
        }
        sunshineService = [ordered]@{
            name = $script:SunshineServiceName
            present = [bool]$State.SunshineServicePresent
            state = $State.SunshineState
            configuration = $State.SunshineConfiguration
        }
        firewallRule = [ordered]@{
            name = $script:StreamRuleName
            present = [bool]$State.FirewallRulePresent
            sourceType = $State.FirewallRuleSourceType
        }
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $document -Depth 8))
    [IO.File]::WriteAllBytes($path, $bytes)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Stream Guard removal backup could not be verified.' }
    return $path
}

function Invoke-StreamGuardRemoval {
    param([Parameter(Mandatory)]$State, [Parameter(Mandatory)]$Plan, [Parameter(Mandatory)][string]$BackupDirectory)
    $operationId = [Guid]::NewGuid().ToString('N')
    $steps = [Collections.Generic.List[string]]::new()
    $agentRestarted = $false
    $backupPath = Write-StreamGuardRemovalBackup -State $State -Directory $BackupDirectory -OperationId $operationId
    $steps.Add('backup-service-and-firewall-configuration')
    try {
        if ($Plan.AgentService.DependencyPresent) {
            $configuration = $State.AgentConfiguration
            Set-StreamGuardRemovalServiceConfiguration -Name $script:AgentServiceName `
                -StartType ([uint32]$configuration.StartType) -Dependencies @($Plan.AgentService.RemainingDependencies)
            $steps.Add('remove-agent-guard-dependency')
        }
        if ($Plan.AgentService.AccountChange) {
            $agentBeforeChange = Get-Service -Name $script:AgentServiceName -ErrorAction SilentlyContinue
            if ($agentBeforeChange -and $agentBeforeChange.Status.ToString() -ne 'Stopped') {
                Stop-Service -Name $script:AgentServiceName -ErrorAction Stop
                Wait-StreamGuardServiceState -Name $script:AgentServiceName -State 'Stopped'
                $agentRestarted = $true
            }
            Set-StreamGuardRemovalServiceAccount -ServiceName $script:AgentServiceName -Account 'LocalSystem'
            $verifiedAccount = (Get-StreamGuardRemovalServiceConfiguration -Name $script:AgentServiceName).Account
            if (-not [string]::Equals([string]$verifiedAccount, 'LocalSystem', [StringComparison]::OrdinalIgnoreCase)) {
                throw "The Agent service account did not change to LocalSystem (observed '$verifiedAccount')."
            }
            $steps.Add('reconfigure-agent-local-system-account')
        }
        if ($Plan.SunshineService.DependencyPresent -or [uint32]$Plan.SunshineService.StartType -ne $script:SunshineStartType) {
            Set-StreamGuardRemovalServiceConfiguration -Name $script:SunshineServiceName `
                -StartType $script:SunshineStartType -Dependencies @($Plan.SunshineService.RemainingDependencies)
            $steps.Add('remove-sunshine-guard-dependency-and-restore-automatic-startup')
        }
        if ($Plan.GuardService.Present) {
            $guard = Get-Service -Name $script:GuardServiceName -ErrorAction SilentlyContinue
            if ($guard -and $guard.Status.ToString() -ne 'Stopped') {
                Stop-Service -Name $script:GuardServiceName -ErrorAction Stop
                Wait-StreamGuardServiceState -Name $script:GuardServiceName -State 'Stopped'
            }
            $steps.Add('stop-guard-service')
            Remove-StreamGuardWindowsService -ServiceName $script:GuardServiceName
            Wait-StreamGuardServiceAbsent -Name $script:GuardServiceName
            $steps.Add('delete-guard-service')
        }
        if ($Plan.FirewallRule.Present) {
            Remove-StreamGuardManagedFirewallRule
            if (@(Get-StreamGuardManagedFirewallRule).Count -ne 0) { throw 'The managed Stream Guard firewall rule is still present after deletion.' }
            $steps.Add('delete-firewall-rule')
        }
        $sunshine = Ensure-SunshineServiceAutomaticAndRunning -ServiceName $script:SunshineServiceName
        $steps.Add('start-sunshine-service')
        if ($agentRestarted) {
            $agentService = Get-Service -Name $script:AgentServiceName -ErrorAction SilentlyContinue
            if (-not $agentService) { throw 'The Agent service disappeared while migrating its service account.' }
            if ($agentService.Status.ToString() -ne 'Running') {
                Start-Service -Name $script:AgentServiceName -ErrorAction Stop
                Wait-StreamGuardServiceState -Name $script:AgentServiceName -State 'Running'
            }
            $steps.Add('restart-agent-service')
        }
        $steps.Add('verify-removal-and-sunshine')

        $verified = New-StreamGuardRemovalPlan -State (Get-StreamGuardRemovalLiveState) -BackupDirectory $BackupDirectory
        if (-not $verified.AlreadyClean) {
            throw ('Stream Guard removal verification failed: ' +
                "guardPresent=$($verified.GuardService.Present), " +
                "firewallPresent=$($verified.FirewallRule.Present), " +
                "agentDependencyPresent=$($verified.AgentService.DependencyPresent), " +
                "agentAccountChange=$($verified.AgentService.AccountChange), " +
                "sunshineStartType=$($verified.SunshineService.StartType), sunshineState=$($verified.SunshineService.State)")
        }
        Write-StreamGuardRemovalJournal -Directory $BackupDirectory -Status 'succeeded' -OperationId $operationId `
            -State $State -BackupPath $backupPath -Steps @($steps) | Out-Null
        return [pscustomobject]@{
            Status = 'removed'
            OperationId = $operationId
            BackupPath = $backupPath
            Steps = @($steps)
            Verified = $true
            SunshineService = $sunshine
            Message = "Legacy Stream Guard removed and '$($script:SunshineServiceName)' restored to automatic startup and Running. Prior configuration backup: $backupPath"
        }
    } catch {
        $failure = $_.Exception
        $journalError = $null
        try {
            Write-StreamGuardRemovalJournal -Directory $BackupDirectory -Status 'failed' -OperationId $operationId `
                -State $State -BackupPath $backupPath -Steps @($steps) -Error $failure.Message | Out-Null
        } catch { $journalError = $_.Exception }
        $message = "Legacy Stream Guard removal failed. Prior service and firewall configuration was backed up to '$backupPath'; fix the reported error and rerun (the removal is idempotent)."
        if ($journalError) { $message += ' The failure journal could not be written: ' + $journalError.Message + '.' }
        throw [AggregateException]::new($message, @($failure))
    }
}

if ($DefineFunctionsOnly) { return }

if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
$scriptRoot = $InstallScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptRoot)) { $scriptRoot = $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($scriptRoot) -or -not [IO.Path]::IsPathRooted($scriptRoot)) { throw 'InstallScriptRoot must identify the absolute Agent package directory.' }
. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $scriptRoot 'PrivateInstallFiles.ps1'))))
Initialize-ServiceManagerNativeType
if ($BackupDirectory -and -not [IO.Path]::IsPathRooted($BackupDirectory)) { throw 'BackupDirectory must be absolute.' }
$script:RemovalBackupDirectory = if ([string]::IsNullOrWhiteSpace($BackupDirectory)) { $null } else { [IO.Path]::GetFullPath($BackupDirectory) }
$state = Get-StreamGuardRemovalLiveState
$removalPlan = New-StreamGuardRemovalPlan -State $state -BackupDirectory $BackupDirectory
if ($Plan) { $removalPlan; return }
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run Stream Guard removal from an elevated PowerShell session.'
}
if ($removalPlan.BlockedReason) { throw $removalPlan.BlockedReason }
if ($removalPlan.AlreadyClean) {
    Write-Output "No legacy Stream Guard state or Agent account migration detected; '$($script:SunshineServiceName)' already uses automatic startup and is running."
    return
}
if (-not $PSCmdlet.ShouldProcess($script:GuardServiceName, 'Remove the legacy Stream Guard and restore the Sunshine service')) { return }
$result = Invoke-StreamGuardRemoval -State $state -Plan $removalPlan -BackupDirectory $removalPlan.Backup.Directory
Write-Output $result.Message
$result
