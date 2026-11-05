$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-install-plan-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $root 'package'
$agentDirectory = Join-Path $package 'Agent'
New-Item -ItemType Directory -Path $agentDirectory -Force | Out-Null
try {
    foreach ($name in @('Install-AgentPackage.ps1', 'Install-Service.ps1', 'Install-TightVncServer.ps1', 'PrivateInstallFiles.ps1', 'ServiceInstallTransaction.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $package $name)
    }
    . (Join-Path $package 'PrivateInstallFiles.ps1')
    [IO.File]::WriteAllText((Join-Path $agentDirectory 'Agent.exe'), 'test fixture')

    $installerSource = [IO.File]::ReadAllText((Join-Path $package 'Install-AgentPackage.ps1'))
    if (-not $installerSource.Contains('[string]$EnrollmentTicket') -or -not $installerSource.Contains('-EnrollmentTicket $EnrollmentTicket')) { throw 'Agent installer must pass the issued ticket to the service installer without putting it in appsettings.' }
    if (-not $installerSource.Contains('Get-AgentInstallMode') -or -not $installerSource.Contains('Invoke-AgentEnrollmentRepair')) { throw 'An existing protected Agent installation must have a ticket-renewal recovery path.' }
    if ($installerSource.IndexOf('Invoke-AgentEnrollmentRepair', [StringComparison]::Ordinal) -gt $installerSource.IndexOf('$dotnetInterfaces', [StringComparison]::Ordinal)) { throw 'Enrollment repair must be selected before new-install network adapter discovery.' }
    if (-not $installerSource.Contains('Get-AgentEnrollmentWaitState -ServiceStatus $service.Status.ToString()')) { throw 'Installer must inspect Windows service state throughout the enrollment receipt wait.' }
    $repairBranch = $installerSource.IndexOf("if (`$installMode -eq 'repair-enrollment')", [StringComparison]::Ordinal)
    if ($repairBranch -lt 0) { throw 'Agent installer is missing the enrollment repair branch.' }
    $repairEnrollment = $installerSource.IndexOf('Invoke-AgentEnrollmentRepair -ServiceName', $repairBranch, [StringComparison]::Ordinal)
    $repairVnc = $installerSource.IndexOf('Invoke-AgentVncSetup -InstallerPath', $repairBranch, [StringComparison]::Ordinal)
    $repairReturn = $installerSource.IndexOf('Open-AgentInstallReturnPage -Url', $repairBranch, [StringComparison]::Ordinal)
    if ($repairBranch -lt 0 -or $repairEnrollment -lt $repairBranch -or $repairVnc -lt $repairEnrollment -or $repairReturn -lt $repairVnc) {
        throw 'Enrollment repair must finish the Agent ticket renewal and VNC setup before opening the admin page.'
    }
    if (-not $installerSource.Contains('Get-AgentVncSetupDecision -EnrollmentStatus $enrollmentStatus') -or
        -not $installerSource.Contains('TightVNC setup was skipped because Agent enrollment is still pending.')) {
        throw 'Enrollment repair must skip VNC setup until an enrollment receipt exists.'
    }
    $freshEnrollmentReceipt = $installerSource.IndexOf("`$receiptPath = Join-Path `$dataDirectory 'enrollment-receipt.json'", [StringComparison]::Ordinal)
    if ($freshEnrollmentReceipt -lt 0) { throw 'Agent installer is missing the fresh enrollment receipt wait.' }
    $freshVnc = $installerSource.IndexOf('Invoke-AgentVncSetup -InstallerPath', $freshEnrollmentReceipt, [StringComparison]::Ordinal)
    $freshReturn = $installerSource.IndexOf('Open-AgentInstallReturnPage -Url', $freshVnc, [StringComparison]::Ordinal)
    if ($freshEnrollmentReceipt -lt 0 -or $freshVnc -lt $freshEnrollmentReceipt -or $freshReturn -lt $freshVnc) {
        throw 'Fresh installation must complete its enrollment wait and VNC setup before opening the admin page.'
    }
    if (-not $installerSource.Contains('Get-AgentVncSetupDecision -EnrollmentStatus $receiptStatus')) { throw 'Fresh installation must skip VNC setup while enrollment is pending.' }
    if (-not $installerSource.Contains('$serviceCreatedByThisRun = $true') -or -not $installerSource.Contains('Get-AgentEnrollmentRecoveryInstruction')) { throw 'Installer must provide a supported recovery path when its service stops during enrollment.' }
    $shouldProcessPosition = $installerSource.IndexOf('$PSCmdlet.ShouldProcess', [StringComparison]::Ordinal)
    $firstMutationPosition = $installerSource.IndexOf('New-Item -ItemType Directory', [StringComparison]::Ordinal)
    if ($shouldProcessPosition -lt 0 -or $firstMutationPosition -lt 0 -or $shouldProcessPosition -gt $firstMutationPosition) {
        throw 'Install-AgentPackage must honor WhatIf before creating any install directories.'
    }

    $candidateOne = [pscustomobject]@{ Id = '{A078E8A5-40F5-42AB-B53C-B38F49ECC4DA}'; Name = 'Ethernet'; Address = '192.168.1.20' }
    $candidateTwo = [pscustomobject]@{ Id = '{FCBD4073-6BD1-4F18-A904-85B26BC9F26C}'; Name = 'Wi-Fi'; Address = '192.168.1.21' }
    $singleSelection = Resolve-AgentNetworkAdapter -Candidates @($candidateOne) -ReadSelection { '1' }
    if ($singleSelection -cne $candidateOne.Id) { throw 'Adapter selection must preserve the exact case of the .NET NetworkInterface.Id value.' }
    $interactiveSelection = Resolve-AgentNetworkAdapter -Candidates @($candidateOne, $candidateTwo) -ReadSelection { '2' }
    if ($interactiveSelection -cne $candidateTwo.Id) { throw 'Multiple active adapters must preserve the selected .NET NetworkInterface.Id value.' }
    $explicitSelection = Resolve-AgentNetworkAdapter -Candidates @($candidateOne, $candidateTwo) -RequestedId $candidateOne.Id
    if ($explicitSelection -cne $candidateOne.Id) { throw 'Explicit adapter selection must resolve to the exact .NET NetworkInterface.Id value.' }
    $configurationJson = New-AgentInstallConfiguration -ControllerUrl 'https://controller.example/' `
        -DataDirectory 'C:\ProgramData\KamenRaider\Agent' -MoonlightPort 47989 -SunshinePort 47990 -NetworkAdapterId $candidateOne.Id
    $configuration = $configurationJson | ConvertFrom-Json
    if ($configuration.ControllerUrl -ne 'https://controller.example/' -or $configuration.DataDirectory -ne 'C:\ProgramData\KamenRaider\Agent' -or
        $configuration.MoonlightPort -ne 47989 -or $configuration.SunshinePort -ne 47990 -or $configuration.NetworkAdapterId -ne $candidateOne.Id) {
        throw 'Generated appsettings JSON does not preserve the selected controller, ports, data path and adapter.'
    }

    $installer = Join-Path $package 'Install-AgentPackage.ps1'
    $adapterId = $candidateOne.Id
    $enrollmentTicket = 'A' * 43
    $plan = & $installer -ControllerUrl 'https://localhost:7443' -TrustedRootThumbprint ('A' * 40) `
        -NetworkAdapterId $adapterId -EnrollmentTicket $enrollmentTicket -InstallReturnUrl 'http://localhost:3000/admin/machines' `
        -VncAllowedSourceAddress '127.0.0.1' -Plan

    $memoryInstaller = [ScriptBlock]::Create($installerSource)
    $memoryPlan = & $memoryInstaller -PackageRoot $package -ControllerUrl 'https://localhost:7443' `
        -TrustedRootThumbprint ('A' * 40) -NetworkAdapterId $adapterId -EnrollmentTicket $enrollmentTicket `
        -InstallReturnUrl 'http://localhost:3000/admin/machines' -VncAllowedSourceAddress '127.0.0.1' -Plan
    if ($memoryPlan.InstallDirectory -ne $plan.InstallDirectory) { throw 'In-memory installer did not honor its explicit PackageRoot.' }

    if ($plan.ControllerUrl -ne 'https://localhost:7443/') { throw 'Controller URL must be normalized as an HTTPS origin.' }
    if ($plan.NetworkAdapterId -ne $adapterId) { throw 'Explicit network adapter selection was not preserved.' }
    if ($plan.VncSetup.Version -ne '2.8.90' -or $plan.VncSetup.Port -ne 5900 -or $plan.VncSetup.AllowedSourceAddress -ne '127.0.0.1' -or
        -not $plan.VncSetup.RemoteInputBlocked -or $plan.VncSetup.FirewallProfile -ne 'Private') { throw 'Agent install plan must include read-only VNC and exact Controller firewall intent.' }
    if ($plan.PSObject.Properties.Name -contains 'VncPassword' -or $plan.VncSetup.PSObject.Properties.Name -contains 'Password') { throw 'Agent install plan must not expose a VNC credential.' }
    if ($plan.MoonlightPort -ne 47989 -or $plan.SunshinePort -ne 47990) { throw 'Initial stream ports are incorrect.' }
    if ($plan.Configuration.ControllerUrl -ne 'https://localhost:7443/' -or $plan.Configuration.DataDirectory -ne $plan.DataDirectory -or
        $plan.Configuration.MoonlightPort -ne 47989 -or $plan.Configuration.SunshinePort -ne 47990 -or $plan.Configuration.NetworkAdapterId -ne $adapterId) {
        throw 'Agent appsettings plan omitted or changed a required configuration value.'
    }
    if ($plan.ServiceStartup -ne 'auto' -or -not $plan.StartAfterInstall) { throw 'Agent service must start automatically after installation.' }
    if ($plan.EnrollmentWaitSeconds -ne 90) { throw 'Enrollment wait must be bounded at 90 seconds.' }
    if (-not $plan.EnrollmentTicketStored -or $plan.PSObject.Properties.Name -contains 'EnrollmentTicket' -or $plan.Configuration.PSObject.Properties.Name -contains 'EnrollmentTicket') { throw 'Plan must show a ticket will be stored privately without returning the ticket in configuration or output.' }
    if ((Get-AgentEnrollmentWaitState -ServiceStatus 'Stopped') -ne 'unavailable') { throw 'Enrollment wait must fail if the service stops.' }
    if ((Get-AgentEnrollmentWaitState -ServiceStatus 'Running' -ReceiptStatus ([pscustomobject]@{ State = 'enrolled' })) -ne 'enrolled') { throw 'Enrollment wait must recognize an enrolled receipt while the service is running.' }
    if ((Get-AgentEnrollmentWaitState -ServiceStatus 'Running') -ne 'pending') { throw 'Enrollment wait must remain pending while the service is running without a receipt.' }
    if ((Get-AgentEnrollmentWaitState -ServiceStatus 'StopPending' -ReceiptStatus ([pscustomobject]@{ State = 'enrolled' })) -ne 'unavailable') { throw 'A stopping service must not be reported as enrolled.' }
    if ((Get-AgentEnrollmentWaitState -ServiceStatus 'Paused' -ReceiptStatus ([pscustomobject]@{ State = 'enrolled' })) -ne 'unavailable') { throw 'A paused service must not be reported as enrolled.' }
    if ((Get-AgentEnrollmentWaitState -ServiceStatus 'StartPending' -ReceiptStatus ([pscustomobject]@{ State = 'enrolled' })) -ne 'pending') { throw 'A starting service must wait until it is running before accepting an enrollment receipt.' }
    if ((Get-AgentEnrollmentWaitState -ServiceStatus 'Unknown' -ReceiptStatus ([pscustomobject]@{ State = 'enrolled' })) -ne 'unavailable') { throw 'Unknown service states must not be reported as enrolled.' }
    $enrollmentFixtureDirectory = Join-Path $package 'enrollment'
    New-Item -ItemType Directory -Path $enrollmentFixtureDirectory | Out-Null
    $pendingEnrollment = Get-AgentEnrollmentStatusFromDataDirectory -DataDirectory $enrollmentFixtureDirectory
    if ((Get-AgentVncSetupDecision -EnrollmentStatus $pendingEnrollment) -ne 'SkipPendingEnrollment') { throw 'VNC setup must be skipped when no enrollment receipt exists.' }
    $receiptFixture = [pscustomobject]@{ MachineId = 'fixture-machine'; ApprovalStatus = 'pending'; PublicKeyFingerprint = 'A' * 64 }
    [IO.File]::WriteAllText((Join-Path $enrollmentFixtureDirectory 'enrollment-receipt.json'), (ConvertTo-Json -InputObject $receiptFixture -Depth 3))
    $enrolledStatus = Get-AgentEnrollmentStatusFromDataDirectory -DataDirectory $enrollmentFixtureDirectory
    if ((Get-AgentVncSetupDecision -EnrollmentStatus $enrolledStatus) -ne 'Configure') { throw 'VNC setup must be allowed after a valid enrollment receipt exists.' }
    if ((Get-AgentEnrollmentRecoveryInstruction 'Stopped') -notmatch 'Start-Service.*Do not rerun the installer') { throw 'A stopped Agent service must show its supported recovery command.' }
    if ((Get-AgentEnrollmentRecoveryInstruction 'Paused') -notmatch 'Resume-Service.*Do not rerun the installer') { throw 'A paused Agent service must show its supported recovery command.' }
    if ((Get-AgentInstallMode -ServiceExists $false -InstallDirectoryExists $false -DataDirectoryExists $false) -ne 'install') { throw 'A clean machine must use a new installation.' }
    if ((Get-AgentInstallMode -ServiceExists $true -InstallDirectoryExists $true -DataDirectoryExists $true) -ne 'repair-enrollment') { throw 'An existing service must use the enrollment-ticket repair path.' }
    $partialInstallRejected=$false;try{Get-AgentInstallMode -ServiceExists $true -InstallDirectoryExists $false -DataDirectoryExists $true | Out-Null}catch{$partialInstallRejected=$true}
    if(-not $partialInstallRejected){throw 'A partial installation must not be overwritten by the ticket-repair path.'}
    if ($plan.InstallReturnUrl -ne 'http://localhost:3000/admin/machines') { throw 'Validated return URL was not preserved.' }
    if ($plan.ConfigurationStagingParent -ne $plan.InstallDirectory) { throw 'Configuration staging must stay inside the protected Agent installation directory.' }
    if (Test-Path -LiteralPath $plan.DataDirectory) { throw 'Plan mode created the data directory.' }
    if (Test-Path -LiteralPath (Join-Path $plan.InstallDirectory 'appsettings.json')) { throw 'Plan mode published configuration.' }

    $adapterPlan = & $installer -ControllerUrl 'https://localhost:7443' -TrustedRootThumbprint ('A' * 40) -EnrollmentTicket $enrollmentTicket -Plan
    if ($adapterPlan.NetworkAdapterSelection -ne 'required-at-install') { throw 'Missing adapter must be selected at install time, not guessed.' }
    if ($adapterPlan.VncSetup.AllowedSourceAddress -ne '127.0.0.1') { throw 'Same-machine development must default the VNC source to loopback.' }

    $invalidCases = @(
        @{ ControllerUrl = 'http://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://user@localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443/?x=1'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443/path'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'http://example.com/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://user@localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines?x=1'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines#setup'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'http://192.168.1.10:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'http://shop.localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://controller.example:7443'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId; VncSource = '203.0.113.10' },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId; VncSource = '192.168.1.0/24' },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = $adapterId; VncSource = '::1' },
        @{ ControllerUrl = 'https://localhost:7443'; ReturnUrl = 'https://localhost:3000/admin/machines'; AdapterId = 'not-a-guid' }
    )
    foreach ($case in $invalidCases) {
        $rejected = $false
        try {
            & $installer -ControllerUrl $case.ControllerUrl -TrustedRootThumbprint ('A' * 40) -EnrollmentTicket $enrollmentTicket `
                -InstallReturnUrl $case.ReturnUrl -NetworkAdapterId $case.AdapterId -VncAllowedSourceAddress $case.VncSource -Plan | Out-Null
        } catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid install plan input was accepted: $($case | ConvertTo-Json -Compress)" }
    }

    $publish = Join-Path $root 'publish'
    $data = Join-Path $root 'data'
    $config = Join-Path $root 'appsettings.json'
    $servicePlan = & (Join-Path $package 'Install-Service.ps1') -Role Agent -PublishDirectory $publish `
        -DataDirectory $data -ConfigurationFile $config -TrustedRootThumbprint ('A' * 40) -StartAfterInstall -Plan
    if ($servicePlan.Startup -ne 'auto' -or -not $servicePlan.StartAfterInstall) { throw 'Service Plan did not describe automatic start.' }

    if (Test-Path -LiteralPath $root) {
        $unexpected = @(Get-ChildItem -LiteralPath $root -Force -Recurse | Where-Object { $_.FullName -notlike ($root + '\package*') })
        if ($unexpected.Count) { throw 'Install plan created unexpected files outside its test fixture.' }
    }
    # Keep real tree/link checks, but substitute ACL reads for external ancestors
    # and service-owned identity objects so this regression needs no elevation.
    $aclTree = Join-Path $package 'private-data'
    $identityTree = Join-Path $aclTree 'identity'
    $identityFile = Join-Path $identityTree 'identity.json'
    $realServiceSid = Get-ServiceSid 'KamenRaider Agent'
    New-PrivateInstallDirectory $aclTree $realServiceSid | Out-Null
    New-PrivateInstallDirectory $identityTree $realServiceSid | Out-Null
    [IO.File]::WriteAllText($identityFile, '{}')
    $safeAncestorAcl = New-PrivateInstallSecurity $realServiceSid -Directory -ReadOnly
    $identityAcl = New-PrivateInstallSecurity $realServiceSid -Directory
    $identityAcl.SetOwner($realServiceSid)
    $identityFileAcl = New-PrivateInstallSecurity $realServiceSid
    $identityFileAcl.SetOwner($realServiceSid)
    $identityFileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($realServiceSid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow))
    $badAclPath = $null
    $badAcl = $null
    function Get-Acl {
        param([string]$LiteralPath)
        if ([IO.Path]::GetFullPath($LiteralPath).TrimEnd('\') -eq [IO.Path]::GetPathRoot($aclTree).TrimEnd('\')) { return Microsoft.PowerShell.Security\Get-Acl -LiteralPath $LiteralPath }
        if ($LiteralPath -eq $badAclPath) { return $badAcl }
        if ($LiteralPath -eq $identityTree) { return $identityAcl }
        if ($LiteralPath -eq $identityFile) { return $identityFileAcl }
        if ($LiteralPath -eq $aclTree) { return Microsoft.PowerShell.Security\Get-Acl -LiteralPath $LiteralPath }
        return $safeAncestorAcl
    }
    try {
        Assert-TrustedPublishTree $aclTree -ServiceSid $realServiceSid -Private
        $untrustedSid = [Security.Principal.SecurityIdentifier]::new('S-1-1-0')
        foreach ($target in @($aclTree, $identityTree, $identityFile)) {
            foreach ($right in @([Security.AccessControl.FileSystemRights]::WriteData,[Security.AccessControl.FileSystemRights]::ReadData)) {
                $badAcl = if ($target -eq $identityFile) { New-PrivateInstallSecurity $realServiceSid } else { New-PrivateInstallSecurity $realServiceSid -Directory }
                $badAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($untrustedSid,$right,[Security.AccessControl.AccessControlType]::Allow))
                $badAclPath = $target
                $rejected = $false
                try { Assert-TrustedPublishTree $aclTree -ServiceSid $realServiceSid -Private } catch { $rejected = $true }
                if (-not $rejected) { throw "Private service tree accepted untrusted $right on $target" }
            }
        }
    } finally { Remove-Item Function:\Get-Acl -Force }
    Write-Output 'Agent install plan and private service-tree ACL tests passed; no services changed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-install-plan-*') {
        throw 'Unsafe test cleanup path.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
