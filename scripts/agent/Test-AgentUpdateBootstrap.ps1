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
    try { Invoke-AgentUpdateMain -BoundParameters $boundParameters | Out-Null } catch { $mainError = $_.Exception.Message }
    if ($mainError -notmatch 'capability is unavailable|operation is not available|not ready') {
        throw "Bootstrap must stop after the read-only helper probe while start/bind/apply/report are unavailable; got: $mainError"
    }
    if ($script:bootstrapAssetCalls.Count -ne 2 -or
        $script:bootstrapAssetCalls[0] -cne '/agent/Update-Agent.ps1' -or
        $script:bootstrapAssetCalls[1] -cne $helperUrl) {
        throw 'Bootstrap did not fetch exactly the fixed wrapper and server-pinned native helper assets.'
    }
    if ($script:bootstrapHelperCommands.Count -ne 1 -or $script:bootstrapHelperCommands[0] -cne 'inspect') {
        throw 'Bootstrap ran a mutating or unsupported helper operation.'
    }
    if ($script:bootstrapPrivateFileSecurityAssignments.Count -ne 2) {
        throw 'Every staged asset must receive an explicit Administrators-owned private file ACL.'
    }

    $wrapperText = [IO.File]::ReadAllText($wrapperPath)
    foreach ($unsafeToken in @('Update-AgentPackage.ps1','ScriptBlock]::Create','Invoke-Expression','Start-Service','Stop-Service','Expand-Archive')) {
        if ($wrapperText.Contains($unsafeToken)) { throw "Trusted bootstrap contains the unsafe updater token: $unsafeToken" }
    }

    Write-Output ('Trusted Agent update bootstrap contract passed under PowerShell ' + $PSVersionTable.PSVersion + '.')
}
finally {
    foreach ($functionName in @('Get-Acl','Set-AgentUpdatePrivateFileSecurity','Assert-AgentUpdateElevated','Get-NativeAgentMaintenanceRuntime','New-AgentUpdatePrivateStage','Assert-AgentUpdatePrivateStage','Assert-AgentUpdatePrivateAcl','Get-AgentUpdateAssetBytes','Invoke-AgentMaintenanceProcess')) {
        Remove-Item -LiteralPath (Join-Path 'Function:' $functionName) -Force -ErrorAction SilentlyContinue
    }
    Remove-Variable testPrivateAcl -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable testAclByPath -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable bootstrapAssetCalls -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable bootstrapHelperCommands -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable bootstrapPrivateFileSecurityAssignments -Scope Script -ErrorAction SilentlyContinue
    $buildRoot = [IO.Path]::GetFullPath($temporaryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixture.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedFixture) -notmatch '\Akr-agent-update-bootstrap-test-[0-9a-f]{32}\z') {
        throw 'Unsafe trusted bootstrap test cleanup path.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
