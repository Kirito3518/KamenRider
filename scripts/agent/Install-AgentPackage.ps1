[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)][string]$ControllerUrl,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$TrustedRootThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{43}$')][string]$EnrollmentTicket,
    [ValidateRange(1, 65535)][int]$MoonlightPort = 47989,
    [ValidateRange(1, 65535)][int]$SunshinePort = 47990,
    [string]$NetworkAdapterId,
    [string]$InstallReturnUrl,
    [string]$VncAllowedSourceAddress,
    [string]$PackageRoot,
    [string]$BootstrapProvenancePath,
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'

function ConvertTo-AgentVncSourceAddress {
    param([Parameter(Mandatory)][Uri]$Controller,[string]$Address)
    $candidate = $Address
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        if ($Controller.IsLoopback) { throw 'When ControllerUrl uses localhost, VncAllowedSourceAddress must be the Controller LAN IPv4 seen by the VNC target; the gateway connects to the confirmed LAN IP, not loopback.' }
        $candidate = $Controller.DnsSafeHost
    }
    $parsed = $null
    if (-not [Net.IPAddress]::TryParse($candidate, [ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'VncAllowedSourceAddress must be one RFC1918 private IPv4 address; configure it when ControllerUrl uses loopback or a hostname.'
    }
    $bytes = $parsed.GetAddressBytes()
    $private = $bytes[0] -eq 10 -or
        ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
    if (-not $private -or $parsed.ToString() -cne $candidate) { throw 'VncAllowedSourceAddress must be one RFC1918 private IPv4 address.' }
    return $parsed.ToString()
}

function Invoke-AgentVncSetup {
    param([Parameter(Mandatory)][string]$InstallerPath,[Parameter(Mandatory)][string]$AllowedSourceAddress,[Parameter(Mandatory)][string]$DataDirectory)
    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw 'TightVNC installer is missing from the Agent package.' }
    $installerBlock = [ScriptBlock]::Create([IO.File]::ReadAllText($InstallerPath))
    & $installerBlock -AllowedSourceAddress $AllowedSourceAddress -DataDirectory $DataDirectory -Confirm:$false
}

function Open-AgentInstallReturnPage {
    param([string]$Url)
    if ($Url) {
        try { Start-Process -FilePath $Url }
        catch { Write-Warning 'Agent installation completed, but the admin page could not be opened automatically.' }
    }
}

function Invoke-LegacyStreamGuardMigration {
    param(
        [Parameter(Mandatory)][string]$InstallerPath,
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string]$BackupDirectory
    )
    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw 'Remove-StreamGuard.ps1 is missing from the Agent package.' }
    $installerBlock = [ScriptBlock]::Create([IO.File]::ReadAllText($InstallerPath))
    return & $installerBlock -InstallScriptRoot $PackageRoot -BackupDirectory $BackupDirectory -Confirm:$false
}

$effectivePackageRoot = if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PSScriptRoot } else { $PackageRoot }
if ([string]::IsNullOrWhiteSpace($effectivePackageRoot) -or -not [IO.Path]::IsPathRooted($effectivePackageRoot)) { throw 'PackageRoot must identify the absolute Agent package directory.' }
$effectivePackageRoot = [IO.Path]::GetFullPath($effectivePackageRoot)

try { $controller = [Uri]::new($ControllerUrl, [UriKind]::Absolute) }
catch { throw 'ControllerUrl must be an absolute HTTPS origin.' }
if ($controller.Scheme -ne 'https' -or $controller.UserInfo -or $controller.Query -or $controller.Fragment -or $controller.AbsolutePath -ne '/') {
    throw 'ControllerUrl must be an HTTPS origin without credentials, path, query or fragment.'
}
$controllerUrlNormalized = $controller.GetLeftPart([UriPartial]::Authority) + '/'
$vncAllowedSourceAddress = ConvertTo-AgentVncSourceAddress -Controller $controller -Address $VncAllowedSourceAddress

$returnUrlNormalized = $null
if (-not [string]::IsNullOrWhiteSpace($InstallReturnUrl)) {
    try { $returnUri = [Uri]::new($InstallReturnUrl, [UriKind]::Absolute) }
    catch { throw 'InstallReturnUrl must be an absolute URL to /admin/machines.' }
    $loopbackHttp = $returnUri.Scheme -eq 'http' -and $returnUri.IsLoopback
    if ((($returnUri.Scheme -ne 'https') -and -not $loopbackHttp) -or $returnUri.UserInfo -or $returnUri.Query -or $returnUri.Fragment -or $returnUri.AbsolutePath -ne '/admin/machines') {
        throw 'InstallReturnUrl must use HTTPS, or loopback HTTP for local development, and point to /admin/machines without credentials, query or fragment.'
    }
    $returnUrlNormalized = $returnUri.AbsoluteUri
}

$normalizedAdapterId = $null
if (-not [string]::IsNullOrWhiteSpace($NetworkAdapterId)) {
    try { $normalizedAdapterId = ([Guid]::Parse($NetworkAdapterId)).ToString('B').ToUpperInvariant() }
    catch { throw 'NetworkAdapterId must be a network interface GUID.' }
}

$sourceDirectory = Join-Path $effectivePackageRoot 'Agent'
$serviceInstaller = Join-Path $effectivePackageRoot 'Install-Service.ps1'
$tightVncInstaller = Join-Path $effectivePackageRoot 'Install-TightVncServer.ps1'
$removeStreamGuardInstaller = Join-Path $effectivePackageRoot 'Remove-StreamGuard.ps1'
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'Agent.exe') -PathType Leaf)) { throw 'Agent.exe is missing from the package.' }
if (-not (Test-Path -LiteralPath $serviceInstaller -PathType Leaf)) { throw 'Install-Service.ps1 is missing from the package.' }
if (-not (Test-Path -LiteralPath $tightVncInstaller -PathType Leaf)) { throw 'Install-TightVncServer.ps1 is missing from the package.' }
if (-not (Test-Path -LiteralPath $removeStreamGuardInstaller -PathType Leaf)) { throw 'Remove-StreamGuard.ps1 is missing from the package.' }
if (-not (Test-Path -LiteralPath (Join-Path $effectivePackageRoot 'PrivateInstallFiles.ps1') -PathType Leaf)) { throw 'PrivateInstallFiles.ps1 is missing from the package.' }

$installDirectory = Join-Path (Join-Path $env:ProgramFiles 'KamenRaider') 'Agent'
$dataDirectory = Join-Path (Join-Path $env:ProgramData 'KamenRaider') 'Agent'
$streamGuardBackupDirectory = Join-Path (Join-Path $env:ProgramData 'KamenRaider') 'StreamGuardMigration'
if ($Plan) {
    [pscustomobject]@{
        ControllerUrl = $controllerUrlNormalized
        VncSetup = [pscustomobject]@{
            Version = '2.8.90'
            Port = 5900
            AllowedSourceAddress = $vncAllowedSourceAddress
            RemoteInputBlocked = $true
            FirewallProfile = 'Private'
        }
        PublishDirectory = $installDirectory
        InstallDirectory = $installDirectory
        DataDirectory = $dataDirectory
        ConfigurationStagingParent = $installDirectory
        MoonlightPort = $MoonlightPort
        SunshinePort = $SunshinePort
        NetworkAdapterId = $normalizedAdapterId
        NetworkAdapterSelection = if ($normalizedAdapterId) { 'explicit' } else { 'required-at-install' }
        Configuration = [pscustomobject]@{
            ControllerUrl = $controllerUrlNormalized
            DataDirectory = $dataDirectory
            MoonlightPort = $MoonlightPort
            SunshinePort = $SunshinePort
            NetworkAdapterId = $normalizedAdapterId
        }
        ServiceStartup = 'auto'
        StartAfterInstall = $true
        AgentService = [pscustomobject]@{
            Name = 'KamenRaider Agent'
            Account = 'LocalSystem'
            Startup = 'auto'
            Dependencies = @()
            StartAfterInstall = $true
        }
        SunshineService = [pscustomobject]@{
            Name = 'SunshineService'
            Startup = 'Automatic'
            EnsureExists = $true
            EnsureRunning = $true
            Action = 'Set automatic startup and start SunshineService before the Agent service starts.'
        }
        LegacyStreamGuardRemoval = [pscustomobject]@{
            Script = 'Remove-StreamGuard.ps1'
            Trigger = 'legacy Stream Guard service, managed firewall rule, dangling Guard dependency, or a virtual-account Agent service'
            BackupDirectory = $streamGuardBackupDirectory
            BackupContents = 'Guard/Agent/Sunshine service configuration and the managed firewall rule shape'
            Idempotent = $true
            RequiredOutcome = 'Guard removed; Agent service is LocalSystem with no dependencies; SunshineService is Automatic and Running.'
        }
        EnrollmentTicketStored = $true
        EnrollmentWaitSeconds = 90
        InstallReturnUrl = $returnUrlNormalized
    }
    return
}
if (-not $PSCmdlet.ShouldProcess($installDirectory, 'Install, configure and start the Agent service')) { return }

if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run installation from an elevated PowerShell session.'
}
. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $effectivePackageRoot 'PrivateInstallFiles.ps1'))))

$serviceName = 'KamenRaider Agent'
$guardServiceName = 'KamenRaider Stream Guard'
$serviceSid = Get-ServiceSid -ServiceName $serviceName
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$existingGuardService = Get-Service -Name $guardServiceName -ErrorAction SilentlyContinue
$installDirectoryExists = Test-Path -LiteralPath $installDirectory
$dataDirectoryExists = Test-Path -LiteralPath $dataDirectory
$emptyIdentityDirectoryResidue = $false
if (-not $existingService -and -not $existingGuardService -and -not $installDirectoryExists -and $dataDirectoryExists) {
    Assert-TrustedInstallParent $dataDirectory -ServiceSid $serviceSid
    $emptyIdentityDirectoryResidue = Test-EmptyAgentIdentityDirectoryResidue -DataDirectory $dataDirectory -ServiceSid $serviceSid
}
$installMode = Get-AgentInstallMode `
    -ServiceExists ([bool]$existingService) `
    -InstallDirectoryExists $installDirectoryExists `
    -DataDirectoryExists $dataDirectoryExists `
    -EmptyIdentityDirectoryResidue $emptyIdentityDirectoryResidue

$maintenanceDirectory = Join-Path $env:ProgramData 'KamenRaider.AgentMaintenance'
$maintenanceMarker = Join-Path $maintenanceDirectory 'maintenance-fence.json'
if ($installMode -eq 'install' -and (Test-Path -LiteralPath $maintenanceMarker -PathType Leaf)) {
    $existingClearFence = $false
    try { $existingFence = [IO.File]::ReadAllText($maintenanceMarker) | ConvertFrom-Json -ErrorAction Stop; $existingClearFence = $existingFence.state -ceq 'clear' } catch { }
    if ($existingClearFence) { throw 'A pre-existing clear maintenance fence cannot authorize newly installed package bytes; reviewed recovery is required.' }
}
$freshInstallCandidate = $installMode -eq 'install' -and
    -not $existingService -and -not $existingGuardService -and
    -not $installDirectoryExists -and -not $dataDirectoryExists -and
    -not (Test-Path -LiteralPath $maintenanceDirectory)
$verifiedBootstrapProvenance = $false
$bootstrapProofId = $null
if ($freshInstallCandidate -and -not [string]::IsNullOrWhiteSpace($BootstrapProvenancePath)) {
    $productDirectory = Join-Path $env:ProgramFiles 'KamenRaider'
    $machineProtectedProductParent = $false
    try {
        Assert-TrustedInstallParent $productDirectory -ServiceSid $serviceSid -MachineProtected
        $machineProtectedProductParent = $true
    } catch {
        Write-Warning 'The existing Program Files product directory is not machine-protected. Fresh fence initialization was skipped; streaming remains held until the administrator secures a new install path.'
    }
    if ($machineProtectedProductParent) {
        $bootstrapProofId = Test-AgentBootstrapProvenance `
            -Path $BootstrapProvenancePath -PackageRoot $effectivePackageRoot `
            -ProductDirectory $productDirectory -ServiceSid $serviceSid
        $verifiedBootstrapProvenance = -not [string]::IsNullOrWhiteSpace($bootstrapProofId)
    }
}
$initializeFreshFence = $freshInstallCandidate -and $verifiedBootstrapProvenance

if (-not (Test-Path -LiteralPath "Cert:\LocalMachine\Root\$TrustedRootThumbprint")) {
    throw 'Provision and verify the certificate trust chain before installation.'
}

if ($installMode -eq 'repair-enrollment') {
    if (-not $PSCmdlet.ShouldProcess($installDirectory, 'Verify the existing Agent and renew its enrollment ticket')) { return }
    if (Test-AgentLegacyServiceMigrationNeeded) {
        Write-Output 'Legacy Stream Guard state or a virtual-account Agent service was detected; running the idempotent Stream Guard removal migration before enrollment repair.'
        Invoke-LegacyStreamGuardMigration -InstallerPath $removeStreamGuardInstaller -PackageRoot $effectivePackageRoot `
            -BackupDirectory $streamGuardBackupDirectory | Out-Null
    }
    Ensure-SunshineServiceAutomaticAndRunning | Out-Null
    try {
        Invoke-AgentEnrollmentRepair -ServiceName $serviceName -InstallDirectory $installDirectory `
            -DataDirectory $dataDirectory -ControllerUrl $controllerUrlNormalized `
            -EnrollmentTicket $EnrollmentTicket -ServiceSid $serviceSid
    } catch {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service) { Write-Warning (Get-AgentEnrollmentRecoveryInstruction -ServiceStatus $service.Status.ToString()) }
        throw
    }
    $enrollmentStatus = Get-AgentEnrollmentStatusFromDataDirectory -DataDirectory $dataDirectory
    if ((Get-AgentVncSetupDecision -EnrollmentStatus $enrollmentStatus) -eq 'Configure') {
        try {
            Invoke-AgentVncSetup -InstallerPath $tightVncInstaller -AllowedSourceAddress $vncAllowedSourceAddress -DataDirectory $dataDirectory
        } catch {
            Write-Warning 'Agent enrollment remains installed, but TightVNC setup failed. VNC may be unavailable; rerun the admin install command to retry.'
            throw
        }
    } else {
        Write-Warning 'TightVNC setup was skipped because Agent enrollment is still pending. Rerun the admin install command after the Agent reaches the Controller.'
    }
    Open-AgentInstallReturnPage -Url $returnUrlNormalized
    return
}

$dotnetInterfaces = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()
$adapterCandidates = @(
    Get-NetIPConfiguration -ErrorAction Stop |
        Where-Object { $_.NetAdapter.Status -eq 'Up' -and $_.IPv4Address -and $_.IPv4DefaultGateway } |
        ForEach-Object {
            $interfaceGuid = [Guid]$_.NetAdapter.InterfaceGuid
            $matchingInterfaces = @($dotnetInterfaces | Where-Object {
                $candidateGuid = [Guid]::Empty
                [Guid]::TryParse([string]$_.Id, [ref]$candidateGuid) -and $candidateGuid -eq $interfaceGuid
            })
            if ($matchingInterfaces.Count -ne 1) { throw 'Unable to map the selected Windows adapter to its .NET NetworkInterface.Id.' }
            [pscustomobject]@{
                Id = [string]$matchingInterfaces[0].Id
                Name = [string]$_.InterfaceAlias
                Address = [string]$_.IPv4Address[0].IPv4Address
            }
        } | Sort-Object -Property Id -Unique
)
$normalizedAdapterId = Resolve-AgentNetworkAdapter -Candidates $adapterCandidates -RequestedId $normalizedAdapterId
$publishParent = Split-Path -Parent $installDirectory
$dataParent = Split-Path -Parent $dataDirectory
$dataParentExisted = Test-Path -LiteralPath $dataParent
$configStage = $null
$publishStage = $null
$publishMoved = $false
$serviceCreatedByThisRun = $false
$legacyMigrationAttempted = $false
$legacyMigrationCompleted = $false
$freshFencePublished = $false
$freshFenceHoldFailed = $false
$vncSetupAttempted = $false
$dataParentCreatedByThisRun = $false
$publishParentExisted = Test-Path -LiteralPath $publishParent
$publishParentCreatedByThisRun = $false

Assert-TrustedInstallParent $publishParent -ServiceSid $serviceSid -MachineProtected:$initializeFreshFence
Assert-TrustedInstallParent $dataDirectory -ServiceSid $serviceSid
Assert-AgentDataDirectoryReady -Path $dataDirectory -ServiceSid $serviceSid -AllowEmptyIdentityDirectory:$emptyIdentityDirectoryResidue
if (Test-Path -LiteralPath $installDirectory) { throw 'Agent installation directory already exists; reviewed upgrade or repair is required.' }
Assert-ServiceIsAbsent $serviceName
Assert-TrustedPublishTree $sourceDirectory
$publishStage = Join-Path $publishParent ('.Agent-install-' + [Guid]::NewGuid().ToString('N'))
$configStage = Join-Path $installDirectory ('.agent-config-' + [Guid]::NewGuid().ToString('N'))
try {
    if (-not $dataParentExisted) {
        New-PrivateInstallDirectory $dataParent $serviceSid -ReadOnly | Out-Null
        $dataParentCreatedByThisRun = $true
    } else {
        Assert-TrustedInstallParent $dataParent -ServiceSid $serviceSid
    }
    if (-not $publishParentExisted) {
        New-PrivateInstallDirectory $publishParent $serviceSid -ReadOnly | Out-Null
        $publishParentCreatedByThisRun = $true
    }
    New-PrivateInstallDirectory $publishStage $serviceSid -ReadOnly -MachineProtected:$initializeFreshFence | Out-Null
    $sourceRoot = [IO.Path]::GetFullPath($sourceDirectory).TrimEnd('\')
    foreach ($item in @(Get-ChildItem -LiteralPath $sourceRoot -Force -Recurse | Sort-Object { $_.FullName.Length })) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Agent package must not contain reparse points.' }
        $relative = $item.FullName.Substring($sourceRoot.Length).TrimStart('\')
        $destination = Join-Path $publishStage $relative
        if ($item.PSIsContainer) {
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            if ($initializeFreshFence) { Set-PrivateAgentBinaryAcl -Path $destination -ServiceSid $serviceSid -Directory }
        } else {
            $destinationParent = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $destinationParent)) { New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null }
            [IO.File]::Copy($item.FullName, $destination, $false)
            if ($initializeFreshFence) { Set-PrivateAgentBinaryAcl -Path $destination -ServiceSid $serviceSid }
        }
    }
    Assert-TrustedPublishTree $publishStage -ServiceSid $serviceSid -MachineProtected:$initializeFreshFence
    [IO.Directory]::Move($publishStage, $installDirectory)
    $publishStage = $null
    $publishMoved = $true

    New-PrivateInstallDirectory $configStage $serviceSid -ReadOnly | Out-Null
    $configSource = Join-Path $configStage 'appsettings.json'
    $configFile = [IO.File]::Open($configSource, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $configFile.Dispose()
    Set-PrivateInstallAcl $configSource $serviceSid
    $configuration = New-AgentInstallConfiguration -ControllerUrl $controllerUrlNormalized -DataDirectory $dataDirectory `
        -MoonlightPort $MoonlightPort -SunshinePort $SunshinePort -NetworkAdapterId $normalizedAdapterId
    [IO.File]::WriteAllText($configSource, $configuration, [Text.UTF8Encoding]::new($false))

    if ($initializeFreshFence) {
        $freshFencePublished = $true
        Initialize-FreshAgentMaintenanceFence -DirectoryPath $maintenanceDirectory -ServiceSid $serviceSid | Out-Null
        Consume-AgentBootstrapProvenance -Path $BootstrapProvenancePath -ProofId $bootstrapProofId
    } elseif ($freshInstallCandidate) {
        Write-Warning 'This fresh install did not arrive through the verified protected bootstrap. Installation may complete, but the missing maintenance fence keeps streaming held; rerun the Controller-provided administrator install command to initialize it.'
    } elseif ($emptyIdentityDirectoryResidue) {
        Write-Warning 'An interrupted identity-directory residue was preserved. No fresh clear maintenance fence was created; streaming remains held until a verified recovery operation completes.'
    }

    if (Test-AgentLegacyServiceMigrationNeeded) {
        $legacyMigrationAttempted = $true
        Write-Output 'Legacy Stream Guard state or a virtual-account Agent service was detected; running the idempotent Stream Guard removal migration.'
        Invoke-LegacyStreamGuardMigration -InstallerPath $removeStreamGuardInstaller -PackageRoot $effectivePackageRoot `
            -BackupDirectory $streamGuardBackupDirectory | Out-Null
        $legacyMigrationCompleted = $true
    }
    Ensure-SunshineServiceAutomaticAndRunning | Out-Null

    $serviceInstallerBlock = [ScriptBlock]::Create([IO.File]::ReadAllText($serviceInstaller))
    & $serviceInstallerBlock -Role Agent -PublishDirectory $installDirectory -DataDirectory $dataDirectory `
        -InstallScriptRoot $effectivePackageRoot `
        -ConfigurationFile $configSource -TrustedRootThumbprint $TrustedRootThumbprint `
        -EnrollmentTicket $EnrollmentTicket -StartAfterInstall -Confirm:$false
    $serviceCreatedByThisRun = $true

    $receiptPath = Join-Path $dataDirectory 'enrollment-receipt.json'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $receiptStatus = $null
    while ($timer.Elapsed.TotalSeconds -lt 90) {
        $candidateStatus = $null
        if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
            try {
                $candidate = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json -ErrorAction Stop
                $candidateStatus = Get-AgentEnrollmentStatus -Receipt $candidate
            } catch { }
        }
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if (-not $service) { throw 'Agent service disappeared while waiting for its enrollment receipt.' }
        $waitState = Get-AgentEnrollmentWaitState -ServiceStatus $service.Status.ToString() -ReceiptStatus $candidateStatus
        if ($waitState -eq 'unavailable') { throw "Agent service entered '$($service.Status)' while waiting for its enrollment receipt; installation did not complete." }
        if ($waitState -eq 'enrolled') { $receiptStatus = $candidateStatus; break }
        $remaining = 90 - $timer.Elapsed.TotalSeconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Min(2000, [Math]::Ceiling($remaining * 1000))) }
    }
    if ($receiptStatus) {
        Write-Output $receiptStatus.Message
        Write-Output 'The machine still requires administrator configuration and approval before it can be rented.'
    } else {
        $pendingStatus = Get-AgentEnrollmentStatus
        Write-Warning $pendingStatus.Message
    }
    if ((Get-AgentVncSetupDecision -EnrollmentStatus $receiptStatus) -eq 'Configure') {
        $vncSetupAttempted = $true
        Invoke-AgentVncSetup -InstallerPath $tightVncInstaller -AllowedSourceAddress $vncAllowedSourceAddress -DataDirectory $dataDirectory
    } else {
        Write-Warning 'TightVNC setup was skipped because Agent enrollment is still pending. Rerun the admin install command after the Agent reaches the Controller.'
    }
    Open-AgentInstallReturnPage -Url $returnUrlNormalized
    Write-Output "Agent service installed and started with automatic startup. Data directory: $dataDirectory"
} catch {
    $original = $_
    if ($freshFencePublished -and -not $serviceCreatedByThisRun) {
        try { Set-FreshAgentMaintenanceFenceHeld -DirectoryPath $maintenanceDirectory -ServiceSid $serviceSid }
        catch {
            $freshFenceHoldFailed = $true
            Write-Warning 'Fresh Agent initialization failed; the maintenance fence could not be changed to held. The protected installation and evidence were retained; use the administrator recovery path.'
        }
    }
    if ($legacyMigrationAttempted -and -not $legacyMigrationCompleted) {
        Write-Warning "The legacy Stream Guard migration did not complete. Its service and firewall configuration backup is retained under '$streamGuardBackupDirectory'; rerun the admin install command after resolving the reported error (the migration is idempotent)."
    }
    if ($serviceCreatedByThisRun) {
        $failedService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($failedService -and $vncSetupAttempted) {
            Write-Warning 'The Agent service remains installed, but TightVNC setup did not finish. VNC may be unavailable; rerun the admin install command to retry.'
        } elseif ($failedService) {
            Write-Warning (Get-AgentEnrollmentRecoveryInstruction -ServiceStatus $failedService.Status.ToString())
        }
    }
    if ($configStage -and (Test-Path -LiteralPath $configStage)) {
        $resolvedConfigStage = [IO.Path]::GetFullPath($configStage)
        if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolvedConfigStage),[IO.Path]::GetFullPath($installDirectory),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedConfigStage) -notlike '.agent-config-*') { throw 'Unsafe configuration staging cleanup path.' }
        Remove-Item -LiteralPath $resolvedConfigStage -Recurse -Force
    }
    $serviceExists = [bool](Get-Service -Name $serviceName -ErrorAction SilentlyContinue)
    $legacyMigrationFailed = $legacyMigrationAttempted -and -not $legacyMigrationCompleted
    if (-not $serviceExists -and -not $legacyMigrationFailed -and -not $freshFenceHoldFailed -and $publishMoved -and (Test-Path -LiteralPath $installDirectory)) {
        $safeInstall = [IO.Path]::GetFullPath($installDirectory)
        $expectedInstall = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'KamenRaider\Agent'))
        if ($safeInstall -eq $expectedInstall) { Remove-Item -LiteralPath $safeInstall -Recurse -Force }
    }
    throw $original
} finally {
    if ($publishStage -and (Test-Path -LiteralPath $publishStage)) {
        $resolvedStage = [IO.Path]::GetFullPath($publishStage)
        $expectedParent = [IO.Path]::GetFullPath($publishParent).TrimEnd('\') + '\'
        if (-not $resolvedStage.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedStage) -notlike '.Agent-install-*') { throw 'Unsafe Agent staging cleanup path.' }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
    if ($configStage -and (Test-Path -LiteralPath $configStage)) {
        $resolvedConfigStage = [IO.Path]::GetFullPath($configStage)
        if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolvedConfigStage),[IO.Path]::GetFullPath($installDirectory),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedConfigStage) -notlike '.agent-config-*') { throw 'Unsafe configuration staging cleanup path.' }
        Remove-Item -LiteralPath $resolvedConfigStage -Recurse -Force
    }
    if ($publishParentCreatedByThisRun -and (Test-Path -LiteralPath $publishParent) -and @(Get-ChildItem -LiteralPath $publishParent -Force).Count -eq 0) {
        Remove-Item -LiteralPath $publishParent -Force
    }
    if ($dataParentCreatedByThisRun -and (Test-Path -LiteralPath $dataParent) -and @(Get-ChildItem -LiteralPath $dataParent -Force).Count -eq 0) {
        Remove-Item -LiteralPath $dataParent -Force
    }
}
