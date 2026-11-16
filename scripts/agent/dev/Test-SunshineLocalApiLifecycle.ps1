$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$wrapperPath = Join-Path $PSScriptRoot 'Test-SunshineLocalApi.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('SunshineLocalApiLifecycle-' + [Guid]::NewGuid().ToString('N'))
$stageRoot = Join-Path $tempRoot 'stages'
$testWrapperPath = Join-Path $tempRoot 'Test-SunshineLocalApi.Mocked.ps1'
$stageRootEnvironmentPath = 'Env:\KAMENRAIDER_SUNSHINE_LIFECYCLE_STAGE_ROOT'
$originalStageRootEnvironmentVariable = Get-Item -LiteralPath $stageRootEnvironmentPath -ErrorAction SilentlyContinue
$hadOriginalStageRootEnvironmentVariable = $null -ne $originalStageRootEnvironmentVariable
$originalStageRootEnvironmentValue = if ($hadOriginalStageRootEnvironmentVariable) {
    [string]$originalStageRootEnvironmentVariable.Value
}
else {
    $null
}

try {
    [IO.Directory]::CreateDirectory($stageRoot) | Out-Null

    $source = [IO.File]::ReadAllText($wrapperPath)
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw 'The Sunshine wrapper did not parse.' }

    # Keep the wrapper's complete orchestration and replace only operations that
    # depend on Windows identity, protected paths, local networking, or elevation.
    $mockFunctions = @{
        'Test-IsAdministrator' = @'
function Test-IsAdministrator { return $true }
'@
        'Test-SunshinePortClosed' = @'
function Test-SunshinePortClosed { return $true }
'@
        'Get-LocalMachineId' = @'
function Get-LocalMachineId { return ('a' * 32) }
'@
        'Get-ControllerMachineState' = @'
function Get-ControllerMachineState {
    return [pscustomobject]@{
        MachineId = ('a' * 32)
        ApprovalStatus = 'pending'
        Readiness = 'unreachable'
        CanRent = $false
        AllocationHeldPresent = $true
        AllocationHeld = $false
    }
}
'@
        'New-ProbeStage' = @'
function New-ProbeStage {
    $path = Join-Path $env:KAMENRAIDER_SUNSHINE_LIFECYCLE_STAGE_ROOT ([Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}
'@
        'Remove-ProbeStage' = @'
function Remove-ProbeStage {
    param([Parameter(Mandatory)][string]$Path)
    $root = [IO.Path]::GetFullPath($env:KAMENRAIDER_SUNSHINE_LIFECYCLE_STAGE_ROOT).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Mock staging cleanup path escaped its temporary root.'
    }
    [IO.File]::AppendAllText((Join-Path $env:KAMENRAIDER_SUNSHINE_LIFECYCLE_STAGE_ROOT 'stage-removal-request.txt'), $fullPath + [Environment]::NewLine)
}
'@
    }

    $edits = @()
    foreach ($functionAst in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
        if ($mockFunctions.ContainsKey($functionAst.Name)) {
            $edits += [pscustomobject]@{
                Start = $functionAst.Extent.StartOffset
                Length = $functionAst.Extent.EndOffset - $functionAst.Extent.StartOffset
                Text = $mockFunctions[$functionAst.Name]
            }
        }
    }
    if ($edits.Count -ne $mockFunctions.Count) { throw 'A required wrapper dependency was not found for mocking.' }

    foreach ($edit in ($edits | Sort-Object Start -Descending)) {
        $source = $source.Remove($edit.Start, $edit.Length).Insert($edit.Start, $edit.Text)
    }

    # The mock copy lives in temp, so point the helper-project lookup back at the
    # real script directory. The build command itself is also mocked below.
    $scriptRootLiteral = "'" + $PSScriptRoot.Replace("'", "''") + "'"
    $source = $source.Replace('$PSScriptRoot', $scriptRootLiteral)
    [IO.File]::WriteAllText($testWrapperPath, $source, [Text.UTF8Encoding]::new($false))
}
catch {
    throw
}

$mockModule = New-Module -Name KamenRaiderSunshineLifecycleMocks -ArgumentList $stageRoot -ScriptBlock {
param([string]$InitialStageRoot)
$script:stageRoot = $InitialStageRoot
$script:fixture = @{}

function Reset-Fixture {
    param([Parameter(Mandatory)][string]$Scenario, [Parameter(Mandatory)][string]$StageRoot)

    $script:stageRoot = $StageRoot
    $env:KAMENRAIDER_SUNSHINE_LIFECYCLE_STAGE_ROOT = $StageRoot
    [IO.Directory]::CreateDirectory($StageRoot) | Out-Null
    $script:fixture = @{
        Scenario = $Scenario
        AgentStatus = 'Running'
        GuardStatus = 'Running'
        SunshineStatus = 'Stopped'
        FirewallServiceStatus = 'Running'
        TaskState = 'Ready'
        TaskAction = $null
        UnregisterCount = 0
        StopTaskCount = 0
        StartServiceCount = 0
        StopServiceCount = 0
    }
}

function Get-MockFixture { return [pscustomobject]$script:fixture }

function Get-Service {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    switch ($Name) {
        'KamenRaider Agent' { return [pscustomobject]@{ Status = $script:fixture.AgentStatus } }
        'KamenRaider Stream Guard' { return [pscustomobject]@{ Status = $script:fixture.GuardStatus } }
        'SunshineService' { return [pscustomobject]@{ Status = $script:fixture.SunshineStatus } }
        'MpsSvc' { return [pscustomobject]@{ Status = $script:fixture.FirewallServiceStatus } }
        default { throw "Unexpected service query: $Name" }
    }
}

function Stop-Service {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    if ($Name -ne 'KamenRaider Agent') { throw "Unexpected service stop: $Name" }
    $script:fixture.StopServiceCount++
    $script:fixture.AgentStatus = 'Stopped'
}

function Start-Service {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    if ($Name -ne 'KamenRaider Agent') { throw "Unexpected service start: $Name" }
    $script:fixture.StartServiceCount++
    if ($script:fixture.Scenario -eq 'AgentRestartFails') { throw 'Mock Agent restart failure.' }
    $script:fixture.AgentStatus = 'Running'
}

function Get-NetFirewallProfile {
    [CmdletBinding()]
    param([string]$PolicyStore, [switch]$All)

    foreach ($name in @('Domain', 'Private', 'Public')) {
        [pscustomobject]@{ Name = $name; Enabled = $true }
    }
}

function Get-NetFirewallRule {
    [CmdletBinding()]
    param([string]$Name, [string]$PolicyStore)

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

function Get-Command {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$Name)

    if ($Name -eq 'dotnet') { return [pscustomobject]@{ Source = 'Test-SunshineMockDotnet' } }
    return Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
}

function Test-SunshineMockDotnet {
    $arguments = @($args)
    $outputIndex = [Array]::IndexOf($arguments, '--output')
    if ($outputIndex -lt 0 -or $outputIndex + 1 -ge $arguments.Count) { throw 'Mock dotnet publish did not receive --output.' }
    $outputPath = [string]$arguments[$outputIndex + 1]
    [IO.File]::WriteAllBytes((Join-Path $outputPath 'StreamGuardFencedApiProbe.exe'), [byte[]]@())
    $global:LASTEXITCODE = 0
}

function New-ScheduledTaskAction {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Execute)
    return [pscustomobject]@{ Execute = $Execute }
}

function New-ScheduledTaskPrincipal {
    [CmdletBinding()]
    param([string]$UserId, [string]$LogonType, [string]$RunLevel)
    return [pscustomobject]@{ UserId = $UserId; LogonType = $LogonType; RunLevel = $RunLevel }
}

function New-ScheduledTaskSettingsSet {
    [CmdletBinding()]
    param([TimeSpan]$ExecutionTimeLimit, [string]$MultipleInstances)
    return [pscustomobject]@{ ExecutionTimeLimit = $ExecutionTimeLimit; MultipleInstances = $MultipleInstances }
}

function Register-ScheduledTask {
    [CmdletBinding()]
    param([string]$TaskName, [object]$Action, [object]$Principal, [object]$Settings, [string]$Description)
    $script:fixture.TaskAction = $Action
    $script:fixture.TaskState = 'Ready'
}

function Start-ScheduledTask {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$TaskName)

    if ($script:fixture.Scenario -eq 'TaskStartFails') { throw 'Mock scheduled task start failure.' }
    if ($script:fixture.Scenario -eq 'TaskCannotBeStopped') {
        $script:fixture.TaskState = 'Running'
        throw 'Mock task started but the command reported failure.'
    }

    $resultPath = Join-Path (Split-Path -Parent $script:fixture.TaskAction.Execute) 'result.json'
    [IO.File]::WriteAllText($resultPath, '{"Status":"RequestCompleted","HttpStatus":200,"TlsValidated":true}')
    $script:fixture.TaskState = 'Ready'
}

function Get-ScheduledTask {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$TaskName)
    return [pscustomobject]@{ State = $script:fixture.TaskState }
}

function Stop-ScheduledTask {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$TaskName)

    $script:fixture.StopTaskCount++
    if ($script:fixture.Scenario -eq 'TaskCannotBeStopped') { throw 'Mock task stop failure.' }
    $script:fixture.TaskState = 'Ready'
}

function Unregister-ScheduledTask {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$TaskName)
    $script:fixture.UnregisterCount++
}

function Get-ScheduledTaskInfo {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$TaskName)
    return [pscustomobject]@{ LastTaskResult = 0 }
}

Export-ModuleMember -Function @(
    'Reset-Fixture', 'Get-MockFixture',
    'Get-Service', 'Stop-Service', 'Start-Service',
    'Get-NetFirewallProfile', 'Get-NetFirewallRule', 'Get-NetFirewallApplicationFilter',
    'Get-NetFirewallServiceFilter', 'Get-NetFirewallPortFilter', 'Get-NetFirewallAddressFilter',
    'Get-NetFirewallInterfaceTypeFilter', 'Get-Command', 'Test-SunshineMockDotnet',
    'New-ScheduledTaskAction', 'New-ScheduledTaskPrincipal', 'New-ScheduledTaskSettingsSet',
    'Register-ScheduledTask', 'Start-ScheduledTask', 'Get-ScheduledTask', 'Stop-ScheduledTask',
    'Unregister-ScheduledTask', 'Get-ScheduledTaskInfo'
)
}
Import-Module $mockModule -Force

function Assert-Condition {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-MockedWrapper {
    $output = @(& $testWrapperPath)
    if ($output.Count -ne 1) { throw "Expected exactly one wrapper result, got $($output.Count)." }
    return [pscustomobject]$output[0]
}

try {
    $stageRoot = Join-Path $tempRoot ('stages-task-start-failure-' + [Guid]::NewGuid().ToString('N'))
    Reset-Fixture -Scenario 'TaskStartFails' -StageRoot $stageRoot
    $taskStartFailure = Invoke-MockedWrapper
    $fixture = Get-MockFixture
    Assert-Condition ($taskStartFailure.Status -eq 'ProbeFailed') 'Task-start failure must be reported.'
    Assert-Condition ($fixture.StopServiceCount -eq 1) 'Task-start failure scenario must stop the Agent once.'
    Assert-Condition ($fixture.AgentStatus -eq 'Running' -and $taskStartFailure.AgentRestartStatus -eq 'Running') 'Agent must restart after task start fails.'
    Assert-Condition ($taskStartFailure.ProbeTaskStatus -eq 'Removed' -and $fixture.UnregisterCount -eq 1) 'Registered task must be removed after start failure.'
    Assert-Condition ($taskStartFailure.CleanupStatus -eq 'StagingRemoved' -and (Test-Path -LiteralPath (Join-Path $stageRoot 'stage-removal-request.txt'))) 'Stage cleanup must be requested after task start failure.'

    $stageRoot = Join-Path $tempRoot ('stages-task-stop-failure-' + [Guid]::NewGuid().ToString('N'))
    Reset-Fixture -Scenario 'TaskCannotBeStopped' -StageRoot $stageRoot
    $taskStopFailure = Invoke-MockedWrapper
    $fixture = Get-MockFixture
    Assert-Condition ($taskStopFailure.Status -eq 'ProbeTaskStopUnconfirmed') 'A task that cannot be stopped must be reported as unconfirmed.'
    Assert-Condition ($taskStopFailure.AgentRestartStatus -eq 'SkippedTaskMayBeRunning' -and $fixture.StartServiceCount -eq 0) 'Agent must not start while the probe task may still be running.'
    Assert-Condition ($taskStopFailure.CleanupStatus -eq 'StagingRetainedTaskMayBeRunning' -and @(Get-ChildItem -LiteralPath $stageRoot -Force).Count -eq 1) 'Staging must be retained while the task may still be running.'
    Assert-Condition ($fixture.TaskState -eq 'Running' -and $fixture.StopTaskCount -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $stageRoot 'stage-removal-request.txt'))) 'The stop-failure fixture must leave the task running and skip cleanup.'

    $stageRoot = Join-Path $tempRoot ('stages-agent-restart-failure-' + [Guid]::NewGuid().ToString('N'))
    Reset-Fixture -Scenario 'AgentRestartFails' -StageRoot $stageRoot
    $restartFailure = Invoke-MockedWrapper
    $fixture = Get-MockFixture
    Assert-Condition ($restartFailure.Status -eq 'AgentRestartFailed' -and $restartFailure.FailureCategory -eq 'AgentRestartFailed') 'Agent restart failure must be reported.'
    Assert-Condition ($restartFailure.AgentRestartStatus -eq 'RestartFailed' -and $fixture.StartServiceCount -eq 1) 'The restart failure must be surfaced in the Agent status.'
    Assert-Condition ($restartFailure.ProbeTaskStatus -eq 'Removed' -and $restartFailure.CleanupStatus -eq 'FencedCleanupCompleted;StagingRemoved') 'Quiesced task and stage must still be cleaned after restart failure.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $stageRoot 'stage-removal-request.txt')) 'Stage cleanup must be requested after restart failure when the task is quiesced.'

    Write-Output 'Sunshine local API lifecycle mock checks passed.'
}
finally {
    if ($null -ne $mockModule) { Remove-Module -Name $mockModule.Name -ErrorAction SilentlyContinue }
    if ($hadOriginalStageRootEnvironmentVariable) {
        Set-Item -LiteralPath $stageRootEnvironmentPath -Value $originalStageRootEnvironmentValue
    }
    else {
        Remove-Item -LiteralPath $stageRootEnvironmentPath -ErrorAction SilentlyContinue
    }
}
