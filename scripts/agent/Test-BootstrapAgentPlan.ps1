$ErrorActionPreference = 'Stop'
$bootstrap = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\public\agent\Bootstrap-Agent.ps1'))
$bootstrapText = [IO.File]::ReadAllText($bootstrap)
if ($bootstrapText -match '\$IsWindows') { throw 'Bootstrap must not depend on the PowerShell 7-only IsWindows variable.' }
if (-not $bootstrapText.Contains('Invoke-WebRequest -UseBasicParsing')) { throw 'Bootstrap downloads must support Windows PowerShell 5.1.' }
if ($bootstrapText -match 'GetTempPath|\$env:TEMP') { throw 'Privileged bootstrap content must not be staged under a shared temporary directory.' }
if (-not $bootstrapText.Contains('[ScriptBlock]::Create([IO.File]::ReadAllText($installer))') -or -not $bootstrapText.Contains('-PackageRoot $expanded') -or -not $bootstrapText.Contains('-EnrollmentTicket $EnrollmentTicket')) { throw 'Bootstrap must invoke the hash-verified package installer in memory with its package root and enrollment ticket.' }
if (-not $bootstrapText.Contains('$verifiedHash = Assert-AgentPackageHash -Path $archive -ExpectedSha256 $PackageSha256') -or
    -not $bootstrapText.Contains('New-BootstrapPrivateDirectory $stage $serviceSid -MachineProtected')) {
    throw 'Bootstrap must retain the verified archive digest and create its download/proof/extraction stage with a machine-protected ACL.'
}
$trustedSidStart = $bootstrapText.IndexOf('function Get-BootstrapTrustedSids {', [StringComparison]::Ordinal)
$trustedSidEnd = $bootstrapText.IndexOf('function Get-BootstrapServiceSid {', $trustedSidStart, [StringComparison]::Ordinal)
if ($trustedSidStart -lt 0 -or $trustedSidEnd -le $trustedSidStart -or
    $bootstrapText.Substring($trustedSidStart, $trustedSidEnd - $trustedSidStart).Contains('[Security.Principal.WindowsIdentity]::GetCurrent().User')) {
    throw 'The interactive installer SID must not make a writable install ancestor trusted for machine-protected bootstrap staging.'
}
if (-not $bootstrapText.Contains('if (Test-Path -LiteralPath $productDirectory) {') -or
    -not $bootstrapText.Contains('Assert-BootstrapTrustedPath $productDirectory')) {
    throw 'Runtime bootstrap must reject an existing untrusted product parent instead of repairing or using it.'
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('kr-bootstrap-plan-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $archive = Join-Path $root 'package.zip'
    [IO.File]::WriteAllBytes($archive, [Text.Encoding]::UTF8.GetBytes('test package bytes'))
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToUpperInvariant()
    $controllerUrl = 'https://localhost:7443'
    $packageUrl = 'http://localhost:3000/agent/AgentPackage.zip'
    $returnUrl = 'http://localhost:3000/admin/machines'
    $ticket = 'A' * 43
    $thumbprint = 'A' * 40
    $aclDefinitionStart = $bootstrapText.IndexOf('function New-BootstrapDirectorySecurity {', [StringComparison]::Ordinal)
    $aclDefinitionEnd = $bootstrapText.IndexOf('function New-BootstrapPrivateDirectory {', $aclDefinitionStart, [StringComparison]::Ordinal)
    if ($aclDefinitionStart -lt 0 -or $aclDefinitionEnd -le $aclDefinitionStart) { throw 'Bootstrap directory ACL builder could not be isolated for descriptor testing.' }
    Invoke-Expression $bootstrapText.Substring($aclDefinitionStart, $aclDefinitionEnd - $aclDefinitionStart)
    $interactiveSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $machineStageAcl = New-BootstrapDirectorySecurity -ServiceSid $interactiveSid -MachineProtected
    $machineStageRules = @($machineStageAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $stageRuleBySid = @{}
    foreach ($rule in $machineStageRules) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $rule.IsInherited -or
            $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            throw 'Machine bootstrap stage ACL must contain only explicit inheritable allow rules.'
        }
        $stageRuleBySid[$rule.IdentityReference.Value] = $rule.FileSystemRights
    }
    if (-not $machineStageAcl.AreAccessRulesProtected -or
        $machineStageAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544' -or
        $machineStageRules.Count -ne 3 -or
        $stageRuleBySid['S-1-5-18'] -ne [Security.AccessControl.FileSystemRights]::FullControl -or
        $stageRuleBySid['S-1-5-32-544'] -ne [Security.AccessControl.FileSystemRights]::FullControl -or
        $stageRuleBySid[$interactiveSid.Value] -ne ([Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize)) {
        throw 'Machine bootstrap stage ACL must exclude interactive-user write access while granting service read/execute only.'
    }
    $plan = & $bootstrap -ControllerUrl $controllerUrl -PackageUrl $packageUrl -PackageSha256 ('B' * 64) `
        -TrustedRootThumbprint $thumbprint -EnrollmentTicket $ticket -InstallReturnUrl $returnUrl `
        -VncAllowedSourceAddress '192.168.1.10' -Plan
    if ($plan.ControllerUrl -ne 'https://localhost:7443/') { throw 'Controller URL must normalize to an HTTPS origin.' }
    if ($plan.PackageUrl -ne $packageUrl -or $plan.InstallReturnUrl -ne $returnUrl) { throw 'Loopback package or admin return URL was not preserved.' }
    if ($plan.VncAllowedSourceAddress -ne '192.168.1.10') { throw 'Bootstrap plan must carry the exact LAN VNC source address.' }
    $expectedStageParent = Join-Path $env:ProgramFiles 'KamenRaider'
    if ($plan.StageParent -ne $expectedStageParent) { throw 'Bootstrap staging must stay under the protected Program Files product directory.' }

    $verifiedPlan = & $bootstrap -ControllerUrl $controllerUrl -PackageUrl $packageUrl -PackageSha256 $actualHash `
        -TrustedRootThumbprint $thumbprint -EnrollmentTicket $ticket -InstallReturnUrl $returnUrl `
        -VncAllowedSourceAddress '192.168.1.10' -PackageArchivePath $archive -Plan
    if ($verifiedPlan.VerifiedPackageSha256 -ne $actualHash) { throw 'Plan did not verify the local package SHA-256.' }
    if ($verifiedPlan.VncAllowedSourceAddress -ne '192.168.1.10') { throw 'Bootstrap plan must preserve a configured private Controller address.' }
    $mismatchRejected = $false
    try {
        & $bootstrap -ControllerUrl $controllerUrl -PackageUrl $packageUrl -PackageSha256 ('C' * 64) `
            -TrustedRootThumbprint $thumbprint -EnrollmentTicket $ticket -InstallReturnUrl $returnUrl `
            -VncAllowedSourceAddress '192.168.1.10' -PackageArchivePath $archive -Plan | Out-Null
    } catch { $mismatchRejected = $_.Exception.Message -eq 'Agent package hash mismatch.' }
    if (-not $mismatchRejected) { throw 'A package with a mismatched SHA-256 must be rejected before extraction or service setup.' }

    $invalidCases = @(
        @{ Controller = 'http://localhost:7443'; Package = $packageUrl; Return = $returnUrl; VncSource = '192.168.1.10' },
        @{ Controller = 'https://user@localhost:7443'; Package = $packageUrl; Return = $returnUrl; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443/controller'; Package = $packageUrl; Return = $returnUrl; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443?token=x'; Package = $packageUrl; Return = $returnUrl; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443'; Package = 'http://192.168.1.20:3000/AgentPackage.zip'; Return = $returnUrl; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443'; Package = 'https://user@packages.example/AgentPackage.zip'; Return = $returnUrl; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443'; Package = 'https://packages.example/AgentPackage.zip?token=x'; Return = $returnUrl; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443'; Package = 'http://shop.localhost:3000/AgentPackage.zip'; Return = $returnUrl; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443'; Package = $packageUrl; Return = 'http://192.168.1.20:3000/admin/machines'; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443'; Package = $packageUrl; Return = 'http://shop.localhost:3000/admin/machines'; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443'; Package = $packageUrl; Return = 'http://localhost:3000/admin/machines?token=x'; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443'; Package = $packageUrl; Return = 'https://localhost:3000/admin/machines#setup'; VncSource = '192.168.1.10' },
        @{ Controller = 'https://localhost:7443'; Package = $packageUrl; Return = $returnUrl; VncSource = '203.0.113.10' },
        @{ Controller = 'https://localhost:7443'; Package = $packageUrl; Return = $returnUrl; VncSource = '192.168.1.0/24' },
        @{ Controller = 'https://localhost:7443'; Package = $packageUrl; Return = $returnUrl; VncSource = '::1' },
        @{ Controller = 'https://localhost:7443'; Package = $packageUrl; Return = $returnUrl; VncSource = '127.0.0.1' }
    )
    foreach ($case in $invalidCases) {
        $rejected = $false
        try {
            & $bootstrap -ControllerUrl $case.Controller -PackageUrl $case.Package -PackageSha256 ('B' * 64) `
                -TrustedRootThumbprint $thumbprint -EnrollmentTicket $ticket -InstallReturnUrl $case.Return `
                -VncAllowedSourceAddress $case.VncSource -Plan | Out-Null
        } catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid bootstrap URL was accepted: $($case | ConvertTo-Json -Compress)" }
    }

    if (@(Get-ChildItem -LiteralPath $root -Force).Count -ne 1) { throw 'Plan mode extracted or created install files.' }
    Write-Output 'Bootstrap plan, URL validation and package-hash tests passed; no services or files outside TEMP changed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-bootstrap-plan-*') {
        throw 'Unsafe bootstrap test cleanup path.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
