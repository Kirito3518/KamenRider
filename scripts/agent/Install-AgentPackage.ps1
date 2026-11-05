[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)][string]$ControllerUrl,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$TrustedRootThumbprint,
    [ValidateRange(1, 65535)][int]$MoonlightPort = 47989,
    [ValidateRange(1, 65535)][int]$SunshinePort = 47990,
    [string]$NetworkAdapterId,
    [string]$InstallReturnUrl,
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'

try { $controller = [Uri]::new($ControllerUrl, [UriKind]::Absolute) }
catch { throw 'ControllerUrl must be an absolute HTTPS origin.' }
if ($controller.Scheme -ne 'https' -or $controller.UserInfo -or $controller.Query -or $controller.Fragment -or $controller.AbsolutePath -ne '/') {
    throw 'ControllerUrl must be an HTTPS origin without credentials, path, query or fragment.'
}
$controllerUrlNormalized = $controller.GetLeftPart([UriPartial]::Authority) + '/'

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

$sourceDirectory = Join-Path $PSScriptRoot 'Agent'
$serviceInstaller = Join-Path $PSScriptRoot 'Install-Service.ps1'
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'Agent.exe') -PathType Leaf)) { throw 'Agent.exe is missing from the package.' }
if (-not (Test-Path -LiteralPath $serviceInstaller -PathType Leaf)) { throw 'Install-Service.ps1 is missing from the package.' }

$installDirectory = Join-Path (Join-Path $env:ProgramFiles 'KamenRaider') 'Agent'
$dataDirectory = Join-Path (Join-Path $env:ProgramData 'KamenRaider') 'Agent'
if ($Plan) {
    [pscustomobject]@{
        ControllerUrl = $controllerUrlNormalized
        PublishDirectory = $installDirectory
        InstallDirectory = $installDirectory
        DataDirectory = $dataDirectory
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
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')

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
$serviceName = 'KamenRaider Agent'
$serviceAccount = "NT SERVICE\$serviceName"
$publishParent = Split-Path -Parent $installDirectory
$dataExisted = Test-Path -LiteralPath $dataDirectory
$dataCreatedByThisRun = -not $dataExisted
$configStage = $null
$publishStage = $null
$publishMoved = $false

Assert-TrustedInstallParent $publishParent
Assert-TrustedInstallParent $dataDirectory
Assert-EmptyInstallDirectory $dataDirectory
if (Test-Path -LiteralPath $installDirectory) { throw 'Agent installation directory already exists; reviewed upgrade or repair is required.' }
Assert-ServiceIsAbsent $serviceName
Assert-TrustedPublishTree $sourceDirectory
if (-not (Test-Path -LiteralPath "Cert:\LocalMachine\Root\$TrustedRootThumbprint")) {
    throw 'Provision and verify the certificate trust chain before installation.'
}

$serviceSid = ([Security.Principal.NTAccount]::new($serviceAccount)).Translate([Security.Principal.SecurityIdentifier])
$parentExisted = Test-Path -LiteralPath $publishParent
if (-not $parentExisted) {
    New-Item -ItemType Directory -Path $publishParent -Force | Out-Null
}
$publishStage = Join-Path $publishParent ('.Agent-install-' + [Guid]::NewGuid().ToString('N'))
$configStage = Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-config-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $publishStage | Out-Null
    Set-PrivateInstallAcl $publishStage $serviceSid -Directory -ReadOnly
    $sourceRoot = [IO.Path]::GetFullPath($sourceDirectory).TrimEnd('\')
    foreach ($item in @(Get-ChildItem -LiteralPath $sourceRoot -Force -Recurse | Sort-Object { $_.FullName.Length })) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Agent package must not contain reparse points.' }
        $relative = $item.FullName.Substring($sourceRoot.Length).TrimStart('\')
        $destination = Join-Path $publishStage $relative
        if ($item.PSIsContainer) {
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
        } else {
            $destinationParent = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $destinationParent)) { New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null }
            [IO.File]::Copy($item.FullName, $destination, $false)
        }
    }
    Assert-TrustedPublishTree $publishStage
    [IO.Directory]::Move($publishStage, $installDirectory)
    $publishStage = $null
    $publishMoved = $true

    New-Item -ItemType Directory -Path $configStage | Out-Null
    Set-PrivateInstallAcl $configStage $serviceSid -Directory
    $configSource = Join-Path $configStage 'appsettings.json'
    $configFile = [IO.File]::Open($configSource, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $configFile.Dispose()
    Set-PrivateInstallAcl $configSource $serviceSid
    $configuration = New-AgentInstallConfiguration -ControllerUrl $controllerUrlNormalized -DataDirectory $dataDirectory `
        -MoonlightPort $MoonlightPort -SunshinePort $SunshinePort -NetworkAdapterId $normalizedAdapterId
    [IO.File]::WriteAllText($configSource, $configuration, [Text.UTF8Encoding]::new($false))

    $serviceInstaller = Join-Path $PSScriptRoot 'Install-Service.ps1'
    & $serviceInstaller -Role Agent -PublishDirectory $installDirectory -DataDirectory $dataDirectory `
        -ConfigurationFile $configSource -TrustedRootThumbprint $TrustedRootThumbprint -StartAfterInstall

    $receiptPath = Join-Path $dataDirectory 'enrollment-receipt.json'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $receipt = $null
    while ($timer.Elapsed.TotalSeconds -lt 90) {
        if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
            try {
                $candidate = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json -ErrorAction Stop
                if ($candidate.MachineId -and $candidate.ApprovalStatus -in @('pending', 'approved') -and $candidate.PublicKeyFingerprint -match '^[A-Fa-f0-9]{64}$') {
                    $receipt = $candidate
                    break
                }
            } catch { }
        }
        $remaining = 90 - $timer.Elapsed.TotalSeconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Min(2000, [Math]::Ceiling($remaining * 1000))) }
    }
    if ($receipt) {
        Write-Output ('Enrollment received: MachineId={0}; Status={1}; Fingerprint={2}' -f $receipt.MachineId, $receipt.ApprovalStatus, $receipt.PublicKeyFingerprint)
        Write-Output 'The machine still requires administrator configuration and approval before it can be rented.'
    } else {
        Write-Warning 'Enrollment receipt was not received within 90 seconds. The installed service remains running and will retry enrollment.'
    }
    if ($returnUrlNormalized) {
        try { Start-Process -FilePath $returnUrlNormalized }
        catch { Write-Warning 'Agent installation completed, but the admin page could not be opened automatically.' }
    }
    Write-Output "Agent service installed and started with automatic startup. Data directory: $dataDirectory"
} catch {
    $original = $_
    if ($configStage -and (Test-Path -LiteralPath $configStage)) {
        $resolvedConfigStage = [IO.Path]::GetFullPath($configStage)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedConfigStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedConfigStage) -notlike 'kr-agent-config-*') { throw 'Unsafe configuration staging cleanup path.' }
        Remove-Item -LiteralPath $resolvedConfigStage -Recurse -Force
    }
    $serviceExists = [bool](Get-Service -Name $serviceName -ErrorAction SilentlyContinue)
    if (-not $serviceExists -and $publishMoved -and (Test-Path -LiteralPath $installDirectory)) {
        $safeInstall = [IO.Path]::GetFullPath($installDirectory)
        $expectedInstall = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'KamenRaider\Agent'))
        if ($safeInstall -eq $expectedInstall) { Remove-Item -LiteralPath $safeInstall -Recurse -Force }
    }
    if ($dataCreatedByThisRun -and (Test-Path -LiteralPath $dataDirectory) -and @(Get-ChildItem -LiteralPath $dataDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $dataDirectory -Force
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
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedConfigStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedConfigStage) -notlike 'kr-agent-config-*') { throw 'Unsafe configuration staging cleanup path.' }
        Remove-Item -LiteralPath $resolvedConfigStage -Recurse -Force
    }
}
