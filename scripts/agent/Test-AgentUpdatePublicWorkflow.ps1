$ErrorActionPreference = 'Stop'
$packageUpdater = Join-Path $PSScriptRoot 'Update-AgentPackage.ps1'
. $packageUpdater -DefineFunctionsOnly
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')

# The public update wrapper now drives the signed AgentMaintenance helper; this
# across-the-package workflow test covers the shipped legacy transaction that the
# package still carries for existing machines, including the new Guard/account
# migration invocation.
$publicUpdater = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\public\agent\Update-Agent.ps1'))
if ($publicUpdater -match 'KamenRaider Stream Guard' -or $publicUpdater -match 'Prepare-SunshineStreamGate\.ps1' -or $publicUpdater -match '\-\-stream-guard') {
    throw 'The public Agent update wrapper must not contain Stream Guard logic.'
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-update-public-' + [guid]::NewGuid().ToString('N'))
$root = [IO.Path]::GetFullPath($root)
New-Item -ItemType Directory -Path $root | Out-Null
$serviceSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')

function New-AgentUpdatePublicFixture {
    param([Parameter(Mandatory)][string]$Root)
    $install = Join-Path $Root 'KamenRaider\Agent'
    $data = Join-Path $Root 'ProgramData\KamenRaider\Agent'
    $package = Join-Path $Root 'package'
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $data 'identity') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $package 'Agent') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $install 'Agent.exe'), 'old-agent')
    [IO.File]::WriteAllText((Join-Path $install 'Agent.deps.json'), '{"libraries":{"Agent/1.2.3":{"type":"project"}}}')
    [IO.File]::WriteAllText((Join-Path $install 'appsettings.json'), '{"ControllerUrl":"https://controller.example/","DataDirectory":"' + $data.Replace('\','\\') + '"}')
    [IO.File]::WriteAllText((Join-Path $data 'identity\identity.json'), '{"MachineId":"00112233445566778899aabbccddeeff","PrivateKey":"keep"}')
    [IO.File]::WriteAllText((Join-Path $package 'Agent\Agent.exe'), 'new-agent')
    [IO.File]::WriteAllText((Join-Path $package 'Agent\Agent.deps.json'), '{"libraries":{"Agent/2.3.4":{"type":"project"}}}')
    [IO.File]::WriteAllText((Join-Path $package 'AgentPackage.manifest.json'), '{"packageFormatVersion":1,"agentInformationalVersion":"2.3.4","sourceCommit":"0123456789ab","sourceDirty":false,"releaseBuild":true,"agentUpdateChallengeProtocol":1}')
    foreach ($name in @('Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Remove-StreamGuard.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1','Update-AgentPackage.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $package $name)
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\public\agent\Update-Agent.ps1') -Destination (Join-Path $package 'Update-Agent.ps1')
    [pscustomobject]@{ Root = $Root; Install = $install; Data = $data; Package = $package; OperationId = 'ffeeddccbbaa99887766554433221100'; Version = '2.3.4' }
}

function New-AgentUpdatePublicServiceApi {
    param([switch]$FailAgentStart, [switch]$FailInitialStop)
    $state = [pscustomobject]@{ Agent = 'Running'; Calls = [Collections.Generic.List[string]]::new() }
    $firstStop = $true
    return [pscustomobject]@{
        State = $state
        StopAgent = {
            $state.Calls.Add('stop-agent')
            if ($FailInitialStop -and $firstStop) { $firstStop = $false; throw 'fake initial Agent stop failure' }
            $state.Agent = 'Stopped'
        }.GetNewClosure()
        StartAgent = {
            $state.Calls.Add('start-agent')
            if ($FailAgentStart) { $FailAgentStart = $false; throw 'fake Agent startup failure' }
            $state.Agent = 'Running'
        }.GetNewClosure()
    }
}

function New-AgentUpdatePublicMigration {
    param([Collections.Generic.List[string]]$Events, [switch]$FailMigration)
    return {
        param($PackagePath)
        $Events.Add('migrate-stream-guard')
        if ($FailMigration) { throw 'fake Stream Guard migration failure' }
    }.GetNewClosure()
}

function Assert-PublicWorkflow {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    $fixture = New-AgentUpdatePublicFixture -Root (Join-Path $root 'success')
    $events = [Collections.Generic.List[string]]::new()
    $api = New-AgentUpdatePublicServiceApi
    $state = $api.State
    $api.StartAgent = { $state.Calls.Add('start-agent'); $events.Add('start-agent'); $state.Agent = 'Running' }.GetNewClosure()
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package `
        -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api `
        -StreamGuardMigration (New-AgentUpdatePublicMigration -Events $events)
    Assert-PublicWorkflow ($result.Outcome -eq 'applied') 'Normal swap did not complete.'
    Assert-PublicWorkflow ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'new-agent') 'New Agent binary was not installed.'
    Assert-PublicWorkflow ([IO.File]::ReadAllText((Join-Path $fixture.Install 'appsettings.json')).Contains('controller.example')) 'Existing appsettings.json was not preserved.'
    Assert-PublicWorkflow (Test-Path -LiteralPath (Join-Path $fixture.Data 'identity\identity.json')) 'Agent identity was not preserved.'
    Assert-PublicWorkflow (Test-Path -LiteralPath $result.BackupDirectory -PathType Container) 'Prior installation backup must remain until Controller heartbeat confirmation.'
    $migrationIndex = $events.IndexOf('migrate-stream-guard')
    $startIndex = $events.IndexOf('start-agent')
    Assert-PublicWorkflow ($migrationIndex -ge 0 -and $startIndex -gt $migrationIndex) 'The Guard/account migration must run before the new Agent service starts.'

    $fixture = New-AgentUpdatePublicFixture -Root (Join-Path $root 'migration-failure')
    $events = [Collections.Generic.List[string]]::new()
    $api = New-AgentUpdatePublicServiceApi
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package `
        -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api `
        -StreamGuardMigration (New-AgentUpdatePublicMigration -Events $events -FailMigration)
    Assert-PublicWorkflow ($result.Outcome -eq 'rolledBack') 'A failing Guard migration must roll the Agent files back.'
    Assert-PublicWorkflow ($result.Message -match 'fake Stream Guard migration failure') 'The migration failure was not surfaced.'
    Assert-PublicWorkflow ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'old-agent') 'Rollback after a failed migration did not restore the old Agent binary.'
    Assert-PublicWorkflow ($api.State.Calls -contains 'start-agent') 'Rollback after a failed migration must restart the previous Agent service.'

    $fixture = New-AgentUpdatePublicFixture -Root (Join-Path $root 'initial-stop-failure')
    $events = [Collections.Generic.List[string]]::new()
    $api = New-AgentUpdatePublicServiceApi -FailInitialStop
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package `
        -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api `
        -StreamGuardMigration (New-AgentUpdatePublicMigration -Events $events)
    Assert-PublicWorkflow ($result.Outcome -eq 'failed') 'An uncertain initial stop must not be reported as a verified rollback.'
    Assert-PublicWorkflow ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'old-agent') 'A failed initial stop must not swap binaries.'
    Assert-PublicWorkflow ($events.Count -eq 0) 'A failed initial stop must not run the Guard/account migration.'

    $fixture = New-AgentUpdatePublicFixture -Root (Join-Path $root 'interrupted')
    $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $fixture.Install -OperationId $fixture.OperationId
    Copy-AgentUpdateTree -Source $fixture.Install -Destination $plan.BackupDirectory -ServiceSid $serviceSid -ReadOnlyRoot
    Set-PrivateInstallAcl -Path (Join-Path $plan.BackupDirectory 'appsettings.json') -ServiceSid $serviceSid
    Remove-Item -LiteralPath $fixture.Install -Recurse -Force
    $events = [Collections.Generic.List[string]]::new()
    $api = New-AgentUpdatePublicServiceApi
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package `
        -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api `
        -StreamGuardMigration (New-AgentUpdatePublicMigration -Events $events)
    Assert-PublicWorkflow ($result.Outcome -eq 'rolledBack') 'Interrupted swap must recover the original install.'
    Assert-PublicWorkflow ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'old-agent') 'Interrupted swap recovery lost old Agent binary.'
    Assert-PublicWorkflow ($api.State.Calls -contains 'start-agent') 'Interrupted recovery must restart the Agent service.'
    Assert-PublicWorkflow ($api.State.Calls -notcontains 'stop-agent' -or $events.Count -eq 0) 'Interrupted pre-stop recovery must not run the Guard/account migration.'

    Write-Output 'Public Agent update workflow and Guard/account migration wiring passed with fake services and temporary files.'
} finally {
    if (Test-Path -LiteralPath $root) {
        $resolved = [IO.Path]::GetFullPath($root)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-update-public-*') { throw 'Unsafe public workflow test cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
