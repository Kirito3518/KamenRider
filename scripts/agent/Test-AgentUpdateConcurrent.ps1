param([string]$Mode='parent',[string]$FixtureRoot,[string]$Case)
$ErrorActionPreference = 'Stop'

if ($Mode -ne 'parent') {
    $env:ProgramFiles = Join-Path $FixtureRoot $(if ($Mode -eq 'contender' -and $Case -eq 'different-env') { 'ProgramFiles-alt' } else { 'ProgramFiles' })
    . (Join-Path $PSScriptRoot '..\..\public\agent\Update-Agent.ps1') -ControllerUrl 'https://controller.example/' -MachineId ('0' * 32) -OperationId ('1' * 32) -UpdateTicket ('A' * 43) -ReleaseVersion '2.3.4' -TargetVersion '2.3.4' -PackageSha256 ('A' * 64) -TrustedRootThumbprint ('B' * 40) -AdminReturnUrl 'https://admin.example/admin/agent-releases' -DefineFunctionsOnly
    function Assert-AgentUpdateHost { param($Request) }
    # The test root is disposable. Only ancestor checks are mocked; the actual
    # file lock and its ACL/regular-file checks still execute in both processes.
    if ($Case -notin @('unsafe-parent-acl','unsafe-parent-reparse')) { function Assert-AgentUpdateLockParent { param($Path,$TrustedSids) } }
    function Get-AgentUpdateLockParent {
        if ($Case -eq 'unsafe-parent-acl') { return Join-Path $FixtureRoot 'bad-acl\KamenRaider' }
        if ($Case -eq 'unsafe-parent-reparse') { return Join-Path $FixtureRoot 'bad-reparse\KamenRaider' }
        return Join-Path $FixtureRoot 'ProgramData\KamenRaider'
    }
    function Get-AgentUpdateCurrentState { param($Request) if ($Mode -eq 'fail-preflight') { throw 'fake preflight failure' }; return [pscustomobject]@{} }
    function Test-AgentUpdateLocalState { param($Request,$State) return $true }
    function Start-Process { param($FilePath) }
    function New-AgentUpdateActions {
        param($Request,$State)
        $context = [pscustomobject]@{ Mode=$Mode; Case=$Case; Root=$FixtureRoot }
        return [pscustomobject]@{
            Start = {
                [IO.File]::AppendAllText((Join-Path $context.Root ($context.Case + '.events')),($context.Mode + ':start' + "`n"))
            }.GetNewClosure()
            Download = { return [pscustomobject]@{ Stream=[IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes('test package')) } }
            Validate = { param($request,$package) }
            Apply = {
                [IO.File]::AppendAllText((Join-Path $context.Root ($context.Case + '.events')),($context.Mode + ':service' + "`n"))
                [IO.File]::WriteAllText((Join-Path $context.Root ($context.Case + '.stage')),$context.Mode)
                if ($context.Mode -eq 'fail-apply') { throw 'fake application failure' }
                if ($context.Mode -eq 'hold-apply') {
                    [IO.File]::WriteAllText((Join-Path $context.Root ($context.Case + '.ready')),'ready')
                    $timer = [Diagnostics.Stopwatch]::StartNew()
                    while (-not (Test-Path -LiteralPath (Join-Path $context.Root ($context.Case + '.release')))) {
                        if ($timer.Elapsed.TotalSeconds -gt 25) { throw 'Timed out waiting for test release.' }
                        Start-Sleep -Milliseconds 50
                    }
                }
                return [pscustomobject]@{ Outcome='applied'; Message='fake update' }
            }.GetNewClosure()
            Report = {
                param($request,$status)
                [IO.File]::AppendAllText((Join-Path $context.Root ($context.Case + '.events')),($context.Mode + ':report:' + $status + "`n"))
                if ($context.Mode -eq 'hold-report') {
                    [IO.File]::WriteAllText((Join-Path $context.Root ($context.Case + '.ready')),'ready')
                    $timer = [Diagnostics.Stopwatch]::StartNew()
                    while (-not (Test-Path -LiteralPath (Join-Path $context.Root ($context.Case + '.release')))) {
                        if ($timer.Elapsed.TotalSeconds -gt 25) { throw 'Timed out waiting for test release.' }
                        Start-Sleep -Milliseconds 50
                    }
                }
                if ($context.Mode -eq 'fail-report') { throw 'fake status report failure' }
            }.GetNewClosure()
        }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes('test package')
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace('-','') } finally { $hasher.Dispose() }
    $parameters = @{
        ControllerUrl='https://controller.example/'; MachineId='00112233445566778899aabbccddeeff'; OperationId=$(if ($Mode -eq 'contender' -and $Case -eq 'different-operation') { '11112222333344445555666677778888' } else { 'ffeeddccbbaa99887766554433221100' })
        UpdateTicket=$(if ($Mode -eq 'contender') { 'B' * 43 } else { 'A' * 43 }); ReleaseVersion='2.3.4'; TargetVersion='2.3.4'; PackageSha256=$hash
        TrustedRootThumbprint=('B' * 40); AdminReturnUrl='https://admin.example/admin/agent-releases'
    }
    try {
        Invoke-AgentUpdateMain -BoundParameters $parameters | Out-Null
        [IO.File]::WriteAllText((Join-Path $FixtureRoot ($Case + '.' + $Mode + '.result')),'completed')
    } catch {
        [IO.File]::WriteAllText((Join-Path $FixtureRoot ($Case + '.' + $Mode + '.result')),$_.Exception.Message)
    }
    exit 0
}

. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('kr-update-concurrent-' + [guid]::NewGuid().ToString('N'))))
$children = [Collections.Generic.List[Diagnostics.Process]]::new()
function Start-LockTestProcess {
    param([string]$ChildMode,[string]$ChildCase)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Process -Id $PID).Path
    $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Mode "{1}" -FixtureRoot "{2}" -Case "{3}"' -f $PSCommandPath,$ChildMode,$root,$ChildCase
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    $children.Add($process)
    return $process
}
function Wait-LockTestReady {
    param([string]$Path,[Diagnostics.Process]$Process)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path)) {
        if ($Process.HasExited -or $timer.Elapsed.TotalSeconds -gt 15) { throw 'Updater test process exited or did not reach the requested point.' }
        Start-Sleep -Milliseconds 50
    }
}
function Wait-LockTestExit {
    param([Diagnostics.Process]$Process)
    if (-not $Process.WaitForExit(15000)) { throw 'Updater test process did not exit.' }
    if ($Process.ExitCode -ne 0) { throw "Updater test process exited $($Process.ExitCode)." }
}
try {
    $sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    foreach ($parentName in @('ProgramFiles','ProgramFiles-alt','ProgramData')) {
        New-Item -ItemType Directory -Path (Join-Path $root $parentName) -Force | Out-Null
        New-PrivateInstallDirectory -Path (Join-Path $root "$parentName\KamenRaider") -ServiceSid $sid -ReadOnly | Out-Null
    }
    foreach ($caseSpec in @(
        @{ Name='hold-apply'; Mode='hold-apply' },
        @{ Name='hold-report'; Mode='hold-report' },
        @{ Name='different-env'; Mode='hold-apply' },
        @{ Name='different-operation'; Mode='hold-apply' }
    )) {
        $caseName = $caseSpec.Name
        $owner = Start-LockTestProcess -ChildMode $caseSpec.Mode -ChildCase $caseName
        Wait-LockTestReady -Path (Join-Path $root ($caseName + '.ready')) -Process $owner
        $before = [IO.File]::ReadAllText((Join-Path $root ($caseName + '.stage')))
        $contender = Start-LockTestProcess -ChildMode 'contender' -ChildCase $caseName
        Wait-LockTestExit -Process $contender
        $outcome = [IO.File]::ReadAllText((Join-Path $root ($caseName + '.contender.result')))
        $events = [IO.File]::ReadAllText((Join-Path $root ($caseName + '.events')))
        if ($owner.HasExited -or $outcome -notmatch 'another Agent update' -or $events -match 'contender:(start|service|report)' -or [IO.File]::ReadAllText((Join-Path $root ($caseName + '.stage'))) -ne $before) { throw "Concurrent recovery was not blocked before ticket, service or staged-file mutation: $outcome; $events" }
        [IO.File]::WriteAllText((Join-Path $root ($caseName + '.release')),'release')
        Wait-LockTestExit -Process $owner
        $retry = Start-LockTestProcess -ChildMode 'retry' -ChildCase $caseName
        Wait-LockTestExit -Process $retry
        if ([IO.File]::ReadAllText((Join-Path $root ($caseName + '.retry.result'))) -ne 'completed') { throw 'Recovery did not acquire the lock after the first updater exited.' }
    }
    $caseName = 'crash'
    $owner = Start-LockTestProcess -ChildMode 'hold-apply' -ChildCase $caseName
    Wait-LockTestReady -Path (Join-Path $root ($caseName + '.ready')) -Process $owner
    $owner.Kill(); $owner.WaitForExit()
    $retry = Start-LockTestProcess -ChildMode 'retry' -ChildCase $caseName
    Wait-LockTestExit -Process $retry
    if ([IO.File]::ReadAllText((Join-Path $root ($caseName + '.retry.result'))) -ne 'completed') { throw 'A crashed updater did not release its local lock.' }
    foreach ($failure in @(
        @{ Mode='fail-preflight'; Message='fake preflight failure' },
        @{ Mode='fail-apply'; Message='fake application failure' },
        @{ Mode='fail-report'; Message='fake status report failure' }
    )) {
        $caseName = $failure.Mode
        $failed = Start-LockTestProcess -ChildMode $failure.Mode -ChildCase $caseName
        Wait-LockTestExit -Process $failed
        if ([IO.File]::ReadAllText((Join-Path $root ($caseName + '.' + $failure.Mode + '.result'))) -notmatch $failure.Message) { throw "The simulated $($failure.Mode) failure did not occur." }
        $retry = Start-LockTestProcess -ChildMode 'retry' -ChildCase $caseName
        Wait-LockTestExit -Process $retry
        if ([IO.File]::ReadAllText((Join-Path $root ($caseName + '.retry.result'))) -ne 'completed') { throw "The $($failure.Mode) failure did not release its local lock." }
    }

    $lockPath = Join-Path $root 'ProgramData\KamenRaider\.Agent-update.lock'
    Remove-Item -LiteralPath $lockPath -Force
    $outside = Join-Path $root 'hardlink-target.txt'
    [IO.File]::WriteAllText($outside,'unchanged')
    New-Item -ItemType HardLink -Path $lockPath -Target $outside | Out-Null
    $caseName = 'hardlink'
    $unsafe = Start-LockTestProcess -ChildMode 'retry' -ChildCase $caseName
    Wait-LockTestExit -Process $unsafe
    $unsafeOutcome = [IO.File]::ReadAllText((Join-Path $root ($caseName + '.retry.result')))
    if ($unsafeOutcome -notmatch 'regular single-link file' -or (Test-Path -LiteralPath (Join-Path $root ($caseName + '.events'))) -or [IO.File]::ReadAllText($outside) -ne 'unchanged') { throw "A hard-linked lock did not fail closed: $unsafeOutcome" }
    Remove-Item -LiteralPath $lockPath -Force

    [IO.File]::WriteAllText($lockPath,'unchanged')
    $acl = Get-Acl -LiteralPath $lockPath
    $everyone = [Security.Principal.SecurityIdentifier]::new('S-1-1-0')
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($everyone,[Security.AccessControl.FileSystemRights]::Modify,[Security.AccessControl.AccessControlType]::Allow))
    Set-Acl -LiteralPath $lockPath -AclObject $acl
    $caseName = 'unsafe-acl'
    $unsafe = Start-LockTestProcess -ChildMode 'retry' -ChildCase $caseName
    Wait-LockTestExit -Process $unsafe
    $unsafeOutcome = [IO.File]::ReadAllText((Join-Path $root ($caseName + '.retry.result')))
    if ($unsafeOutcome -notmatch 'untrusted writable ACL' -or (Test-Path -LiteralPath (Join-Path $root ($caseName + '.events'))) -or [IO.File]::ReadAllText($lockPath) -ne 'unchanged') { throw "An untrusted lock ACL did not fail closed: $unsafeOutcome" }

    New-Item -ItemType Directory -Path (Join-Path $root 'bad-acl') | Out-Null
    $badParent = Join-Path $root 'bad-acl\KamenRaider'
    New-PrivateInstallDirectory -Path $badParent -ServiceSid $sid -ReadOnly | Out-Null
    $badAcl = New-PrivateInstallSecurity -ServiceSid $sid -Directory -ReadOnly
    $badAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($everyone,[Security.AccessControl.FileSystemRights]::Modify,[Security.AccessControl.AccessControlType]::Allow))
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($badAcl.GetSecurityDescriptorBinaryForm(),0)
    $daclBytes = [byte[]]::new($descriptor.DiscretionaryAcl.BinaryLength)
    $descriptor.DiscretionaryAcl.GetBinaryForm($daclBytes,0)
    Initialize-PrivateFileAclNativeType
    [KamenRaider.Install.NativePrivateFileAclV1]::SetProtectedDacl($badParent,$daclBytes)
    $caseName = 'unsafe-parent-acl'
    $unsafe = Start-LockTestProcess -ChildMode 'retry' -ChildCase $caseName
    Wait-LockTestExit -Process $unsafe
    $unsafeOutcome = [IO.File]::ReadAllText((Join-Path $root ($caseName + '.retry.result')))
    if ($unsafeOutcome -notmatch 'untrusted writable ACL' -or (Test-Path -LiteralPath (Join-Path $root ($caseName + '.events'))) -or (Test-Path -LiteralPath (Join-Path $badParent '.Agent-update.lock'))) { throw "An untrusted lock parent did not fail closed: $unsafeOutcome" }

    New-Item -ItemType Directory -Path (Join-Path $root 'bad-reparse') | Out-Null
    $junction = Join-Path $root 'bad-reparse\KamenRaider'
    New-Item -ItemType Junction -Path $junction -Target (Join-Path $root 'ProgramData\KamenRaider') | Out-Null
    $caseName = 'unsafe-parent-reparse'
    $unsafe = Start-LockTestProcess -ChildMode 'retry' -ChildCase $caseName
    Wait-LockTestExit -Process $unsafe
    $unsafeOutcome = [IO.File]::ReadAllText((Join-Path $root ($caseName + '.retry.result')))
    if ($unsafeOutcome -notmatch 'reparse' -or (Test-Path -LiteralPath (Join-Path $root ($caseName + '.events')))) { throw "A reparse-point lock parent did not fail closed: $unsafeOutcome" }
    [IO.Directory]::Delete($junction)
    Write-Output 'Concurrent Agent updater processes are excluded and locks are released after success, failure and crash.'
} finally {
    foreach ($child in $children) { if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }; $child.Dispose() }
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $root.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($root) -notlike 'kr-update-concurrent-*') { throw 'Unsafe concurrency test cleanup path.' }
    $junction = Join-Path $root 'bad-reparse\KamenRaider'
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
