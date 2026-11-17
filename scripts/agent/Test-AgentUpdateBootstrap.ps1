$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$wrapperPath = Join-Path $repository 'public\agent\Update-Agent.ps1'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixtureRoot = Join-Path $temporaryRoot ('kr-agent-update-bootstrap-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null

function Get-TestSha256Hex {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Bytes)).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function New-TestPeImage {
    param([Parameter(Mandatory)][uint16]$Machine)
    $bytes = New-Object byte[] 512
    $bytes[0] = [byte]0x4d
    $bytes[1] = [byte]0x5a
    [Array]::Copy([BitConverter]::GetBytes([int]0x80),0,$bytes,0x3c,4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]0x00004550),0,$bytes,0x80,4)
    [Array]::Copy([BitConverter]::GetBytes($Machine),0,$bytes,0x84,2)
    return ,$bytes
}

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock]$Action,[Parameter(Mandatory)][string]$Message)
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    if (-not $threw) { throw $Message }
}

function New-TestAncestorAcl {
    param([Parameter(Mandatory)][string]$OwnerSid,[switch]$StandardDriveRoot,[switch]$StandardCommonData)
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true,$false)
    $security.SetOwner([Security.Principal.SecurityIdentifier]::new($OwnerSid))
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($sidValue in @('S-1-5-18','S-1-5-32-544')) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sidValue),[Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    }
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::ReadAndExecute,
        $inheritance,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-3-0'),[Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,[Security.AccessControl.PropagationFlags]::InheritOnly,[Security.AccessControl.AccessControlType]::Allow))
    if ($StandardDriveRoot) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new('S-1-5-11'),[Security.AccessControl.FileSystemRights]::CreateDirectories,
            [Security.AccessControl.InheritanceFlags]::None,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    }
    if ($StandardCommonData) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::Write,
            [Security.AccessControl.InheritanceFlags]::ContainerInherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    }
    return $security
}

try {
    $helperBytes = New-TestPeImage -Machine 0x8664
    $wrapperBytes = [Text.Encoding]::UTF8.GetBytes('Set-StrictMode -Version Latest`n')
    $helperHash = Get-TestSha256Hex -Bytes $helperBytes
    $wrapperHash = Get-TestSha256Hex -Bytes $wrapperBytes
    $helperUrl = '/agent/maintenance/sha256/' + $helperHash.ToLowerInvariant() + '/AgentMaintenance.exe'

    . $wrapperPath `
        -ControllerUrl 'https://controller.example/' `
        -AssetBaseUrl 'https://assets.example/' `
        -MachineId ('0' * 32) `
        -OperationId ('1' * 32) `
        -UpdateTicket ('A' * 43) `
        -ReleaseVersion '2.3.4' `
        -TargetVersion '2.3.4' `
        -TargetRuntime 'win-x64' `
        -PackageSha256 ('B' * 64) `
        -TrustedRootThumbprint ('C' * 40) `
        -AdminReturnUrl 'https://admin.example/admin/agent-releases' `
        -WrapperSha256 $wrapperHash `
        -WrapperLength $wrapperBytes.Length `
        -HelperRelativeUrl $helperUrl `
        -HelperSha256 $helperHash `
        -HelperLength $helperBytes.Length `
        -HelperProtocol 1 `
        -DefineFunctionsOnly

    $expectedBootstrapRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'KamenRaider.AgentMaintenanceBootstrap'
    if (-not [string]::Equals((Get-AgentUpdateBootstrapRoot),$expectedBootstrapRoot,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Bootstrap staging does not use the native CommonApplicationData root.'
    }
    Assert-AgentUpdateTrustedAncestorChain -Path ([IO.Path]::GetDirectoryName($expectedBootstrapRoot))

    Initialize-AgentUpdateNativeFileInfoType
    $linkProbe = Join-Path $fixtureRoot 'single-link-probe.bin'
    $secondLink = Join-Path $fixtureRoot 'second-link-probe.bin'
    [IO.File]::WriteAllText($linkProbe,'private bootstrap staging fixture')
    $script:testPrivateAcl = New-AgentUpdatePrivateFileSecurity
    $script:testAclByPath = @{}
    function Get-Acl {
        param($LiteralPath,$ErrorAction)
        if ($script:testAclByPath.ContainsKey($LiteralPath)) { return $script:testAclByPath[$LiteralPath] }
        return $script:testPrivateAcl
    }
    Assert-AgentUpdatePrivateAcl -Path $linkProbe
    New-Item -ItemType HardLink -Path $secondLink -Target $linkProbe | Out-Null
    Assert-Throws { Assert-AgentUpdatePrivateAcl -Path $linkProbe } 'Bootstrap accepted a staged file with multiple hard links.'

    $interactiveSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $privateOwners = @(Get-AgentUpdatePrivateOwnerSids | ForEach-Object { $_.Value })
    if ($interactiveSid -notin @('S-1-5-18','S-1-5-32-544') -and $interactiveSid -in $privateOwners) {
        throw 'The interactive administrator SID is trusted as a protected staging owner.'
    }
    $script:testPrivateAcl.SetOwner([Security.Principal.SecurityIdentifier]::new($interactiveSid))
    Assert-Throws { Assert-AgentUpdatePrivateAcl -Path $linkProbe } 'Bootstrap accepted a file owned by the interactive administrator SID.'
    $script:testPrivateAcl = New-AgentUpdatePrivateFileSecurity

    $privateFileSecurity = New-AgentUpdatePrivateFileSecurity
    if ($privateFileSecurity.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne 'S-1-5-32-544' -or
        -not $privateFileSecurity.AreAccessRulesProtected) {
        throw 'Bootstrap staged files must have protected ACLs and Administrators ownership.'
    }

    $script:testAclByPath['C:\'] = New-TestAncestorAcl -OwnerSid 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464' -StandardDriveRoot
    $script:testAclByPath['C:\ProgramData'] = New-TestAncestorAcl -OwnerSid 'S-1-5-18' -StandardCommonData
    Assert-AgentUpdateTrustedAncestorChain -Path 'C:\ProgramData\KamenRaider.AgentMaintenanceBootstrap'

    $unsafeProgramDataAcl = New-TestAncestorAcl -OwnerSid 'S-1-5-18' -StandardCommonData
    $unsafeProgramDataAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::Modify,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow))
    $script:testAclByPath['C:\ProgramData'] = $unsafeProgramDataAcl
    Assert-Throws { Assert-AgentUpdateTrustedAncestorChain -Path 'C:\ProgramData\KamenRaider.AgentMaintenanceBootstrap' } 'Bootstrap accepted untrusted ancestor write/delete/change-ACL rights.'

    $script:testAclByPath['C:\ProgramData'] = New-TestAncestorAcl -OwnerSid 'S-1-5-21-1111-2222-3333-4444'
    Assert-Throws { Assert-AgentUpdateTrustedAncestorChain -Path 'C:\ProgramData\KamenRaider.AgentMaintenanceBootstrap' } 'Bootstrap accepted an untrusted ancestor owner.'
    $script:testAclByPath.Clear()

    $valid = [pscustomobject]@{
        ControllerUrl = 'https://controller.example/'
        AssetBaseUrl = 'https://assets.example/'
        MachineId = '00112233445566778899aabbccddeeff'
        OperationId = 'ffeeddccbbaa99887766554433221100'
        ChainId = 'ffeeddccbbaa99887766554433221100'
        HeadOperationId = 'ffeeddccbbaa99887766554433221100'
        Generation = 1
        EnrollmentKeyFingerprint = 'D' * 64
        PackageLength = 1048576
        UpdateTicket = 'A' * 43
        ReleaseVersion = '2.3.4'
        TargetVersion = '2.3.4'
        TargetRuntime = 'win-x64'
        PackageSha256 = 'B' * 64
        TrustedRootThumbprint = 'C' * 40
        AdminReturnUrl = 'https://admin.example/admin/agent-releases'
        WrapperSha256 = $wrapperHash
        WrapperLength = $wrapperBytes.Length
        HelperRelativeUrl = $helperUrl
        HelperSha256 = $helperHash
        HelperLength = $helperBytes.Length
        HelperProtocol = 1
    }

    $normalized = Test-AgentUpdateRequest -Request $valid
    if ($normalized.HelperRelativeUrl -cne $helperUrl -or $normalized.HelperSha256 -cne $helperHash) {
        throw 'Valid deployment-pinned helper metadata was not preserved.'
    }

    foreach ($case in @(
        @{ Name='arbitrary helper URL'; Property='HelperRelativeUrl'; Value='/agent/maintenance/other.exe' },
        @{ Name='mismatched helper digest'; Property='HelperSha256'; Value=('D' * 64) },
        @{ Name='missing helper length'; Property='HelperLength'; Value=0 },
        @{ Name='helper above shared 150 MiB limit'; Property='HelperLength'; Value=157286401 },
        @{ Name='unsupported helper protocol'; Property='HelperProtocol'; Value=2 },
        @{ Name='non-origin asset base'; Property='AssetBaseUrl'; Value='https://assets.example/unpinned' },
        @{ Name='unsupported target runtime'; Property='TargetRuntime'; Value='win-ia64' }
    )) {
        $bad = $valid.PSObject.Copy()
        $bad.($case.Property) = $case.Value
        Assert-Throws { Test-AgentUpdateRequest -Request $bad } "Accepted $($case.Name)."
    }

    $script:bootstrapAssetCalls = [Collections.Generic.List[string]]::new()
    $script:bootstrapHelperCommands = [Collections.Generic.List[string]]::new()
    function Assert-AgentUpdateElevated { param($Request) }
    function Get-NativeAgentMaintenanceRuntime { return 'win-x64' }
    function New-AgentUpdatePrivateStage {
        param($Request)
        $path = Join-Path $fixtureRoot $Request.OperationId
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }
    function Assert-AgentUpdatePrivateStage { param($Path) }
    function Assert-AgentUpdatePrivateAcl { param($Path,[switch]$Directory) }
    $script:bootstrapPrivateFileSecurityAssignments = [Collections.Generic.List[string]]::new()
    function Set-AgentUpdatePrivateFileSecurity { param($Path) $script:bootstrapPrivateFileSecurityAssignments.Add($Path) }
    function Get-AgentUpdateAssetBytes {
        param($Request,$RelativeUrl,$ExpectedLength)
        $script:bootstrapAssetCalls.Add($RelativeUrl)
        if ($RelativeUrl -eq '/agent/Update-Agent.ps1') { return ,$wrapperBytes }
        if ($RelativeUrl -eq $helperUrl) { return ,$helperBytes }
        throw "Unexpected non-pinned asset request: $RelativeUrl"
    }
    function Invoke-AgentMaintenanceProcess {
        param($Path,$Verb)
        $script:bootstrapHelperCommands.Add($Verb)
        if ($Verb -cne 'inspect') { throw "Unexpected helper operation: $Verb" }
        if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $helperHash) { throw 'Helper file changed before launch.' }
        return [pscustomobject]@{
            ExitCode = 0
            StandardOutput = '{"status":"ok","source":{"version":"1.2.3"}}'
            StandardError = ''
        }
    }

    $script:bootstrapWorkRequests = [Collections.Generic.List[object]]::new()
    $script:bootstrapPackageSaves = [Collections.Generic.List[string]]::new()
    function Initialize-AgentUpdateWorkStore {
        return [pscustomobject]@{
            Root = $fixtureRoot
            Work = $fixtureRoot
            Request = (Join-Path $fixtureRoot 'request.json')
            Archive = (Join-Path $fixtureRoot 'target.zip')
        }
    }
    function Write-AgentUpdateProtectedJson {
        param($Path,$Value)
        $script:bootstrapWorkRequests.Add($Value)
    }
    function Save-AgentUpdatePackage {
        param($Request,$Destination)
        $script:bootstrapPackageSaves.Add($Destination)
    }
    function Invoke-AgentMaintenanceCommand {
        param($Path,$Verb,[int]$TimeoutSeconds = 600)
        $script:bootstrapHelperCommands.Add($Verb)
        $outcome = if ($Verb -ceq 'apply') { 'applied' } else { $null }
        return [pscustomobject]@{
            Verb = $Verb
            ExitCode = 0
            Result = [pscustomobject]@{ status = 'ok'; category = $null; applyOutcome = $outcome }
            StandardError = ''
        }
    }

    $boundParameters = @{}
    foreach ($property in $valid.PSObject.Properties) { $boundParameters[$property.Name] = $property.Value }
    $wrongRuntime = $valid.PSObject.Copy()
    $wrongRuntime.TargetRuntime = 'win-arm64'
    $wrongRuntimeParameters = @{}
    foreach ($property in $wrongRuntime.PSObject.Properties) { $wrongRuntimeParameters[$property.Name] = $property.Value }
    $wrongRuntimeError = $null
    try { Invoke-AgentUpdateMain -BoundParameters $wrongRuntimeParameters | Out-Null }
    catch { $wrongRuntimeError = $_.Exception.Message }
    if ($wrongRuntimeError -notmatch 'native Windows architecture' -or $script:bootstrapAssetCalls.Count -ne 0 -or $script:bootstrapHelperCommands.Count -ne 0) {
        throw 'Wrong target runtime was not rejected before fetching or invoking the helper.'
    }

    $mainError = $null
    $mainOutput = @()
    try { $mainOutput = @(Invoke-AgentUpdateMain -BoundParameters $boundParameters) } catch { $mainError = $_.Exception.Message }
    if ($mainError) { throw "Trusted bootstrap main flow failed before its fail-closed boundary: $mainError" }
    if (-not $mainOutput -or (($mainOutput | Out-String) -notmatch '"status":"ok"')) {
        throw 'Bootstrap did not complete the helper-driven update workflow.'
    }
    if ($script:bootstrapAssetCalls.Count -ne 2 -or
        $script:bootstrapAssetCalls[0] -cne '/agent/Update-Agent.ps1' -or
        $script:bootstrapAssetCalls[1] -cne $helperUrl) {
        throw 'Bootstrap did not fetch exactly the fixed wrapper and server-pinned native helper assets.'
    }
    $expectedHelperCommands = @('inspect','start','extract','bind-source','apply','report')
    if (($script:bootstrapHelperCommands -join ',') -cne ($expectedHelperCommands -join ',')) {
        throw ('Bootstrap invoked an unexpected helper command sequence: ' + ($script:bootstrapHelperCommands -join ','))
    }
    $requestCommands = @($script:bootstrapWorkRequests | ForEach-Object { [string]$_.command })
    if (($requestCommands -join ',') -cne 'start,extract,bind-source,apply,report') {
        throw ('Bootstrap wrote an unexpected helper request sequence: ' + ($requestCommands -join ','))
    }
    if ($script:bootstrapWorkRequests[0].chainId -cne $valid.ChainId -or
        $script:bootstrapWorkRequests[0].generation -ne 1 -or
        $script:bootstrapWorkRequests[0].enrollmentKeyFingerprint -cne $valid.EnrollmentKeyFingerprint -or
        $script:bootstrapWorkRequests[0].controllerOrigin -cne $valid.ControllerUrl) {
        throw 'Bootstrap lifecycle request does not carry the Controller-bound operation identity.'
    }
    if ($script:bootstrapWorkRequests[2].helperBuildSha256 -cne $helperHash) {
        throw 'Bootstrap source-binding request is not pinned to the verified helper build.'
    }
    if ($script:bootstrapWorkRequests[3].targetPackageLength -ne $valid.PackageLength -or
        $script:bootstrapWorkRequests[3].headOperationId -cne $valid.HeadOperationId) {
        throw 'Bootstrap apply request does not carry the bound package and maintenance head.'
    }
    if ($script:bootstrapPackageSaves.Count -ne 1) {
        throw 'Bootstrap did not download exactly one pinned package into the protected work store.'
    }
    if ($script:bootstrapPrivateFileSecurityAssignments.Count -ne 3) {
        throw 'Every staged asset and the workflow lock must receive an explicit Administrators-owned private file ACL.'
    }

    # An ambiguous Controller start must never be retried blindly: the helper reconcile
    # command runs and the wrapper still fails closed.
    $script:bootstrapHelperCommands.Clear()
    $script:bootstrapWorkRequests.Clear()
    $ambiguousStage = Join-Path $fixtureRoot $valid.OperationId
    if (Test-Path -LiteralPath $ambiguousStage) { Remove-Item -LiteralPath $ambiguousStage -Recurse -Force }
    function Invoke-AgentMaintenanceCommand {
        param($Path,$Verb,[int]$TimeoutSeconds = 600)
        $script:bootstrapHelperCommands.Add($Verb)
        if ($Verb -ceq 'start') {
            return [pscustomobject]@{ Verb='start'; ExitCode=3; Result=[pscustomobject]@{ status='unavailable'; category='controllerStartUnconfirmed' }; StandardError='' }
        }
        return [pscustomobject]@{ Verb=$Verb; ExitCode=0; Result=[pscustomobject]@{ status='ok'; category=$null; applyOutcome=$null }; StandardError='' }
    }
    $ambiguousError = $null
    try { Invoke-AgentUpdateMain -BoundParameters $boundParameters | Out-Null } catch { $ambiguousError = $_.Exception.Message }
    if ($ambiguousError -notmatch 'controllerStartUnconfirmed' -or
        ($script:bootstrapHelperCommands -join ',') -cne 'inspect,start,reconcile') {
        throw ('Bootstrap did not reconcile and fail closed after an unconfirmed Controller start. error=' +
            [string]$ambiguousError + ' commands=' + ($script:bootstrapHelperCommands -join ','))
    }

    $wrapperText = [IO.File]::ReadAllText($wrapperPath)
    foreach ($unsafeToken in @('Update-AgentPackage.ps1','ScriptBlock]::Create','Invoke-Expression','Start-Service','Stop-Service','Expand-Archive')) {
        if ($wrapperText.Contains($unsafeToken)) { throw "Trusted bootstrap contains the unsafe updater token: $unsafeToken" }
    }

    Write-Output ('Trusted Agent update bootstrap contract passed under PowerShell ' + $PSVersionTable.PSVersion + '.')
}
finally {
    foreach ($functionName in @('Get-Acl','Set-AgentUpdatePrivateFileSecurity','Assert-AgentUpdateElevated','Get-NativeAgentMaintenanceRuntime','New-AgentUpdatePrivateStage','Assert-AgentUpdatePrivateStage','Assert-AgentUpdatePrivateAcl','Get-AgentUpdateAssetBytes','Invoke-AgentMaintenanceProcess','Initialize-AgentUpdateWorkStore','Write-AgentUpdateProtectedJson','Save-AgentUpdatePackage','Invoke-AgentMaintenanceCommand','Enter-AgentUpdateWorkflowLock')) {
        Remove-Item -LiteralPath (Join-Path 'Function:' $functionName) -Force -ErrorAction SilentlyContinue
    }
    Remove-Variable testPrivateAcl -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable testAclByPath -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable bootstrapAssetCalls -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable bootstrapHelperCommands -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable bootstrapPrivateFileSecurityAssignments -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable bootstrapWorkRequests -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable bootstrapPackageSaves -Scope Script -ErrorAction SilentlyContinue
    $buildRoot = [IO.Path]::GetFullPath($temporaryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixture.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedFixture) -notmatch '\Akr-agent-update-bootstrap-test-[0-9a-f]{32}\z') {
        throw 'Unsafe trusted bootstrap test cleanup path.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
