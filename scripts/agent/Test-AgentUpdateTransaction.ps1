param([string]$RecoveryCase)
$ErrorActionPreference = 'Stop'
$packageUpdater = Join-Path $PSScriptRoot 'Update-AgentPackage.ps1'
. $packageUpdater -DefineFunctionsOnly
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-update-transaction-' + [guid]::NewGuid().ToString('N'))
$root = [IO.Path]::GetFullPath($root)
New-Item -ItemType Directory -Path $root | Out-Null
$script:MigrationCalls = [Collections.Generic.List[string]]::new()
$noMigration = { param($PackagePath) $script:MigrationCalls.Add($PackagePath) }.GetNewClosure()
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
    $manifest = [pscustomobject]@{ packageFormatVersion=1; agentInformationalVersion='2.3.4'; sourceCommit='0123456789ab'; sourceDirty=$false; releaseBuild=$true; agentUpdateChallengeProtocol=1 }
    [IO.File]::WriteAllText((Join-Path $package 'AgentPackage.manifest.json'), (ConvertTo-Json $manifest -Depth 4))
    foreach ($name in @('Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Remove-StreamGuard.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1','Update-AgentPackage.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $package $name) }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\public\agent\Update-Agent.ps1') -Destination (Join-Path $package 'Update-Agent.ps1')
    return [pscustomobject]@{ Root = $Root; Install = $install; Data = $data; Package = $package; OperationId = 'ffeeddccbbaa99887766554433221100'; Version = '2.3.4' }
}
function New-AgentUpdateFakeServiceApi {
    param([switch]$FailAgentStart)
    $state = [pscustomobject]@{ Agent = 'Running'; Calls = [Collections.Generic.List[string]]::new(); FailAgentStart = [bool]$FailAgentStart }
    return [pscustomobject]@{
        State = $state
        StopAgent = { $state.Calls.Add('stop-agent'); $state.Agent = 'Stopped' }.GetNewClosure()
        StartAgent = { $state.Calls.Add('start-agent'); if ($state.FailAgentStart) { $state.FailAgentStart = $false; throw 'fake Agent startup failure' }; $state.Agent = 'Running' }.GetNewClosure()
        WaitAgent = { if ($state.Agent -ne 'Running') { throw 'Agent did not start' } }.GetNewClosure()
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
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
    Assert-AgentUpdateTest ($result.Outcome -eq 'applied') 'Normal swap did not complete.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'new-agent') 'New Agent binary was not installed.'
    Assert-AgentUpdateTest ($script:MigrationCalls.Count -eq 1 -and $script:MigrationCalls[0] -eq $fixture.Package) 'Successful swap must run the Stream Guard/account migration against the update package before starting the Agent.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'appsettings.json')).Contains('controller.example')) 'Existing appsettings.json was not preserved.'
    foreach ($name in @('identity\identity.json','enrollment-receipt.json','device-data.json')) { if (-not (Test-Path -LiteralPath (Join-Path $fixture.Data $name))) { throw "Data file was lost: $name" } }
    Assert-AgentUpdateTest (Test-Path -LiteralPath $result.BackupDirectory -PathType Container) 'Prior installation backup must remain until Controller heartbeat confirmation.'
    Assert-AgentUpdateTest ($api.State.Calls -notcontains 'start-sunshine' -and $api.State.Calls -notcontains 'stop-sunshine') 'Updater must not manage Sunshine directly.'

    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'backup-prepared')
    $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $fixture.Install -OperationId $fixture.OperationId
    Copy-AgentUpdateTree -Source $fixture.Install -Destination $plan.BackupDirectory -ServiceSid $serviceSid -ReadOnlyRoot
    Set-PrivateInstallAcl -Path (Join-Path $plan.BackupDirectory 'appsettings.json') -ServiceSid $serviceSid
    $api = New-AgentUpdateFakeServiceApi
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
    Assert-AgentUpdateTest ($result.Outcome -eq 'applied') 'A complete backup made before service stop must be reusable during recovery.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'new-agent') 'Recovery after a completed backup did not swap the requested binary.'

    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'rollback')
    $api = New-AgentUpdateFakeServiceApi -FailAgentStart
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
    Assert-AgentUpdateTest ($result.Outcome -eq 'rolledBack') 'Agent start failure must roll back.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'old-agent') 'Rollback did not restore old Agent binary.'
    Assert-AgentUpdateTest (Test-Path -LiteralPath $result.BackupDirectory -PathType Container) 'Rollback backup must be retained for verification.'
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
    Assert-AgentUpdateTest ($result.Outcome -eq 'applied') 'After a verified rollback, recovery must be able to retry the target under the same operation.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'new-agent') 'Recovery retry did not install the challenge-capable target.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $result.BackupDirectory 'Agent.exe')) -eq 'old-agent') 'Recovery retry must preserve the original rollback backup.'

    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'rollback-stop-failure')
    $api = New-AgentUpdateFakeServiceApi -FailAgentStart
    $serviceState = $api.State
    $serviceState | Add-Member -NotePropertyName StopAttempts -NotePropertyValue 0
    $api.StopAgent = { $serviceState.StopAttempts++; $serviceState.Calls.Add('stop-agent'); if ($serviceState.StopAttempts -eq 2) { throw 'fake rollback stop failure' }; $serviceState.Agent = 'Stopped' }.GetNewClosure()
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
    Assert-AgentUpdateTest ($result.Outcome -eq 'failed') 'A failed rollback stop must remain failed and locked.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'new-agent') 'Failed stop must not restore files while a service may still be running.'
    Assert-AgentUpdateTest (Test-Path -LiteralPath $result.BackupDirectory -PathType Container) 'An uncertain rollback must retain its backup.'

    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'initial-stop-failure')
    $api = New-AgentUpdateFakeServiceApi
    $api.StopAgent = { throw 'fake initial Agent stop failure' }
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
    Assert-AgentUpdateTest ($result.Outcome -eq 'failed') 'An uncertain initial stop must not be reported as a verified rollback.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'old-agent') 'A failed initial stop must not swap binaries.'

    $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'interrupted')
    $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $fixture.Install -OperationId $fixture.OperationId
    Copy-AgentUpdateTree -Source $fixture.Install -Destination $plan.BackupDirectory -ServiceSid $serviceSid -ReadOnlyRoot
    Set-PrivateInstallAcl -Path (Join-Path $plan.BackupDirectory 'appsettings.json') -ServiceSid $serviceSid
    Remove-Item -LiteralPath $fixture.Install -Recurse -Force
    $api = New-AgentUpdateFakeServiceApi
    $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
    Assert-AgentUpdateTest ($result.Outcome -eq 'rolledBack') 'Interrupted swap must recover the original install.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'old-agent') 'Interrupted swap recovery lost old Agent binary.'
    Assert-AgentUpdateTest ($api.State.Calls -contains 'start-agent') 'Interrupted recovery must restore files and restart the managed Agent service.'
    $retry = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
    Assert-AgentUpdateTest ($retry.Outcome -eq 'applied') 'A later recovery of a verified rollback must be able to install the capable target Agent.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'new-agent') 'Recovery after a legacy rollback must install the selected target.'
    Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $plan.BackupDirectory 'Agent.exe')) -eq 'old-agent') 'Recovery must preserve the original rollback backup.'

    if (-not $RecoveryCase -or $RecoveryCase -eq 'pre-backup') {
        $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'partial-stage')
        $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $fixture.Install -OperationId $fixture.OperationId
        foreach ($partial in @($plan.StageDirectory,$plan.BackupStageDirectory)) {
            New-PrivateInstallDirectory -Path $partial -ServiceSid $serviceSid -ReadOnly | Out-Null
            [IO.File]::WriteAllText((Join-Path $partial 'incomplete.txt'),'incomplete')
        }
        $api = New-AgentUpdateFakeServiceApi
        $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
        Assert-AgentUpdateTest ($result.Outcome -eq 'applied') 'An interrupted stage before backup publication must safely resume the update.'
        Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $fixture.Install 'Agent.exe')) -eq 'new-agent') 'Pre-backup recovery did not install the target binary.'
        Assert-AgentUpdateTest ([IO.File]::ReadAllText((Join-Path $plan.BackupDirectory 'Agent.exe')) -eq 'old-agent') 'Pre-backup recovery did not protect the original binary.'

        $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'unsafe-stage')
        $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $fixture.Install -OperationId $fixture.OperationId
        New-Item -ItemType Directory -Path $plan.StageDirectory | Out-Null
        [IO.File]::WriteAllText((Join-Path $plan.StageDirectory 'untrusted.txt'),'untrusted')
        $api = New-AgentUpdateFakeServiceApi
        $rejected = $false
        try { Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration | Out-Null } catch { $rejected = $true }
        Assert-AgentUpdateTest $rejected 'An unprotected abandoned stage must be rejected.'
        Assert-AgentUpdateTest (Test-Path -LiteralPath (Join-Path $plan.StageDirectory 'untrusted.txt')) 'An untrusted stage must not be removed.'
        Assert-AgentUpdateTest ($api.State.Calls.Count -eq 0) 'An untrusted stage must not cause service mutations.'

        $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'ambiguous-prior')
        $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $fixture.Install -OperationId $fixture.OperationId
        New-PrivateInstallDirectory -Path $plan.PriorDirectory -ServiceSid $serviceSid -ReadOnly | Out-Null
        $api = New-AgentUpdateFakeServiceApi
        $rejected = $false
        try { Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration | Out-Null } catch { $rejected = $true }
        Assert-AgentUpdateTest $rejected 'A prior directory without its operation backup must remain ambiguous.'
        Assert-AgentUpdateTest ((Test-Path -LiteralPath $plan.PriorDirectory) -and $api.State.Calls.Count -eq 0) 'Ambiguous recovery must preserve files and service state.'
    }
    if (-not $RecoveryCase -or $RecoveryCase -eq 'partial-restore') {
        foreach ($withConfig in @($false,$true)) {
            $fixture = New-AgentUpdateFixture -Root (Join-Path $root "partial-restore-$withConfig")
            $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $fixture.Install -OperationId $fixture.OperationId
            Copy-AgentUpdateTree -Source $fixture.Install -Destination $plan.BackupDirectory -ServiceSid $serviceSid -ReadOnlyRoot
            Set-PrivateInstallAcl -Path (Join-Path $plan.BackupDirectory 'appsettings.json') -ServiceSid $serviceSid
            Remove-Item -LiteralPath $fixture.Install -Recurse -Force
            New-PrivateInstallDirectory -Path $fixture.Install -ServiceSid $serviceSid -ReadOnly | Out-Null
            [IO.File]::WriteAllText((Join-Path $fixture.Install 'Agent.exe'),'partial-agent')
            if ($withConfig) {
                Copy-Item -LiteralPath (Join-Path $plan.BackupDirectory 'Agent.deps.json') -Destination $fixture.Install
                Copy-Item -LiteralPath (Join-Path $plan.BackupDirectory 'appsettings.json') -Destination $fixture.Install
                Set-PrivateInstallAcl -Path (Join-Path $fixture.Install 'appsettings.json') -ServiceSid $serviceSid
            }
            $api = New-AgentUpdateFakeServiceApi
            $api.State.Agent = 'Stopped'
            $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
            Assert-AgentUpdateTest ($result.Outcome -eq 'rolledBack') 'An interrupted partial restore must complete a verified rollback.'
            Assert-AgentUpdateTest (Test-AgentUpdateTreesEquivalent -First $fixture.Install -Second $plan.BackupDirectory) 'Restored installation must exactly match the protected backup.'
            Assert-AgentUpdateTest ($api.State.Calls -contains 'stop-agent' -and $api.State.Calls -contains 'start-agent') 'Partial recovery must stop and restart the managed Agent service.'
        }

        $fixture = New-AgentUpdateFixture -Root (Join-Path $root 'retry-restore')
        $plan = Get-AgentUpdateTransactionPlan -InstallDirectory $fixture.Install -OperationId $fixture.OperationId
        Copy-AgentUpdateTree -Source $fixture.Install -Destination $plan.BackupDirectory -ServiceSid $serviceSid -ReadOnlyRoot
        Set-PrivateInstallAcl -Path (Join-Path $plan.BackupDirectory 'appsettings.json') -ServiceSid $serviceSid
        Remove-Item -LiteralPath $fixture.Install -Recurse -Force
        New-PrivateInstallDirectory -Path $fixture.Install -ServiceSid $serviceSid -ReadOnly | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixture.Install 'Agent.exe'),'partial-agent')
        New-PrivateInstallDirectory -Path $plan.RestoreDirectory -ServiceSid $serviceSid -ReadOnly | Out-Null
        New-PrivateInstallDirectory -Path (Join-Path $plan.RestoreDirectory 'Agent') -ServiceSid $serviceSid -ReadOnly | Out-Null
        [IO.File]::WriteAllText((Join-Path $plan.RestoreDirectory 'Agent\Agent.exe'),'partial-copy')
        $api = New-AgentUpdateFakeServiceApi -FailAgentStart
        $api.State.Agent = 'Stopped'
        $rejected = $false
        try { Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration | Out-Null } catch { $rejected = $true }
        Assert-AgentUpdateTest $rejected 'A failed restored Agent start must not report rollback.'
        Assert-AgentUpdateTest (Test-Path -LiteralPath $plan.RestoreDirectory -PathType Container) 'An interrupted rollback must retain its operation-bound marker.'
        $result = Invoke-AgentUpdateTransaction -InstallDirectory $fixture.Install -DataDirectory $fixture.Data -PackageRoot $fixture.Package -OperationId $fixture.OperationId -TargetVersion $fixture.Version -ServiceSid $serviceSid -ServiceApi $api -StreamGuardMigration $noMigration
        Assert-AgentUpdateTest ($result.Outcome -eq 'rolledBack') 'A retry after restoring files must finish rollback, not resume the update.'
        Assert-AgentUpdateTest (Test-AgentUpdateTreesEquivalent -First $fixture.Install -Second $plan.BackupDirectory) 'A repeated rollback must leave the original backup intact and installed.'
    }
    Write-Output 'Agent update transaction tests passed using temporary files and fake service APIs only.'
} finally {
    if (Test-Path -LiteralPath $root) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($root) -notlike 'kr-agent-update-transaction-*') { throw 'Unsafe transaction test cleanup path.' }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
