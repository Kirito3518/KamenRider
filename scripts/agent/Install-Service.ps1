[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)][ValidateSet('Controller', 'Agent')][string]$Role,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$ConfigurationFile,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$TrustedRootThumbprint,
    [string]$InstallScriptRoot,
    [string]$EnrollmentTicket,
    [string[]]$Dependencies = @(),
    [switch]$StartAfterInstall,
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
foreach ($path in @($PublishDirectory, $DataDirectory, $ConfigurationFile)) {
    if (-not [IO.Path]::IsPathRooted($path)) { throw 'All installation paths must be absolute.' }
}

$serviceName = "KamenRaider $Role"
$account = if ($Role -eq 'Agent') { 'LocalSystem' } else { "NT SERVICE\$serviceName" }
$executable = Join-Path $PublishDirectory "$Role.exe"
$startup = 'auto'
if (@($Dependencies | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_.Contains([char]0) }).Count -gt 0) { throw 'Service dependencies must be non-empty service names without null characters.' }
if (@($Dependencies | Select-Object -Unique).Count -ne @($Dependencies).Count) { throw 'Service dependencies must not contain duplicates.' }
if ($Role -eq 'Agent' -and @($Dependencies).Count -ne 0) {
    throw 'The Agent service must be a single LocalSystem service with no service dependencies.'
}
if ($Plan) {
    [pscustomobject]@{
        ServiceName = $serviceName
        Account = $account
        Binary = $executable
        Dependencies = @($Dependencies)
        DataDirectory = $DataDirectory
        Startup = $startup
        StartAfterInstall = [bool]$StartAfterInstall
        TrustedRootThumbprint = $TrustedRootThumbprint
        EnrollmentTicketStored = -not [string]::IsNullOrWhiteSpace($EnrollmentTicket)
        Recovery = 'restart after 5/15/60 seconds'
    }
    return
}

if ($Role -eq 'Controller') { throw 'Controller service installation is not enabled until separately reviewed.' }
if ($Role -eq 'Agent' -and $EnrollmentTicket -notmatch '^[A-Za-z0-9_-]{43}$') { throw 'A valid enrollment ticket is required for Agent service installation.' }

if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run service installation from an elevated PowerShell session.'
}

$scriptRoot = if ([string]::IsNullOrWhiteSpace($InstallScriptRoot)) { $PSScriptRoot } else { $InstallScriptRoot }
if ([string]::IsNullOrWhiteSpace($scriptRoot) -or -not [IO.Path]::IsPathRooted($scriptRoot)) { throw 'InstallScriptRoot must identify the absolute Agent package directory.' }
$scriptRoot = [IO.Path]::GetFullPath($scriptRoot)
. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $scriptRoot 'PrivateInstallFiles.ps1'))))
. ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $scriptRoot 'ServiceInstallTransaction.ps1'))))
$serviceSid = Get-ServiceSid -ServiceName $serviceName
Assert-TrustedInstallParent $PublishDirectory
Assert-TrustedInstallParent $DataDirectory -ServiceSid $serviceSid
Assert-TrustedInstallParent $ConfigurationFile
if ($Role -eq 'Agent') { Assert-AgentDataDirectoryReady -Path $DataDirectory -ServiceSid $serviceSid -AllowEmptyIdentityDirectory }
else { Assert-EmptyInstallDirectory $DataDirectory }
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Publish executable is missing.' }
if (-not (Test-Path -LiteralPath $ConfigurationFile -PathType Leaf)) { throw 'Provide a provisioned configuration file.' }
if (Test-Path -LiteralPath (Join-Path $PublishDirectory 'appsettings.json')) { throw 'Service configuration already exists; reviewed upgrade or repair is required.' }
Assert-TrustedPublishTree $PublishDirectory
Assert-TrustedInstallFile $executable
Assert-TrustedInstallFile $ConfigurationFile -ServiceSid $serviceSid -Private
if (-not (Test-Path -LiteralPath "Cert:\LocalMachine\Root\$TrustedRootThumbprint")) {
    throw 'Provision and verify the certificate trust chain before installation.'
}
Assert-ServiceIsAbsent $serviceName

if (-not $PSCmdlet.ShouldProcess($serviceName, 'Create service, secure configuration and data, and configure startup')) { return }

if ($Role -eq 'Agent') {
    # AddWindowsService uses the Agent assembly name as the default Event Log source.
    # Register it elevated so the restricted service identity can write startup failures.
    Ensure-AgentEventLogSource -SourceName 'Agent'
}

$binaryPath = Get-ServiceBinaryPath -Executable $executable -ContentRoot $PublishDirectory
$dataDirectoryState = [pscustomobject]@{ Created = $false }
Invoke-ServiceInstallTransaction -Create {
    New-DisabledWindowsService -ServiceName $serviceName -BinaryPath $binaryPath -Account $account -Dependencies $Dependencies
    return $true
} -Configure {
    $dataDirectoryState.Created = Initialize-PrivateDataDirectory $DataDirectory $serviceSid -AllowEmptyIdentityDirectory:($Role -eq 'Agent')
    if ($Role -eq 'Agent') { Publish-PrivateEnrollmentTicket -Directory $DataDirectory -Ticket $EnrollmentTicket -ServiceSid $serviceSid | Out-Null }
    Publish-PrivateConfiguration $ConfigurationFile $PublishDirectory $serviceSid
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Recovery configuration failed.' }
    & sc.exe config $serviceName start= $startup | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Automatic startup configuration failed.' }
    if ($StartAfterInstall) {
        Start-Service -Name $serviceName -ErrorAction Stop
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        do {
            $service = Get-Service -Name $serviceName -ErrorAction Stop
            if ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running) { break }
            if ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Stopped) { throw 'Agent service stopped during startup.' }
            Start-Sleep -Milliseconds 250
        } while ($deadline.Elapsed.TotalSeconds -lt 30)
        if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Running) { throw 'Agent service did not reach Running state within 30 seconds.' }
    }
} -Rollback {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $serviceName -Force -ErrorAction Stop
            $deadline = [Diagnostics.Stopwatch]::StartNew()
            do {
                Start-Sleep -Milliseconds 250
                $service = Get-Service -Name $serviceName -ErrorAction Stop
            } while ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped -and $deadline.Elapsed.TotalSeconds -lt 15)
            if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) { throw 'New service could not be stopped safely.' }
        }
        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to remove the service created by this installation.' }
    }
    if ($dataDirectoryState.Created -and (Test-Path -LiteralPath $DataDirectory)) {
        $ticketPath = Join-Path $DataDirectory 'enrollment-ticket.txt'
        if (Test-Path -LiteralPath $ticketPath -PathType Leaf) { Remove-Item -LiteralPath $ticketPath -Force }
        if (@(Get-ChildItem -LiteralPath $DataDirectory -Force).Count -eq 0) { [IO.Directory]::Delete([IO.Path]::GetFullPath($DataDirectory), $false) }
    }
}

$startState = if ($StartAfterInstall) { 'started' } else { 'not started' }
Write-Output "Installed $serviceName as $account, startup=$startup, $startState."
