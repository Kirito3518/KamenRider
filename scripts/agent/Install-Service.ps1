[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)][ValidateSet('Controller', 'Agent')][string]$Role,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$ConfigurationFile,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$TrustedRootThumbprint,
    [switch]$StartAfterInstall,
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
foreach ($path in @($PublishDirectory, $DataDirectory, $ConfigurationFile)) {
    if (-not [IO.Path]::IsPathRooted($path)) { throw 'All installation paths must be absolute.' }
}

$serviceName = "KamenRaider $Role"
$account = "NT SERVICE\$serviceName"
$executable = Join-Path $PublishDirectory "$Role.exe"
$startup = 'auto'
if ($Plan) {
    [pscustomobject]@{
        ServiceName = $serviceName
        Account = $account
        Binary = $executable
        DataDirectory = $DataDirectory
        Startup = $startup
        StartAfterInstall = [bool]$StartAfterInstall
        TrustedRootThumbprint = $TrustedRootThumbprint
        Recovery = 'restart after 5/15/60 seconds'
    }
    return
}

if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run service installation from an elevated PowerShell session.'
}

. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')
. (Join-Path $PSScriptRoot 'ServiceInstallTransaction.ps1')
Assert-TrustedInstallParent $PublishDirectory
Assert-TrustedInstallParent $DataDirectory
Assert-TrustedInstallParent $ConfigurationFile
Assert-EmptyInstallDirectory $DataDirectory
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Publish executable is missing.' }
if (-not (Test-Path -LiteralPath $ConfigurationFile -PathType Leaf)) { throw 'Provide a provisioned configuration file.' }
if (Test-Path -LiteralPath (Join-Path $PublishDirectory 'appsettings.json')) { throw 'Service configuration already exists; reviewed upgrade or repair is required.' }
Assert-TrustedPublishTree $PublishDirectory
Assert-TrustedInstallFile $executable
Assert-TrustedInstallFile $ConfigurationFile -Private
if (-not (Test-Path -LiteralPath "Cert:\LocalMachine\Root\$TrustedRootThumbprint")) {
    throw 'Provision and verify the certificate trust chain before installation.'
}
Assert-ServiceIsAbsent $serviceName

if (-not $PSCmdlet.ShouldProcess($serviceName, 'Create service, secure configuration and data, and configure startup')) { return }

$binaryPath = '"' + $executable + '" --contentRoot "' + $PublishDirectory + '"'
Invoke-ServiceInstallTransaction -Create {
    & sc.exe create $serviceName binPath= $binaryPath start= disabled obj= $account | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Service creation failed; ownership of any partially created service could not be confirmed.' }
    return $true
} -Configure {
    $serviceSid = ([Security.Principal.NTAccount]::new($account)).Translate([Security.Principal.SecurityIdentifier])
    Initialize-PrivateDataDirectory $DataDirectory $serviceSid
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
}

$startState = if ($StartAfterInstall) { 'started' } else { 'not started' }
Write-Output "Installed $serviceName as $account, startup=$startup, $startState."
