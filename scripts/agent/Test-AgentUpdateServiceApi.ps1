$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\public\agent\Update-Agent.ps1') -ControllerUrl 'https://controller.example/' -MachineId ('0' * 32) -OperationId ('1' * 32) -UpdateTicket ('A' * 43) -ReleaseVersion '2.3.4' -TargetVersion '2.3.4' -PackageSha256 ('A' * 64) -TrustedRootThumbprint ('B' * 40) -AdminReturnUrl 'https://admin.example/admin/agent-releases' -DefineFunctionsOnly
. (Join-Path $PSScriptRoot 'Update-AgentPackage.ps1') -DefineFunctionsOnly
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')

$failures = [Collections.Generic.List[string]]::new()

# The Agent service remains a virtual service for ACL purposes even though the
# process runs as LocalSystem: the fence reader resolves the deterministic
# virtual service SID and expects it in the three-principal maintenance ACL.
$virtualSid = 'S-1-5-80-306869082-510711969-1671650537-2166470815-2388419276'
try {
    $agentServiceSid = Get-AgentUpdateAgentServiceSid
    if ($agentServiceSid.Value -ne $virtualSid) { throw "Agent service SID lookup must return the deterministic virtual service SID, got $($agentServiceSid.Value)." }
} catch { $failures.Add($_.Exception.Message) }

try {
    $expectedBootstrapRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'KamenRaider.AgentMaintenanceBootstrap'
    if ((Get-AgentUpdateBootstrapRoot) -ne $expectedBootstrapRoot) { throw 'Agent maintenance bootstrap must use the machine-wide Windows data directory.' }
} catch { $failures.Add($_.Exception.Message) }

try {
    $security = New-AgentUpdateFenceDirectorySecurity
    $rules = @($security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if (-not $security.AreAccessRulesProtected -or $security.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544') {
        throw 'Maintenance fence descriptor must be protected and owned by Administrators.'
    }
    $expected = @{
        'S-1-5-18' = [Security.AccessControl.FileSystemRights]::FullControl
        'S-1-5-32-544' = [Security.AccessControl.FileSystemRights]::FullControl
        $virtualSid = [Security.AccessControl.FileSystemRights]'ReadAndExecute,Synchronize'
    }
    if ($rules.Count -ne 3) { throw 'Maintenance fence descriptor must contain exactly three principals.' }
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $rule.IsInherited -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or
            $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' -or
            -not $expected.ContainsKey($rule.IdentityReference.Value) -or
            $rule.FileSystemRights -ne $expected[$rule.IdentityReference.Value]) {
            throw 'Maintenance fence descriptor differs from the Agent reader policy.'
        }
    }
} catch { $failures.Add($_.Exception.Message) }

# The legacy updater service API must manage only the Agent service now.
$global:AgentUpdateServiceTestCalls = [Collections.Generic.List[string]]::new()
$global:AgentUpdateServiceStatus = 'Running'
function global:Get-Service {
    param($Name, $ErrorAction)
    if ($Name -ne 'KamenRaider Agent') { throw "Unexpected service target: $Name" }
    $service = [pscustomobject]@{ Name = $Name; Status = $global:AgentUpdateServiceStatus }
    $service | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value { param($status, $timeout) }
    return $service
}
function global:Stop-Service {
    param($Name, [switch]$Force, $ErrorAction)
    if ($Force) { throw 'Agent stop must not be forced; StopAgent owns normal stop semantics.' }
    $global:AgentUpdateServiceTestCalls.Add('stop:' + $Name)
    $global:AgentUpdateServiceStatus = 'Stopped'
}
function global:Start-Service {
    param($Name, $ErrorAction)
    $global:AgentUpdateServiceTestCalls.Add('start:' + $Name)
    $global:AgentUpdateServiceStatus = 'Running'
}
try {
    try {
        $api = Get-AgentUpdateLiveServiceApi -AgentServiceName 'KamenRaider Agent'
        if ($api.PSObject.Properties.Name -contains 'StopGuard' -or $api.PSObject.Properties.Name -contains 'StartGuard') {
            throw 'The update service API must not manage a Stream Guard service.'
        }
        & $api.StopAgent
        if ($global:AgentUpdateServiceStatus -ne 'Stopped') { throw 'StopAgent did not stop the Agent service.' }
        & $api.StartAgent
        if ($global:AgentUpdateServiceStatus -ne 'Running') { throw 'StartAgent did not start the Agent service.' }
        $expectedCalls = 'stop:KamenRaider Agent,start:KamenRaider Agent'
        if (($global:AgentUpdateServiceTestCalls -join ',') -ne $expectedCalls) {
            throw "Update service API called unexpected services: $($global:AgentUpdateServiceTestCalls -join ',')"
        }
    } catch { $failures.Add($_.Exception.Message) }
} finally {
    Remove-Item Function:\Get-Service -Force
    Remove-Item Function:\Stop-Service -Force
    Remove-Item Function:\Start-Service -Force
    Remove-Variable AgentUpdateServiceTestCalls -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable AgentUpdateServiceStatus -Scope Global -ErrorAction SilentlyContinue
}

# The legacy update package must ship the idempotent Guard-removal migration and
# no longer ship the removed Stream Guard setup script.
$root = Join-Path ([IO.Path]::GetTempPath()) ('kr-update-service-api-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path (Join-Path $root 'Agent') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'Agent\Agent.exe'), 'fixture')
    [IO.File]::WriteAllText((Join-Path $root 'Agent\Agent.deps.json'), '{"libraries":{"Agent/2.3.4":{"type":"project"}}}')
    [IO.File]::WriteAllText((Join-Path $root 'AgentPackage.manifest.json'), '{"packageFormatVersion":1,"agentInformationalVersion":"2.3.4","sourceCommit":"0123456789ab","sourceDirty":false,"releaseBuild":true,"agentUpdateChallengeProtocol":1}')
    foreach ($name in @('Install-AgentPackage.ps1','Install-Service.ps1','Install-TightVncServer.ps1','Remove-StreamGuard.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1','Update-AgentPackage.ps1','Update-Agent.ps1')) {
        [IO.File]::WriteAllText((Join-Path $root $name), 'fixture')
    }
    try {
        Assert-AgentUpdatePackageRoot -PackageRoot $root -TargetVersion '2.3.4' | Out-Null
        Remove-Item -LiteralPath (Join-Path $root 'Remove-StreamGuard.ps1') -Force
        $rejected = $false
        try { Assert-AgentUpdatePackageRoot -PackageRoot $root -TargetVersion '2.3.4' | Out-Null } catch { $rejected = $_.Exception.Message -like '*Remove-StreamGuard.ps1*' }
        if (-not $rejected) { throw 'The update package must require Remove-StreamGuard.ps1.' }
        [IO.File]::WriteAllText((Join-Path $root 'Prepare-SunshineStreamGate.ps1'), 'fixture')
        $rejected = $false
        try { Assert-AgentUpdatePackageRoot -PackageRoot $root -TargetVersion '2.3.4' | Out-Null } catch { $rejected = $_.Exception.Message -like '*Unexpected Agent package file*' }
        if (-not $rejected) { throw 'The update package must reject the removed Prepare-SunshineStreamGate.ps1.' }
    } catch { $failures.Add($_.Exception.Message) }
} finally {
    if (Test-Path -LiteralPath $root) {
        $resolved = [IO.Path]::GetFullPath($root)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-update-service-api-*') { throw 'Unsafe update service API test cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

if ($failures.Count) { throw ($failures -join "`n") }
Write-Output 'Agent update service SID/ACL, Agent-only service API, and Guard-migration package contract tests passed with fake services and temporary files.'
