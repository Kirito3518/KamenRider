$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:fixture = @{
    DisabledProfile = ''
    FirewallServiceStatus = 'Running'
    Profiles = @('Domain', 'Private', 'Public')
    GuardServiceStatus = 'Running'
    ProfileQueryCount = 0
    LastPolicyStore = ''
    LastAll = $false
    ServiceMutations = 0
}

function Get-Service {
    param([string]$Name, [string]$ErrorAction)

    switch ($Name) {
        'KamenRaider Agent' { return [pscustomobject]@{ Status = 'Running' } }
        'KamenRaider Stream Guard' { return [pscustomobject]@{ Status = $script:fixture.GuardServiceStatus } }
        'SunshineService' { return [pscustomobject]@{ Status = 'Stopped' } }
        'MpsSvc' { return [pscustomobject]@{ Status = $script:fixture.FirewallServiceStatus } }
        default { throw "Unexpected service query: $Name" }
    }
}

function Get-NetFirewallProfile {
    param([string]$PolicyStore, [switch]$All, [string]$ErrorAction)

    $script:fixture.ProfileQueryCount++
    $script:fixture.LastPolicyStore = $PolicyStore
    $script:fixture.LastAll = [bool]$All
    foreach ($name in $script:fixture.Profiles) {
        [pscustomobject]@{
            Name = $name
            Enabled = ($name -ne $script:fixture.DisabledProfile)
        }
    }
}

function Get-NetFirewallRule {
    param([string]$Name, [string]$PolicyStore, [string]$ErrorAction)
    [pscustomobject]@{
        Name = $Name
        Enabled = 'True'
        Action = 'Block'
        Direction = 'Inbound'
        Profile = 'Any'
        EdgeTraversalPolicy = 'Block'
        OverrideBlockRules = $false
    }
}

function Get-NetFirewallApplicationFilter { process { [pscustomobject]@{ Program = 'C:\Program Files\Sunshine\sunshine.exe' } } }
function Get-NetFirewallServiceFilter { process { [pscustomobject]@{ Service = 'Any' } } }
function Get-NetFirewallPortFilter { process { [pscustomobject]@{ Protocol = 'Any'; LocalPort = 'Any'; RemotePort = 'Any' } } }
function Get-NetFirewallAddressFilter { process { [pscustomobject]@{ LocalAddress = 'Any'; RemoteAddress = 'Any' } } }
function Get-NetFirewallInterfaceTypeFilter { process { [pscustomobject]@{ InterfaceType = 'Any' } } }
function Start-Service { $script:fixture.ServiceMutations++; throw 'Plan mode attempted to start a service.' }
function Stop-Service { $script:fixture.ServiceMutations++; throw 'Plan mode attempted to stop a service.' }

$probe = Join-Path $PSScriptRoot 'Test-SunshineLocalApi.ps1'
. $probe -Plan:$true | Out-Null
$planResult = [pscustomobject]$result
if ($planResult.Mode -ne 'Plan' -or $planResult.Status -ne 'PlanOnly' -or $planResult.Preflight -ne 'Passed' -or
    -not $planResult.WindowsFirewallBefore -or $planResult.ApiStatus -ne 'NotAttempted') {
    throw "Expected a read-only Plan with a valid Windows Firewall preflight; got $($planResult | ConvertTo-Json -Compress)."
}
if ($script:fixture.ServiceMutations -ne 0) { throw 'Plan mode mutated a service.' }
if ($script:fixture.LastPolicyStore -ne 'ActiveStore' -or -not $script:fixture.LastAll) {
    throw 'Firewall preflight must query every effective ActiveStore profile.'
}

# The optional UI mode must remain a read-only plan when combined with -Plan.
$result.Status = 'NotStarted'
$result.Preflight = 'NotRun'
. $probe -Plan:$true -OpenSetupUi:$true | Out-Null
$setupPlanResult = [pscustomobject]$result
if ($setupPlanResult.Mode -ne 'Plan' -or $setupPlanResult.Status -ne 'PlanOnly' -or
    $setupPlanResult.SetupWindowStatus -ne 'NotStarted' -or $script:fixture.ServiceMutations -ne 0) {
    throw "Setup UI plan must not launch a task or mutate a service; got $($setupPlanResult | ConvertTo-Json -Compress)."
}

$unreachableJson = '[{"machineId":"machine-1","approvalStatus":"pending","readiness":"unreachable","canRent":false}]'
$parsedUnreachable = ConvertFrom-ControllerMachineJson -MachineId 'machine-1' -Json $unreachableJson
if ($parsedUnreachable.ApprovalStatus -ne 'pending' -or $parsedUnreachable.Readiness -ne 'unreachable' -or
    $parsedUnreachable.CanRent -isnot [bool] -or $parsedUnreachable.CanRent -or $parsedUnreachable.AllocationHeldPresent) {
    throw 'Controller JSON parser must preserve unreachable readiness and strict boolean types.'
}

$typedAllocationJson = '[{"machineId":"machine-1","approvalStatus":"pending","readiness":"notReady","canRent":false,"allocationHeld":false}]'
$parsedAllocation = ConvertFrom-ControllerMachineJson -MachineId 'machine-1' -Json $typedAllocationJson
if (-not $parsedAllocation.AllocationHeldPresent -or $parsedAllocation.AllocationHeld -isnot [bool] -or $parsedAllocation.AllocationHeld) {
    throw 'Controller JSON parser must preserve a typed allocationHeld=false value.'
}

foreach ($invalidJson in @(
    '{"machineId":"machine-1","approvalStatus":"pending","readiness":"unreachable","canRent":false}',
    '[{"MachineId":"machine-1","approvalStatus":"pending","readiness":"unreachable","canRent":false}]',
    '[{"machineId":"machine-1","approvalStatus":"pending","readiness":"unreachable","canRent":"false"}]',
    '[{"machineId":"machine-1","approvalStatus":"pending","readiness":"unreachable","canRent":false,"allocationHeld":"false"}]')) {
    $rejected = $false
    try { ConvertFrom-ControllerMachineJson -MachineId 'machine-1' -Json $invalidJson | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Controller JSON parser accepted an invalid shape, casing, or boolean type.' }
}

foreach ($readiness in @('notReady', 'unreachable')) {
    $closedMachine = [pscustomobject]@{
        ApprovalStatus = 'pending'
        Readiness = $readiness
        CanRent = $false
        AllocationHeldPresent = $false
        AllocationHeld = $null
    }
    if (-not (Test-ControllerMachineClosedForDevProbe -MachineState $closedMachine)) {
        throw "Pending, non-rentable machine in $readiness must be accepted by the dev-probe gate."
    }
}

$safeAllocation = [pscustomobject]@{
    ApprovalStatus = 'pending'
    Readiness = 'unreachable'
    CanRent = $false
    AllocationHeldPresent = $true
    AllocationHeld = $false
}
if (-not (Test-ControllerMachineClosedForDevProbe -MachineState $safeAllocation)) {
    throw 'allocationHeld=false must be accepted by the dev-probe gate.'
}

foreach ($unsafeMachine in @(
    [pscustomobject]@{ ApprovalStatus = 'approved'; Readiness = 'unreachable'; CanRent = $false; AllocationHeldPresent = $false; AllocationHeld = $null },
    [pscustomobject]@{ ApprovalStatus = 'pending'; Readiness = 'ready'; CanRent = $false; AllocationHeldPresent = $false; AllocationHeld = $null },
    [pscustomobject]@{ ApprovalStatus = 'pending'; Readiness = 'unreachable'; CanRent = $true; AllocationHeldPresent = $false; AllocationHeld = $null },
    [pscustomobject]@{ ApprovalStatus = 'pending'; Readiness = 'unreachable'; CanRent = $false; AllocationHeldPresent = $true; AllocationHeld = $true })) {
    if (Test-ControllerMachineClosedForDevProbe -MachineState $unsafeMachine) {
        throw 'The dev-probe gate accepted an approved, ready, rentable, or allocation-held machine.'
    }
}

$result.Status = 'NotStarted'
$result.FailureCategory = $null
$result.FailureDetail = $null
if (-not (Set-SunshinePortWaitFailure -SunshineState 'Stopped') -or
    $result.Status -ne 'SunshineNotRunningDuringPortWait' -or
    $result.FailureCategory -ne 'SunshineNotRunningDuringPortWait' -or
    $result.SunshineStateDuringPortWait -ne 'Stopped' -or
    $result.GuardDuringPortWait -ne 'Running' -or
    $result.FailureDetail -notmatch 'Guard enforcement is possible') {
    throw 'A Sunshine stop while Guard remains running must produce a bounded possible-enforcement diagnostic.'
}

foreach ($name in @('Domain', 'Private', 'Public')) {
    $script:fixture.DisabledProfile = $name
    if (Test-WindowsFirewallReady) { throw "Disabled $name profile must fail the firewall preflight." }
}

$script:fixture.DisabledProfile = ''
$script:fixture.FirewallServiceStatus = 'Stopped'
if (Test-WindowsFirewallReady) { throw 'A stopped Windows Defender Firewall service must fail the firewall preflight.' }

$script:fixture.FirewallServiceStatus = 'Running'
$script:fixture.Profiles = @('Domain', 'Private')
if (Test-WindowsFirewallReady) { throw 'A missing firewall profile must fail the firewall preflight.' }

Write-Output 'Sunshine local API preflight mock checks passed.'
