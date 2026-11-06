[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory)][string]$GuardExecutablePath,
    [string]$ContentRoot = [IO.Path]::GetDirectoryName($GuardExecutablePath),
    [ValidateSet('Plan', 'Apply', 'Rollback', 'Validate')][string]$Action = 'Plan',
    [string]$InstallScriptRoot = $PSScriptRoot,
    [switch]$Plan,
    [psobject]$Transaction
)

$ErrorActionPreference = 'Stop'
$script:GuardServiceName = 'KamenRaider Stream Guard'
$script:AgentServiceName = 'KamenRaider Agent'
$script:SunshineServiceName = 'SunshineService'
$script:StreamRuleName = 'KamenRaider Stream Guard - Sunshine'

function Get-SunshineStreamGatePlan {
    param([string]$GuardPath, [string]$Root)
    [pscustomobject]@{
        GuardService = [pscustomobject]@{
            Name = $script:GuardServiceName
            Account = 'LocalSystem'
            Startup = 'Automatic'
            Mode = 'Guard'
            Binary = [IO.Path]::GetFullPath($GuardPath)
            ContentRoot = [IO.Path]::GetFullPath($Root)
        }
        Sunshine = [pscustomobject]@{
            ServiceName = $script:SunshineServiceName
            Startup = 'Manual'
            Dependencies = @($script:GuardServiceName)
            Program = 'sunshine.exe resolved from SunshineService ImagePath'
        }
        Agent = [pscustomobject]@{
            ServiceName = $script:AgentServiceName
            Dependency = $script:GuardServiceName
            Account = 'NT SERVICE\KamenRaider Agent'
        }
        Firewall = [pscustomobject]@{
            Name = $script:StreamRuleName
            DisplayName = 'KamenRaider Stream Guard - Sunshine'
            Program = 'sunshine.exe resolved from SunshineService ImagePath'
            Direction = 'Inbound'
            Action = 'Block'
            Profile = 'Any'
            Protocol = 'Any'
            LocalPort = 'Any'
            RemotePort = 'Any'
            LocalAddress = 'Any'
            RemoteAddress = 'Any'
            InterfaceType = 'Any'
            Enabled = $true
            EdgeTraversalPolicy = 'Block'
            OverrideBlockRules = $false
        }
        Rollback = 'Restore Sunshine startup/dependencies and prior rule state; remove only the transaction-owned Guard service and newly created rule.'
    }
}

function Get-SunshineStreamDependencies {
    param([string[]]$Existing, [Parameter(Mandatory)][string]$Required)
    $result = [Collections.Generic.List[string]]::new()
    foreach ($name in @($Existing)) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if (-not $result.Exists([Predicate[string]]{ param($item) [string]::Equals($item, $name, [StringComparison]::OrdinalIgnoreCase) })) {
            $result.Add($name)
        }
    }
    if (-not $result.Exists([Predicate[string]]{ param($item) [string]::Equals($item, $Required, [StringComparison]::OrdinalIgnoreCase) })) {
        $result.Add($Required)
    }
    return $result.ToArray()
}

function Invoke-SunshineStreamGateRollback {
    param(
        [Parameter(Mandatory)][psobject]$Transaction,
        [Parameter(Mandatory)][scriptblock]$EnsureClosed,
        [Parameter(Mandatory)][scriptblock]$RestoreSunshineConfiguration,
        [Parameter(Mandatory)][scriptblock]$StopGuard,
        [Parameter(Mandatory)][scriptblock]$RemoveGuard,
        [Parameter(Mandatory)][scriptblock]$RestoreFirewall,
        [Parameter(Mandatory)][scriptblock]$RestoreSunshineRunning
    )
    $failures = [Collections.Generic.List[Exception]]::new()
    if ($Transaction.FencePrepared) {
        try { & $EnsureClosed } catch { $failures.Add($_.Exception) }
    }
    if ($Transaction.GuardServiceCreated -and $failures.Count -eq 0) {
        try { & $StopGuard } catch { $failures.Add($_.Exception) }
    }
    if ($Transaction.SunshineConfigurationChanged -and $failures.Count -eq 0) {
        try { & $RestoreSunshineConfiguration } catch { $failures.Add($_.Exception) }
    }
    # Keep the verified fence and Guard service definition until Sunshine has
    # been restored and its running state verified. Stop Guard first because
    # its shutdown handler can stop Sunshine.
    if ($Transaction.SunshineStopped -and $Transaction.SunshineWasRunning -and $failures.Count -eq 0) {
        try { & $RestoreSunshineRunning } catch { $failures.Add($_.Exception) }
    }
    if ($Transaction.FirewallChanged -and $failures.Count -eq 0) {
        try { & $RestoreFirewall $Transaction } catch { $failures.Add($_.Exception) }
    }
    if ($Transaction.GuardServiceCreated -and $failures.Count -eq 0) {
        try { & $RemoveGuard } catch { $failures.Add($_.Exception) }
    }
    if ($failures.Count -gt 0) {
        if ($Transaction.FencePrepared -or $Transaction.FirewallChanged) {
            # A failing firewall restore can already have removed/disabled the
            # rule, and a failing start can already have started Sunshine.
            try { & $EnsureClosed } catch { $failures.Add($_.Exception) }
        }
        throw [AggregateException]::new('Stream Guard installation failed and one or more rollback steps also failed.', $failures)
    }
}

function Get-SunshineExecutableFromServiceImagePath {
    param([Parameter(Mandatory)][string]$ImagePath)
    $value = $ImagePath.Trim()
    if ($value.StartsWith('"')) {
        $endQuote = $value.IndexOf([string]'"', [int]1, [StringComparison]::Ordinal)
        if ($endQuote -lt 0) { throw 'SunshineService ImagePath has an unmatched quote.' }
        if ($endQuote + 1 -lt $value.Length -and -not [char]::IsWhiteSpace($value[$endQuote + 1])) {
            throw 'SunshineService ImagePath has invalid text after its quoted executable.'
        }
        $executable = $value.Substring(1, $endQuote - 1)
    } else {
        $exeEnd = $value.IndexOf('.exe', [StringComparison]::OrdinalIgnoreCase)
        if ($exeEnd -lt 0) { throw 'SunshineService ImagePath does not contain an executable path.' }
        $exeEnd += 4
        if ($exeEnd -lt $value.Length -and -not [char]::IsWhiteSpace($value[$exeEnd])) { throw 'SunshineService ImagePath has invalid trailing text.' }
        $executable = $value.Substring(0, $exeEnd)
        if ($executable -match '\s') { throw 'Unquoted SunshineService ImagePath is ambiguous because its path contains spaces.' }
    }
    if ([string]::IsNullOrWhiteSpace($executable)) { throw 'SunshineService ImagePath does not contain an executable.' }
    $executable = [Environment]::ExpandEnvironmentVariables($executable)
    if (-not [IO.Path]::IsPathRooted($executable)) { throw 'SunshineService ImagePath must contain an absolute executable path.' }
    return [IO.Path]::GetFullPath($executable)
}

function Test-SunshineStreamGateFirewallRule {
    param([Parameter(Mandatory)]$Rule, [Parameter(Mandatory)][string]$SunshineExecutable, [switch]$AllowDisabled)
    $application = @($Rule | Get-NetFirewallApplicationFilter -ErrorAction Stop)
    $service = @($Rule | Get-NetFirewallServiceFilter -ErrorAction Stop)
    $ports = @($Rule | Get-NetFirewallPortFilter -ErrorAction Stop)
    $addresses = @($Rule | Get-NetFirewallAddressFilter -ErrorAction Stop)
    $interfaces = @($Rule | Get-NetFirewallInterfaceFilter -ErrorAction Stop)
    $interfaceTypes = @($Rule | Get-NetFirewallInterfaceTypeFilter -ErrorAction Stop)
    if ($application.Count -ne 1 -or $service.Count -ne 1 -or $ports.Count -ne 1 -or $addresses.Count -ne 1 -or $interfaces.Count -ne 1 -or $interfaceTypes.Count -ne 1) { return $false }
    ($AllowDisabled -or $rule.Enabled -eq 'True') -and $rule.Direction -eq 'Inbound' -and $rule.Action -eq 'Block' -and
        [string]$rule.Profile -eq 'Any' -and [string]$rule.EdgeTraversalPolicy -eq 'Block' -and
        [string]$rule.OverrideBlockRules -eq 'False' -and
        [string]::Equals([IO.Path]::GetFullPath([string]$application[0].Program), [IO.Path]::GetFullPath($SunshineExecutable), [StringComparison]::OrdinalIgnoreCase) -and
        [string]$service[0].Service -eq 'Any' -and [string]$ports[0].Protocol -eq 'Any' -and
        (@($ports[0].LocalPort) -join ',') -eq 'Any' -and (@($ports[0].RemotePort) -join ',') -eq 'Any' -and
        (@($addresses[0].LocalAddress) -join ',') -eq 'Any' -and (@($addresses[0].RemoteAddress) -join ',') -eq 'Any' -and
        (@($interfaces[0].InterfaceAlias) -join ',') -eq 'Any' -and [string]$interfaceTypes[0].InterfaceType -eq 'Any'
}

function Wait-SunshineServiceState {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$State, [int]$TimeoutSeconds = 30)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $service = Get-Service -Name $Name -ErrorAction Stop
        if ($service.Status.ToString() -eq $State) { return }
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "Service '$Name' did not reach $State within $TimeoutSeconds seconds."
}

function Get-StreamGateServiceConfiguration {
    param([string]$Name)
    [KamenRaider.Install.NativeServiceManager]::QueryConfiguration($Name)
}
function Set-StreamGateServiceConfiguration {
    param([string]$Name, [uint32]$StartType, [string[]]$Dependencies)
    [KamenRaider.Install.NativeServiceManager]::SetStartAndDependencies($Name, $StartType, $Dependencies)
}
function Invoke-StreamGateServiceControl {
    param([string[]]$Arguments)
    & (Join-Path $env:SystemRoot 'System32\sc.exe') @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Service control failed: $($Arguments[0])" }
}
function Assert-SunshineStreamGateFirewallPolicy {
    $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)
    foreach ($name in @('Domain', 'Private', 'Public')) {
        $profile = @($profiles | Where-Object { $_.Name -eq $name })
        # LocalPolicyModifyState only describes current profiles. Require an
        # explicit effective local-rule merge permission for inactive ones too.
        if ($profile.Count -ne 1 -or [string]$profile[0].Enabled -ne 'True' -or
            [string]$profile[0].AllowLocalFirewallRules -ne 'True') {
            throw "Windows Firewall profile '$name' must be enabled and verifiably permit local firewall rules."
        }
    }
    $policy = New-Object -ComObject HNetCfg.FwPolicy2
    if ([int]$policy.CurrentProfileTypes -eq 0 -or [int]$policy.LocalPolicyModifyState -ne 0) {
        throw 'Windows Firewall policy does not permit the Stream Guard to verify and update a local rule.'
    }
}
function Test-InstalledSunshineStreamGate {
    param([string]$GuardExecutablePath, [string]$ContentRoot)
    $guard = Get-StreamGateServiceConfiguration -Name $script:GuardServiceName
    $agent = Get-StreamGateServiceConfiguration -Name $script:AgentServiceName
    $sunshine = Get-StreamGateServiceConfiguration -Name $script:SunshineServiceName
    $expectedGuard = (Get-ServiceBinaryPath -Executable $GuardExecutablePath -ContentRoot $ContentRoot) + ' --stream-guard'
    if ($guard.Account -ne 'LocalSystem' -or $guard.StartType -ne 2 -or $guard.BinaryPath -ne $expectedGuard -or
        $agent.Account -ne 'NT SERVICE\KamenRaider Agent' -or $script:GuardServiceName -notin $agent.Dependencies -or
        $sunshine.StartType -ne 3 -or $script:GuardServiceName -notin $sunshine.Dependencies) {
        throw 'Existing Agent, Guard or Sunshine service configuration is inconsistent; refusing enrollment repair.'
    }
    $serviceImage = Get-SunshineExecutableFromServiceImagePath -ImagePath $sunshine.BinaryPath
    if ([IO.Path]::GetFileName($serviceImage) -ine 'sunshinesvc.exe' -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($serviceImage)) -ine 'tools') {
        throw 'SunshineService must point to Sunshine\tools\sunshinesvc.exe.'
    }
    $sunshineExecutable = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($serviceImage))) 'sunshine.exe'
    Assert-SunshineStreamGateFirewallPolicy
    $rule = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name $script:StreamRuleName -ErrorAction Stop)
    if ($rule.Count -ne 1 -or [string]$rule[0].PolicyStoreSourceType -ne 'Local' -or
        -not (Test-SunshineStreamGateFirewallRule -Rule $rule[0] -SunshineExecutable $sunshineExecutable)) {
        throw 'An exact enabled local Sunshine block rule is required before enrollment repair.'
    }
}
function New-SunshineStreamGateFirewallRule {
    param([string]$SunshineExecutable)
    New-NetFirewallRule -PolicyStore PersistentStore -Name $script:StreamRuleName `
        -DisplayName 'KamenRaider Stream Guard - Sunshine' -Description 'Managed by KamenRaider Stream Guard.' `
        -Direction Inbound -Action Block -Program $SunshineExecutable -Profile Any -Protocol Any `
        -LocalPort Any -RemotePort Any -LocalAddress Any -RemoteAddress Any -InterfaceType Any `
        -Enabled True -EdgeTraversalPolicy Block -OverrideBlockRules $false -ErrorAction Stop | Out-Null
}
function Close-SunshineStreamGateTransaction {
    param([psobject]$Transaction)
    $failures = [Collections.Generic.List[Exception]]::new()
    try {
        $rule = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name $script:StreamRuleName -ErrorAction SilentlyContinue)
        if ($rule.Count -eq 0 -and $Transaction.FirewallRuleCreated) {
            New-SunshineStreamGateFirewallRule -SunshineExecutable $Transaction.SunshineExecutablePath
        } elseif ($rule.Count -eq 1 -and [string]$rule[0].PolicyStoreSourceType -eq 'Local' -and
            (Test-SunshineStreamGateFirewallRule -Rule $rule[0] -SunshineExecutable $Transaction.SunshineExecutablePath -AllowDisabled)) {
            Set-NetFirewallRule -PolicyStore PersistentStore -Name $script:StreamRuleName -Enabled True -ErrorAction Stop | Out-Null
        } else { throw 'Cannot safely recover the transaction-owned Sunshine fence.' }
        Assert-SunshineStreamGateFirewallPolicy
        $verified = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name $script:StreamRuleName -ErrorAction Stop)
        if ($verified.Count -ne 1 -or -not (Test-SunshineStreamGateFirewallRule -Rule $verified[0] -SunshineExecutable $Transaction.SunshineExecutablePath)) {
            throw 'The recovered Sunshine fence could not be verified.'
        }
    } catch { $failures.Add($_.Exception) }
    # Attempt service containment even when firewall recovery/verification fails.
    try {
        $sunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop
        if ($sunshine.Status.ToString() -ne 'Stopped') {
            Stop-Service -Name $script:SunshineServiceName -ErrorAction Stop
        }
        Wait-SunshineServiceState -Name $script:SunshineServiceName -State 'Stopped'
    } catch { $failures.Add($_.Exception) }
    if ($failures.Count -gt 0) { throw [AggregateException]::new('Could not fully re-close and stop Sunshine during rollback.', $failures) }
}
function Restore-SunshineStreamGate {
    param([psobject]$Transaction)
    Invoke-SunshineStreamGateRollback -Transaction $Transaction `
        -EnsureClosed {
            Close-SunshineStreamGateTransaction -Transaction $Transaction
        } `
        -RestoreSunshineConfiguration {
            Set-StreamGateServiceConfiguration -Name $script:SunshineServiceName -StartType $Transaction.PriorSunshineStartType -Dependencies $Transaction.PriorSunshineDependencies
        } `
        -StopGuard {
            $guard = Get-Service -Name $script:GuardServiceName -ErrorAction SilentlyContinue
            if ($guard -and $guard.Status.ToString() -ne 'Stopped') {
                Stop-Service -Name $script:GuardServiceName -ErrorAction Stop
                Wait-SunshineServiceState -Name $script:GuardServiceName -State 'Stopped'
            }
        } `
        -RemoveGuard {
            if (Get-Service -Name $script:GuardServiceName -ErrorAction SilentlyContinue) {
                Invoke-StreamGateServiceControl -Arguments @('delete', $script:GuardServiceName)
            }
        } `
        -RestoreFirewall {
            param($state)
            if ($state.FirewallRuleCreated) {
                Remove-NetFirewallRule -PolicyStore PersistentStore -Name $script:StreamRuleName -ErrorAction Stop
            } else {
                Set-NetFirewallRule -PolicyStore PersistentStore -Name $script:StreamRuleName -Enabled ([bool]$state.PriorFirewallEnabled) -ErrorAction Stop | Out-Null
            }
        } `
        -RestoreSunshineRunning {
            $sunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop
            if ($sunshine.Status.ToString() -eq 'Stopped') {
                Start-Service -Name $script:SunshineServiceName -ErrorAction Stop
            }
            Wait-SunshineServiceState -Name $script:SunshineServiceName -State 'Running'
        }

}
function Install-SunshineStreamGate {
    param([string]$GuardExecutablePath, [string]$ContentRoot)
if (Get-Service -Name $script:GuardServiceName -ErrorAction SilentlyContinue) { throw 'Stream Guard service already exists; refusing to replace a service not created by this transaction.' }
if (Get-Service -Name $script:AgentServiceName -ErrorAction SilentlyContinue) { throw 'Agent service already exists; the Stream Guard setup path only supports a clean installation.' }

$sunshineInfo = @(Get-CimInstance -ClassName Win32_Service -Filter "Name='SunshineService'" -ErrorAction Stop)
if ($sunshineInfo.Count -ne 1) { throw "Windows service '$($script:SunshineServiceName)' is missing or ambiguous." }
$sunshineServiceImage = Get-SunshineExecutableFromServiceImagePath -ImagePath ([string]$sunshineInfo[0].PathName)
if ([IO.Path]::GetFileName($sunshineServiceImage) -ine 'sunshinesvc.exe' -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($sunshineServiceImage)) -ine 'tools') {
    throw 'SunshineService must point to Sunshine\tools\sunshinesvc.exe.'
}
$sunshineTools = [IO.Path]::GetDirectoryName($sunshineServiceImage)
$sunshineRoot = [IO.Path]::GetDirectoryName($sunshineTools)
$sunshineExecutable = [IO.Path]::GetFullPath((Join-Path $sunshineRoot 'sunshine.exe'))
if (-not (Test-Path -LiteralPath $sunshineServiceImage -PathType Leaf) -or -not (Test-Path -LiteralPath $sunshineExecutable -PathType Leaf)) {
    throw 'Sunshine service wrapper and sunshine.exe must both exist.'
}

Assert-SunshineStreamGateFirewallPolicy

$priorSunshine = (Get-StreamGateServiceConfiguration -Name $script:SunshineServiceName)
$currentSunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop
$transaction = [pscustomobject]@{
    SunshineServiceName = $script:SunshineServiceName
    PriorSunshineStartType = [uint32]$priorSunshine.StartType
    PriorSunshineDependencies = [string[]]$priorSunshine.Dependencies
    SunshineConfigurationChanged = $false
    SunshineStopped = $false
    SunshineWasRunning = $currentSunshine.Status.ToString() -eq 'Running'
    GuardServiceCreated = $false
    FirewallChanged = $false
    FirewallRuleCreated = $false
    PriorFirewallEnabled = $null
    FencePrepared = $false
    SunshineExecutablePath = $sunshineExecutable
}

try {
    $existingRule = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name $script:StreamRuleName -ErrorAction SilentlyContinue)
    if ($existingRule.Count -gt 1) { throw 'Multiple active rules use the managed Sunshine fence name.' }
    if ($existingRule.Count -eq 1) {
        if ([string]$existingRule[0].PolicyStoreSourceType -ne 'Local') { throw 'The managed Sunshine fence name is owned by Group Policy; refusing to modify it.' }
        if (-not (Test-SunshineStreamGateFirewallRule -Rule $existingRule[0] -SunshineExecutable $sunshineExecutable -AllowDisabled)) {
            throw 'The existing managed Sunshine firewall rule has a conflicting shape; refusing to change it.'
        }
        $transaction.PriorFirewallEnabled = ([string]$existingRule[0].Enabled -eq 'True')
        if (-not $transaction.PriorFirewallEnabled) {
            $transaction.FirewallChanged = $true
            Set-NetFirewallRule -PolicyStore PersistentStore -Name $script:StreamRuleName -Enabled True -ErrorAction Stop | Out-Null
        }
    } else {
        New-SunshineStreamGateFirewallRule -SunshineExecutable $sunshineExecutable
        $transaction.FirewallRuleCreated = $true
        $transaction.FirewallChanged = $true
    }
    $verifiedRule = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name $script:StreamRuleName -ErrorAction Stop)
    if ($verifiedRule.Count -ne 1 -or -not (Test-SunshineStreamGateFirewallRule -Rule $verifiedRule[0] -SunshineExecutable $sunshineExecutable)) {
        throw 'The managed Sunshine firewall block rule could not be verified after publication.'
    }
    $transaction.FencePrepared = $true

    if ($currentSunshine.Status.ToString() -ne 'Stopped') {
        $transaction.SunshineStopped = $true
        Stop-Service -Name $script:SunshineServiceName -ErrorAction Stop
        Wait-SunshineServiceState -Name $script:SunshineServiceName -State 'Stopped'
    }

    $guardBinaryPath = (Get-ServiceBinaryPath -Executable $GuardExecutablePath -ContentRoot $ContentRoot) + ' --stream-guard'
    New-DisabledWindowsService -ServiceName $script:GuardServiceName -BinaryPath $guardBinaryPath -Account 'LocalSystem' -Dependencies @()
    $transaction.GuardServiceCreated = $true
    Set-StreamGateServiceConfiguration -Name $script:GuardServiceName -StartType 2 -Dependencies @()
    $recoveryBinaryPath = (Get-ServiceBinaryPath -Executable $GuardExecutablePath -ContentRoot $ContentRoot) + ' --stream-guard-recover'
    Invoke-StreamGateServiceControl -Arguments @('failure', $script:GuardServiceName, 'reset=', '86400', 'actions=', 'run/0/restart/5000/restart/15000', 'command=', $recoveryBinaryPath)
    Invoke-StreamGateServiceControl -Arguments @('failureflag', $script:GuardServiceName, '1')

    $sunshineDependencies = Get-SunshineStreamDependencies -Existing ([string[]]$priorSunshine.Dependencies) -Required $script:GuardServiceName
    $transaction.SunshineConfigurationChanged = $true
    Set-StreamGateServiceConfiguration -Name $script:SunshineServiceName -StartType 3 -Dependencies $sunshineDependencies
    $verifiedSunshine = (Get-StreamGateServiceConfiguration -Name $script:SunshineServiceName)
    if ([uint32]$verifiedSunshine.StartType -ne 3 -or $script:GuardServiceName -notin [string[]]$verifiedSunshine.Dependencies) {
        throw 'Sunshine startup or Guard dependency could not be verified.'
    }
    return $transaction
} catch {
    $original = $_
    try {
        Restore-SunshineStreamGate -Transaction $transaction
    } catch {
        throw [AggregateException]::new('Stream Guard setup failed and rollback did not fully restore prior service/firewall state.', @($original.Exception, $_.Exception))
    }
    throw $original
}

}

if (-not [IO.Path]::IsPathRooted($GuardExecutablePath) -or -not [IO.Path]::IsPathRooted($ContentRoot)) {
    throw 'Guard executable and content root must be absolute paths.'
}
$GuardExecutablePath = [IO.Path]::GetFullPath($GuardExecutablePath)
$ContentRoot = [IO.Path]::GetFullPath($ContentRoot)
if ($Plan -or $Action -eq 'Plan') {
    Get-SunshineStreamGatePlan -GuardPath $GuardExecutablePath -Root $ContentRoot
    return
}
if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run Stream Guard installation from an elevated PowerShell session.'
}
if (-not $InstallScriptRoot) { throw 'InstallScriptRoot is required.' }
. (Join-Path $InstallScriptRoot 'PrivateInstallFiles.ps1')
Initialize-ServiceManagerNativeType
if ($Action -eq 'Rollback') {
    if (-not $Transaction) { throw 'Rollback requires the in-memory Stream Guard transaction record.' }
    if ($PSCmdlet.ShouldProcess($script:GuardServiceName, 'Restore transaction-owned Stream Guard installation changes')) {
        Restore-SunshineStreamGate -Transaction $Transaction
    }
    return
}
Assert-TrustedPublishTree -Path $ContentRoot
Assert-TrustedInstallFile -Path $GuardExecutablePath
if ($Action -eq 'Validate') {
    Test-InstalledSunshineStreamGate -GuardExecutablePath $GuardExecutablePath -ContentRoot $ContentRoot
    return
}
if ($PSCmdlet.ShouldProcess($script:GuardServiceName, 'Install closed Sunshine policy and Stream Guard service')) {
    Install-SunshineStreamGate -GuardExecutablePath $GuardExecutablePath -ContentRoot $ContentRoot
}
