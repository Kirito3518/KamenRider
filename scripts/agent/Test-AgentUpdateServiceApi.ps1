$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\public\agent\Update-Agent.ps1') -ControllerUrl 'https://controller.example/' -MachineId ('0' * 32) -OperationId ('1' * 32) -UpdateTicket ('A' * 43) -ReleaseVersion '2.3.4' -TargetVersion '2.3.4' -PackageSha256 ('A' * 64) -TrustedRootThumbprint ('B' * 40) -AdminReturnUrl 'https://admin.example/admin/agent-releases' -DefineFunctionsOnly
. (Join-Path $PSScriptRoot 'Update-AgentPackage.ps1') -DefineFunctionsOnly
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')

$global:AgentUpdateServiceTestCalls = [Collections.Generic.List[string]]::new()
function global:Get-CimInstance {
    param($ClassName,$Filter,$ErrorAction)
    return [pscustomobject]@{ Name = 'KamenRaider Agent'; StartName = 'NT SERVICE\KamenRaider Agent'; StartMode = 'Auto'; PathName = 'mock'; State = 'Running' }
}
function global:Get-Service {
    param($Name,$ErrorAction)
    $service = [pscustomobject]@{ Name = $Name; Status = 'Running'; ServicesDependedOn = @([pscustomobject]@{ ServiceName = 'KamenRaider Stream Guard' }) }
    $service | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value { param($status,$timeout) }
    return $service
}
function global:Stop-Service {
    param($Name,[switch]$Force,$ErrorAction)
    if ($Force) { throw 'Guard stop was forced and could stop Sunshine.' }
    $global:AgentUpdateServiceTestCalls.Add($Name)
}
function global:Get-Acl { param($LiteralPath,$ErrorAction) return $global:AgentUpdateTestAcl }
try {
    $failures = [Collections.Generic.List[string]]::new()
    try {
        $expectedLockParent = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'KamenRaider'
        if ((Get-AgentUpdateLockParent) -ne $expectedLockParent) { throw 'Agent lock location must use the machine-wide Windows data directory.' }
    } catch { $failures.Add($_.Exception.Message) }
    try {
        $record = Get-AgentUpdateServiceRecord -Name 'KamenRaider Agent'
        if ('KamenRaider Stream Guard' -notin @($record.Dependencies)) { throw 'Service preflight did not read the configured Guard dependency.' }
    } catch { $failures.Add($_.Exception.Message) }
    try {
        $api = Get-AgentUpdateLiveServiceApi -AgentServiceName 'KamenRaider Agent' -GuardServiceName 'KamenRaider Stream Guard'
        & $api.StopGuard
        if ($global:AgentUpdateServiceTestCalls.Count -ne 1 -or $global:AgentUpdateServiceTestCalls[0] -ne 'KamenRaider Stream Guard') { throw 'Guard stop did not target only the managed Guard service.' }
    } catch { $failures.Add($_.Exception.Message) }
    $aclRoot = Join-Path ([IO.Path]::GetTempPath()) ('kr-update-acl-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $aclRoot | Out-Null
        $sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
        $trusted = Get-TrustedInstallSids @($sid)
        $global:AgentUpdateTestAcl = New-PrivateInstallSecurity -ServiceSid $sid -Directory -ReadOnly
        Assert-AgentUpdatePathAcl -Path $aclRoot -TrustedSids $trusted -RequireReadOnlySid $sid
        $global:AgentUpdateTestAcl = New-PrivateInstallSecurity -ServiceSid $sid -Directory
        $rejected = $false
        try { Assert-AgentUpdatePathAcl -Path $aclRoot -TrustedSids $trusted -RequireReadOnlySid $sid } catch { $rejected = $true }
        if (-not $rejected) { throw 'Service-writable Agent binaries were accepted.' }
        $script:realAclCheck = ${function:Assert-AgentUpdatePathAcl}
        $script:realSystemRootCheck = ${function:Assert-AgentUpdateSystemRoot}
        $global:UpdateAclChecks = [Collections.Generic.List[object]]::new()
        function Assert-AgentUpdatePathAcl {
            param($Path,$TrustedSids,$RequireModifySid,$RequireReadOnlySid,$DisallowServiceWriteSid,[switch]$Private)
            $global:UpdateAclChecks.Add([pscustomobject]@{ Path=$Path; Private=[bool]$Private })
        }
        function Assert-AgentUpdateSystemRoot { param($Path) }
        Assert-AgentUpdateTrustedTree -Path $aclRoot -TrustedSids $trusted -ServiceSid $sid -ReadOnly -Private
        $rootCheck = @($global:UpdateAclChecks | Where-Object { $_.Path -eq $aclRoot })
        if ($rootCheck.Count -ne 1 -or -not $rootCheck[0].Private) { throw 'The recovery directory itself must have a private ACL.' }
        if (@($global:UpdateAclChecks | Where-Object { $_.Path -ne $aclRoot -and $_.Private }).Count -ne 0) { throw 'Normal read access on a trusted recovery ancestor must not be treated as a private-data leak.' }
    } catch { $failures.Add($_.Exception.Message) }
    finally {
        if ($script:realAclCheck) { Set-Item Function:\Assert-AgentUpdatePathAcl $script:realAclCheck }
        if ($script:realSystemRootCheck) { Set-Item Function:\Assert-AgentUpdateSystemRoot $script:realSystemRootCheck }
        Remove-Variable UpdateAclChecks -Scope Global -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $aclRoot) { Remove-Item -LiteralPath $aclRoot -Recurse -Force }
    }
    if ($failures.Count) { throw ($failures -join "`n") }
    Write-Output 'Agent update service query and stop tests passed with fake services only.'
} finally {
    Remove-Item Function:\Get-CimInstance -Force
    Remove-Item Function:\Get-Service -Force
    Remove-Item Function:\Stop-Service -Force
    Remove-Item Function:\Get-Acl -Force
    Remove-Variable AgentUpdateServiceTestCalls -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable AgentUpdateTestAcl -Scope Global -ErrorAction SilentlyContinue
}
