$ErrorActionPreference = 'Stop'
$packageUpdater = Join-Path $PSScriptRoot 'Update-AgentPackage.ps1'
. $packageUpdater -DefineFunctionsOnly
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-update-transaction-' + [guid]::NewGuid().ToString('N'))
$root = [IO.Path]::GetFullPath($root)
New-Item -ItemType Directory -Path $root | Out-Null
function New-AgentUpdateFixture {
    param([Parameter(Mandatory)][string]$Root)
    $install = Join-Path $Root 'KamenRaider\Agent'
    $data = Join-Path $Root 'ProgramData\KamenRaider\Agent'
    $package = Join-Path $Root 'package'
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $data 'identity') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $package 'Agent') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $install 'Agent.exe'), 'old-agent')
    $oldDeps = [pscustomobject]@{ libraries = [pscustomobject]@{ 'Agent/1.2.3' = [pscustomobject]@{ type = 'project' } } }
    [IO.File]::WriteAllText((Join-Path $install 'Agent.deps.json'), (ConvertTo-Json $oldDeps -Depth 5))
    [IO.File]::WriteAllText((Join-Path $install 'appsettings.json'), '{"ControllerUrl":"https://controller.example/","DataDirectory":"' + $data.Replace('\','\\') + '"}')
    [IO.File]::WriteAllText((Join-Path $data 'identity\identity.json'), '{"MachineId":"00112233445566778899aabbccddeeff","PrivateKey":"preserve-me"}')
    [IO.File]::WriteAllText((Join-Path $data 'enrollment-receipt.json'), '{"MachineId":"00112233445566778899aabbccddeeff","ApprovalStatus":"approved"}')
    [IO.File]::WriteAllText((Join-Path $data 'device-data.json'), 'keep-data')
    [IO.File]::WriteAllText((Join-Path $package 'Agent\Agent.exe'), 'new-agent')
    $deps = [pscustomobject]@{ libraries = [pscustomobject]@{ 'Agent/2.3.4' = [pscustomobject]@{ type = 'project' } } }
    [IO.File]::WriteAllText((Join-Path $package 'Agent\Agent.deps.json'), (ConvertTo-Json $deps -Depth 5))
    $manifest = [pscustomobject]@{ packageFormatVersion=1; agentInformationalVersion='2.3.4'; sourceCommit='0123456789ab'; sourceDirty=$false; releaseBuild=$true }
    [IO.File]::WriteAllText((Join-Path $package 'AgentPackage.manifest.json'), (ConvertTo-Json $manifest -Depth 4))
    foreach ($name in @('Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Prepare-SunshineStreamGate.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1','Update-AgentPackage.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $package $name) }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\public\agent\Update-Agent.ps1') -Destination (Join-Path $package 'Update-Agent.ps1')
    return [pscustomobject]@{ Root = $Root; Install = $install; Data = $data; Package = $package; OperationId = 'ffeeddccbbaa99887766554433221100'; Version = '2.3.4' }
}
function New-AgentUpdateFakeServiceApi {
    param([switch]$FailAgentStart)
    $state = [pscustomobject]@{ Agent = 'Running'; Guard = 'Running'; Calls = [Collections.Generic.List[string]]::new(); FailAgentStart = [bool]$FailAgentStart }
    return [pscustomobject]@{
        State = $state
        StopAgent = { $state.Calls.Add('stop-agent'); $state.Agent = 'Stopped' }.GetNewClosure()
        StopGuard = { $state.Calls.Add('stop-guard'); $state.Guard = 'Stopped' }.GetNewClosure()
        StartGuard = { $state.Calls.Add('start-guard'); $state.Guard = 'Running' }.GetNewClosure()
        StartAgent = { $state.Calls.Add('start-agent'); if ($state.FailAgentStart) { $state.FailAgentStart = $false; throw 'fake Agent startup failure' }; $state.Agent = 'Running' }.GetNewClosure()
        WaitAgent = { if ($state.Agent -ne 'Running') { throw 'Agent did not start' } }.GetNewClosure()
        WaitGuard = { if ($state.Guard -ne 'Running') { throw 'Guard did not start' } }.GetNewClosure()
    }
}
function Assert-AgentUpdateTest {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    $serviceSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'success')
    $api = New-AgentUpdateFakeServiceApi
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api
    Assert-AgentUpdateTest ($result.Outcome -eq 'applied') 'Normal swap did not complete.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'new-agent') 'New Agent binary was not installed.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'appsettings.json')).Contains('controller.example')) 'Existing appsettings.json was not preserved.'
    foreach ($name in @('identity\identity.json','enrollment-receipt.json','device-data.json')) { if (-not (Test-Path -LiteralPath (Join-Path $fixture.Data $name))) { throw "Data file was lost: $name" } }
    Assert-AgentUpdateTest (Test-Path -LiteralPath $result.BackupDirectory -PathType Container) 'Prior installation backup must remain until Controller heartbeat confirmation.'
    Assert-AgentUpdateTest ($api.State.Calls -notcontains 'start-sunshine' -and $api.State.Calls -notcontains 'stop-sunshine') 'Updater must not manage Sunshine directly.'

    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'backup-prepared')
    $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $fixture.Install -OperationId $fixture.OperationId
    Copy-AgentUpdateTree -Source $fixture.Install -Destination $plan.BackupDirectory -ServiceSid $serviceSid -ReadOnlyRoot
    Set-PrivateInstallAcl -Path (Join-Path $plan.BackupDirectory 'appsettings.json') -ServiceSid $serviceSid
    $api = New-AgentUpdateFakeServiceApi
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api
    Assert-AgentUpdateTest ($result.Outcome -eq 'applied') 'A complete backup made before service stop must be reusable during recovery.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'new-agent') 'Recovery after a completed backup did not swap the requested binary.'

    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'rollback')
    $api = New-AgentUpdateFakeServiceApi -FailAgentStart
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api
    Assert-AgentUpdateTest ($result.Outcome -eq 'rolledBack') 'Agent start failure must roll back.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'old-agent') 'Rollback did not restore old Agent binary.'
    Assert-AgentUpdateTest (Test-Path -LiteralPath $result.BackupDirectory -PathType Container) 'Rollback backup must be retained for verification.'

    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'rollback-stop-failure')
    $api = New-AgentUpdateFakeServiceApi -FailAgentStart
    $serviceState = $api.State
    $serviceState | Add-Member -NotePropertyName StopAttempts -NotePropertyValue 0
    $api.StopAgent = { $serviceState.StopAttempts++; $serviceState.Calls.Add('stop-agent'); if ($serviceState.StopAttempts -eq 2) { throw 'fake rollback stop failure' }; $serviceState.Agent = 'Stopped' }.GetNewClosure()
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api
    Assert-AgentUpdateTest ($result.Outcome -eq 'failed') 'A failed rollback stop must remain failed and locked.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'new-agent') 'Failed stop must not restore files while a service may still be running.'
    Assert-AgentUpdateTest (Test-Path -LiteralPath $result.BackupDirectory -PathType Container) 'An uncertain rollback must retain its backup.'

    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'initial-stop-failure')
    $api = New-AgentUpdateFakeServiceApi
    $api.StopGuard = { throw 'fake initial guard stop failure' }
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api
    Assert-AgentUpdateTest ($result.Outcome -eq 'failed') 'An uncertain initial stop must not be reported as a verified rollback.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'old-agent') 'A failed initial stop must not swap binaries.'

    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'interrupted')
    $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $fixture.Install -OperationId $fixture.OperationId
    Copy-AgentUpdateTree -Source $fixture.Install -Destination $plan.BackupDirectory -ServiceSid $serviceSid -ReadOnlyRoot
    Set-PrivateInstallAcl -Path (Join-Path $plan.BackupDirectory 'appsettings.json') -ServiceSid $serviceSid
    Remove-Item -LiteralPath $fixture.Install -Recurse -Force
    $api = New-AgentUpdateFakeServiceApi
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api
    Assert-AgentUpdateTest ($result.Outcome -eq 'rolledBack') 'Interrupted swap must recover the original install.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'old-agent') 'Interrupted swap recovery lost old Agent binary.'
    Assert-AgentUpdateTest ($api.State.Calls -contains 'start-guard' -and $api.State.Calls -contains 'start-agent') 'Interrupted recovery must restore files and restart the managed services.'
    Write-Output 'Agent update transaction tests passed using temporary files and fake service APIs only.'
} finally {
    if (Test-Path -LiteralPath $root) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($root) -notlike 'kr-agent-update-transaction-*') { throw 'Unsafe transaction test cleanup path.' }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
