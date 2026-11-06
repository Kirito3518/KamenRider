$ErrorActionPreference = 'Stop'
$prepare = Join-Path $PSScriptRoot 'Prepare-SunshineStreamGate.ps1'
$guardPath = Join-Path ([IO.Path]::GetTempPath()) 'KamenRaider\Agent\Agent.exe'
$gatePlan = . $prepare -GuardExecutablePath $guardPath -Plan

if ($gatePlan.GuardService.Name -ne 'KamenRaider Stream Guard' -or $gatePlan.GuardService.Account -ne 'LocalSystem' -or
    $gatePlan.GuardService.Startup -ne 'Automatic' -or $gatePlan.GuardService.Mode -ne 'Guard') {
    throw 'Stream Guard plan must describe the automatic LocalSystem Agent.exe guard service.'
}
if ($gatePlan.Sunshine.ServiceName -ne 'SunshineService' -or $gatePlan.Sunshine.Startup -ne 'Manual' -or
    'KamenRaider Stream Guard' -notin $gatePlan.Sunshine.Dependencies) {
    throw 'Stream Guard plan must set Sunshine to manual start and depend on the Guard.'
}
if ($gatePlan.Agent.Dependency -ne 'KamenRaider Stream Guard') { throw 'Agent service must depend on the Stream Guard.' }
if ($gatePlan.Firewall.Name -ne 'KamenRaider Stream Guard - Sunshine' -or $gatePlan.Firewall.Action -ne 'Block' -or
    $gatePlan.Firewall.Direction -ne 'Inbound' -or $gatePlan.Firewall.Profile -ne 'Any' -or
    $gatePlan.Firewall.Protocol -ne 'Any' -or
    $gatePlan.Firewall.RemoteAddress -ne 'Any' -or $gatePlan.Firewall.Program -ne 'sunshine.exe resolved from SunshineService ImagePath' -or
    $gatePlan.Firewall.Enabled -ne $true -or
    $gatePlan.Firewall.OverrideBlockRules -ne $false) {
    throw 'Stream Guard plan must describe the exact enabled all-profile Sunshine block rule.'
}

. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')
if ((Get-AgentInstallMode -ServiceExists $false -InstallDirectoryExists $false -DataDirectoryExists $false -GuardServiceExists $false) -ne 'install') {
    throw 'A clean machine without Agent or Guard must use a new installation.'
}
if ((Get-AgentInstallMode -ServiceExists $true -InstallDirectoryExists $true -DataDirectoryExists $true -GuardServiceExists $true) -ne 'repair-enrollment') {
    throw 'A complete Agent and Guard installation must use the enrollment repair path.'
}
foreach ($case in @(
    @{ Service = $true; Install = $true; Data = $true; Guard = $false },
    @{ Service = $false; Install = $false; Data = $false; Guard = $true }
)) {
    $rejected = $false
    try {
        Get-AgentInstallMode -ServiceExists $case.Service -InstallDirectoryExists $case.Install `
            -DataDirectoryExists $case.Data -GuardServiceExists $case.Guard | Out-Null
    } catch { $rejected = $true }
    if (-not $rejected) { throw 'Inconsistent Agent and Guard service states must be rejected.' }
}

$events = [Collections.Generic.List[string]]::new()
$transaction = [pscustomobject]@{
    FencePrepared = $true
    SunshineConfigurationChanged = $true
    GuardServiceCreated = $true
    FirewallChanged = $true
    FirewallRuleCreated = $false
    PriorFirewallEnabled = $false
    SunshineWasRunning = $true
    SunshineStopped = $true
}
Invoke-SunshineStreamGateRollback -Transaction $transaction `
    -EnsureClosed { $events.Add('close') } `
    -RestoreSunshineConfiguration { $events.Add('restore-sunshine-config') } `
    -StopAndRemoveGuard { $events.Add('remove-guard') } `
    -RestoreFirewall { param($state) $events.Add("restore-firewall:$($state.PriorFirewallEnabled)") } `
    -RestoreSunshineRunning { $events.Add('restore-sunshine-running') }
if (($events -join ',') -ne 'close,restore-sunshine-config,remove-guard,restore-firewall:False,restore-sunshine-running') {
    throw "Rollback order or prior firewall state was not preserved: $($events -join ',')"
}
$dependencies = @(Get-SunshineStreamDependencies -Existing @('BaseService', 'KamenRaider Stream Guard', 'baseService') -Required 'KamenRaider Stream Guard')
if ($dependencies.Count -ne 2 -or $dependencies[0] -ne 'BaseService' -or $dependencies[1] -ne 'KamenRaider Stream Guard') {
    throw 'Sunshine dependency updates must preserve existing services and avoid a duplicate Guard dependency.'
}
if ((Get-SunshineExecutableFromServiceImagePath '"C:\Program Files\Sunshine\tools\sunshinesvc.exe" --service') -ne 'C:\Program Files\Sunshine\tools\sunshinesvc.exe' -or
    (Get-SunshineExecutableFromServiceImagePath 'C:\Sunshine\tools\sunshinesvc.exe') -ne 'C:\Sunshine\tools\sunshinesvc.exe') { throw 'Sunshine service executable parsing failed.' }
foreach ($badImage in @('"C:\Sunshine\sunshinesvc.exe"trailing', '"C:\Sunshine\sunshinesvc.exe', 'C:\Program Files\Sunshine\tools\sunshinesvc.exe', 'Sunshine\tools\sunshinesvc.exe')) {
    $rejected = $false
    try { Get-SunshineExecutableFromServiceImagePath $badImage | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Ambiguous Sunshine executable must be rejected: $badImage" }
}

$agentPackageTest = Join-Path $PSScriptRoot 'Test-AgentPackageInstallPlan.ps1'
$agentPackageSource = [IO.File]::ReadAllText($agentPackageTest)
if (-not $agentPackageSource.Contains('Prepare-SunshineStreamGate.ps1') -or -not $agentPackageSource.Contains('StreamGateSetup')) {
    throw 'The Agent package Plan test must cover the packaged Stream Guard setup.'
}
# All OS boundaries below are replaced with in-memory fakes. Never invoke the
# script's Apply/Rollback entry points or native SCM/firewall APIs in this suite.
function Reset-Fixture {
    $script:fixture = @{
        Events = [Collections.Generic.List[string]]::new(); Fail = ''; Failed = $false
        Agent = $false; Guard = $false; Sunshine = $true; Running = $true
        DisabledProfile = ''; PolicyState = 0; Rule = $null; Reads = 0
        StartType = 2; Dependencies = @('BaseService'); ConfigReads = 0
        VendorRules = 'TCP:Any:Allow,UDP:Any:Allow'; Unrelated = 'description,account,recovery,display-name'
        BadGuardAccount = $false; MissingAgentDependency = $false
    }
}
function Step([string]$Name) {
    $fixture.Events.Add($Name)
    if ($fixture.Fail -eq $Name -and -not $fixture.Failed) { $fixture.Failed = $true; throw "Injected $Name" }
}
function Get-Service {
    param($Name, $ErrorAction)
    if ($Name -eq 'KamenRaider Agent' -and $fixture.Agent) { return [pscustomobject]@{ Status = 'Stopped' } }
    if ($Name -eq 'KamenRaider Stream Guard' -and $fixture.Guard) { return [pscustomobject]@{ Status = 'Stopped' } }
    if ($Name -eq 'SunshineService' -and $fixture.Sunshine) { return [pscustomobject]@{ Status = $(if ($fixture.Running) { 'Running' } else { 'Stopped' }) } }
}
function Get-CimInstance {
    param($ClassName, $Filter, $ErrorAction)
    if ($fixture.Sunshine) { [pscustomobject]@{ PathName = '"C:\Program Files\Sunshine\tools\sunshinesvc.exe"' } }
}
function Test-Path { param($LiteralPath, $PathType) return $true }
function Get-NetFirewallProfile {
    param($PolicyStore, $ErrorAction)
    foreach ($name in @('Domain', 'Private', 'Public')) { [pscustomobject]@{ Name = $name; Enabled = ($name -ne $fixture.DisabledProfile).ToString() } }
}
function New-Object {
    param($ComObject)
    if ($ComObject -ne 'HNetCfg.FwPolicy2') { throw 'Unexpected COM access in mock suite.' }
    [pscustomobject]@{ CurrentProfileTypes = 2; LocalPolicyModifyState = $fixture.PolicyState }
}
function Get-StreamGateServiceConfiguration {
    param($Name)
    if ($Name -eq 'KamenRaider Stream Guard') {
        return [pscustomobject]@{ StartType = 2; Account = $(if ($fixture.BadGuardAccount) { 'Guest' } else { 'LocalSystem' }); BinaryPath = (Get-ServiceBinaryPath -Executable $guardPath -ContentRoot ([IO.Path]::GetDirectoryName($guardPath))) + ' --stream-guard' }
    }
    if ($Name -eq 'KamenRaider Agent') {
        return [pscustomobject]@{ Account = 'NT SERVICE\KamenRaider Agent'; Dependencies = $(if ($fixture.MissingAgentDependency) { @() } else { @('KamenRaider Stream Guard') }) }
    }
    $fixture.ConfigReads++
    if ($fixture.ConfigReads -eq 2) { Step 'verify-sunshine' }
    [pscustomobject]@{ StartType = $fixture.StartType; Dependencies = @($fixture.Dependencies); BinaryPath = '"C:\Program Files\Sunshine\tools\sunshinesvc.exe"' }
}
function Set-StreamGateServiceConfiguration {
    param($Name, $StartType, $Dependencies)
    if ($Name -eq 'SunshineService') {
        if ($StartType -eq 3) { Step 'sunshine-config' } else { Step 'restore-config' }
        $fixture.StartType = $StartType; $fixture.Dependencies = @($Dependencies)
    } else { Step 'guard-config' }
}
function New-FixtureRule {
    [pscustomobject]@{ Enabled = 'True'; Direction = 'Inbound'; Action = 'Block'; Profile = 'Any'; EdgeTraversalPolicy = 'Block'; OverrideBlockRules = 'False'; PolicyStoreSourceType = 'Local' }
}
function Get-NetFirewallRule {
    param($PolicyStore, $Name, $ErrorAction)
    if ($Name -ne 'KamenRaider Stream Guard - Sunshine') { throw 'Only the owned firewall rule may be inspected.' }
    $fixture.Reads++
    if ($fixture.Reads -eq 2) { Step 'verify-firewall' }
    if ($fixture.Rule) { $fixture.Rule }
}
function Get-NetFirewallApplicationFilter { process { [pscustomobject]@{ Program = 'C:\Program Files\Sunshine\sunshine.exe' } } }
function Get-NetFirewallServiceFilter { process { [pscustomobject]@{ Service = 'Any' } } }
function Get-NetFirewallPortFilter { process { [pscustomobject]@{ Protocol = 'Any'; LocalPort = 'Any'; RemotePort = 'Any' } } }
function Get-NetFirewallAddressFilter { process { [pscustomobject]@{ LocalAddress = 'Any'; RemoteAddress = 'Any' } } }
function Get-NetFirewallInterfaceFilter { process { [pscustomobject]@{ InterfaceAlias = 'Any' } } }
function Get-NetFirewallInterfaceTypeFilter { process { [pscustomobject]@{ InterfaceType = 'Any' } } }
function New-NetFirewallRule {
    param($PolicyStore, $Name, $DisplayName, $Description, $Direction, $Action, $Program, $Profile, $Protocol,
        $LocalPort, $RemotePort, $LocalAddress, $RemoteAddress, $InterfaceType, $Enabled, $EdgeTraversalPolicy, $OverrideBlockRules, $ErrorAction)
    Step 'create-firewall'; $fixture.Rule = New-FixtureRule
}
function Set-NetFirewallRule {
    param($PolicyStore, $Name, $Enabled, $ErrorAction)
    Step 'set-firewall'
    if (-not $fixture.Rule) { throw 'Attempted to modify a firewall rule not owned by this transaction.' }
    $fixture.Rule.Enabled = [string]$Enabled
}
function Remove-NetFirewallRule { param($PolicyStore, $Name, $ErrorAction) Step 'remove-firewall'; $fixture.Rule = $null }
function Stop-Service { param($Name, $ErrorAction) Step 'stop-sunshine'; $fixture.Running = $false }
function Start-Service { param($Name, $ErrorAction) Step 'start-sunshine'; $fixture.Running = $true }
function New-DisabledWindowsService { param($ServiceName, $BinaryPath, $Account, $Dependencies) Step 'create-guard'; $fixture.Guard = $true }
function Invoke-StreamGateServiceControl {
    param($Arguments)
    Step ([string]$Arguments[0])
    if ($Arguments[0] -eq 'delete') { $fixture.Guard = $false }
}

$caseCount = 0
foreach ($case in @('existing-agent', 'existing-guard', 'missing-sunshine', 'Domain', 'Private', 'Public', 'policy-disabled', 'conflicting-rule', 'gpo-rule')) {
    Reset-Fixture
    switch ($case) {
        'existing-agent' { $fixture.Agent = $true }
        'existing-guard' { $fixture.Guard = $true }
        'missing-sunshine' { $fixture.Sunshine = $false }
        'policy-disabled' { $fixture.PolicyState = 1 }
        'conflicting-rule' { $fixture.Rule = New-FixtureRule; $fixture.Rule.Action = 'Allow' }
        'gpo-rule' { $fixture.Rule = New-FixtureRule; $fixture.Rule.PolicyStoreSourceType = 'GroupPolicy' }
        default { $fixture.DisabledProfile = $case }
    }
    $rejected = $false
    try { Install-SunshineStreamGate -GuardExecutablePath $guardPath -ContentRoot ([IO.Path]::GetDirectoryName($guardPath)) | Out-Null } catch { $rejected = $true }
    if (-not $rejected -or $fixture.Events.Count -ne 0) { throw "Preflight $case must reject without mutations: $($fixture.Events -join ',')" }
    $caseCount++
}
foreach ($stage in @('create-firewall', 'verify-firewall', 'stop-sunshine', 'create-guard', 'guard-config', 'failure', 'failureflag', 'sunshine-config', 'verify-sunshine')) {
    Reset-Fixture; $fixture.Fail = $stage
    $rejected = $false
    try { Install-SunshineStreamGate -GuardExecutablePath $guardPath -ContentRoot ([IO.Path]::GetDirectoryName($guardPath)) | Out-Null } catch {
        if ($_.Exception.Message -ne "Injected $stage") { throw }
        $rejected = $true
    }
    if (-not $rejected -or -not $fixture.Failed -or $fixture.Guard -or $fixture.Rule -or -not $fixture.Running -or
        $fixture.StartType -ne 2 -or ($fixture.Dependencies -join ',') -ne 'BaseService') { throw "Failure at $stage did not restore the prior state." }
    if ($fixture.VendorRules -ne 'TCP:Any:Allow,UDP:Any:Allow' -or $fixture.Unrelated -ne 'description,account,recovery,display-name') { throw 'Unrelated Sunshine settings changed.' }
    $caseCount++
}
foreach ($priorRule in @('absent', 'True', 'False')) {
    Reset-Fixture
    if ($priorRule -ne 'absent') { $fixture.Rule = New-FixtureRule; $fixture.Rule.Enabled = $priorRule }
    $installed = Install-SunshineStreamGate -GuardExecutablePath $guardPath -ContentRoot ([IO.Path]::GetDirectoryName($guardPath))
    if (-not $fixture.Guard -or $fixture.Running -or $fixture.Rule.Enabled -ne 'True' -or $fixture.StartType -ne 3 -or
        ($fixture.Dependencies -join ',') -ne 'BaseService,KamenRaider Stream Guard') { throw 'Installation did not establish the closed policy.' }
    if ($fixture.Events.IndexOf('verify-firewall') -gt $fixture.Events.IndexOf('stop-sunshine') -or
        $fixture.Events.IndexOf('stop-sunshine') -gt $fixture.Events.IndexOf('create-guard')) { throw 'Installation order must close, stop, then create Guard.' }
    Restore-SunshineStreamGate -Transaction $installed
    if ($fixture.Guard -or -not $fixture.Running -or $fixture.StartType -ne 2 -or ($fixture.Dependencies -join ',') -ne 'BaseService') { throw 'Successful installation rollback did not restore Sunshine.' }
    if (($priorRule -eq 'absent' -and $fixture.Rule) -or ($priorRule -ne 'absent' -and $fixture.Rule.Enabled -ne $priorRule)) { throw 'Rollback must preserve the existing firewall rule and its prior enable state.' }
    $caseCount++
}
Reset-Fixture
$installed = Install-SunshineStreamGate -GuardExecutablePath $guardPath -ContentRoot ([IO.Path]::GetDirectoryName($guardPath))
$fixture.Fail = 'restore-config'
$rejected = $false
try { Restore-SunshineStreamGate -Transaction $installed } catch { $rejected = $_.Exception -is [AggregateException] }
if (-not $rejected -or -not $fixture.Guard -or $fixture.Running -or $fixture.Rule.Enabled -ne 'True') { throw 'Rollback failure must retain Guard and fence, keep Sunshine stopped, and surface the failure.' }
$caseCount++
foreach ($case in @('valid', 'guard-account', 'agent-dependency', 'sunshine-start', 'closed-policy', 'profile', 'policy')) {
    Reset-Fixture
    $fixture.StartType = 3; $fixture.Dependencies = @('BaseService', 'KamenRaider Stream Guard'); $fixture.Rule = New-FixtureRule
    switch ($case) {
        'guard-account' { $fixture.BadGuardAccount = $true }
        'agent-dependency' { $fixture.MissingAgentDependency = $true }
        'sunshine-start' { $fixture.StartType = 2 }
        'closed-policy' { $fixture.Rule.Enabled = 'False' }
        'profile' { $fixture.DisabledProfile = 'Public' }
        'policy' { $fixture.PolicyState = 1 }
    }
    $rejected = $false
    try { Test-InstalledSunshineStreamGate -GuardExecutablePath $guardPath -ContentRoot ([IO.Path]::GetDirectoryName($guardPath)) } catch { $rejected = $true }
    if ($rejected -ne ($case -ne 'valid') -or $fixture.Events.Count -ne 0) { throw "Enrollment repair validation failed: $case" }
    $caseCount++
}
Write-Output "Sunshine Stream Guard plan and $caseCount mocked setup/rollback cases passed; no services or firewall rules changed."
