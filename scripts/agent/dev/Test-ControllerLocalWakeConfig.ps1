$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$sourceLauncher = Join-Path $repo 'scripts\agent\dev\Run-Controller-Local.ps1'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('KamenRaider-ControllerWakeConfig-' + [guid]::NewGuid().ToString('N'))
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
if (-not $resolvedTempRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporary launcher fixture escaped the system temp directory.'
}

function Write-EnvFixture([string]$Root, [bool]$IncludeWake, [string]$InterfaceAddress = '', [string]$BroadcastAddress = '') {
    $lines = @(
        'CONTROLLER_INTERNAL_URL=http://127.0.0.1:5081'
        'CONTROLLER_SERVICE_CREDENTIAL=fixture-credential'
        'CONTROLLER_AGENT_URL=https://localhost:7443'
        'CONTROLLER_DATABASE_PATH=C:\fixture\controller.db'
    )
    if ($IncludeWake) {
        $lines += "CONTROLLER_WAKE_INTERFACE_ADDRESS=$InterfaceAddress"
        $lines += "CONTROLLER_WAKE_BROADCAST_ADDRESS=$BroadcastAddress"
    }
    Set-Content -LiteralPath (Join-Path $Root '.env.local') -Value $lines -Encoding UTF8
}

function Invoke-Launcher([string]$Launcher, [string]$StubDirectory, [string]$StaleInterface, [string]$StaleBroadcast) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Join-Path $PSHOME 'powershell.exe'
    $startInfo.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $Launcher + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['PATH'] = $StubDirectory + ';' + $env:PATH
    $startInfo.EnvironmentVariables['WakeInterfaceAddress'] = $StaleInterface
    $startInfo.EnvironmentVariables['WakeBroadcastAddress'] = $StaleBroadcast

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'Could not start the isolated launcher process.' }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout; Stderr = $stderr }
}

try {
    $launcherDirectory = Join-Path $tempRoot 'scripts\agent\dev'
    $stubDirectory = Join-Path $tempRoot 'stub-bin'
    New-Item -ItemType Directory -Path $launcherDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $stubDirectory -Force | Out-Null
    $launcher = Join-Path $launcherDirectory 'Run-Controller-Local.ps1'
    Copy-Item -LiteralPath $sourceLauncher -Destination $launcher
    @'
@echo off
echo DOTNET_LAUNCHED
echo WAKE_INTERFACE=%WakeInterfaceAddress%
echo WAKE_BROADCAST=%WakeBroadcastAddress%
exit /b 0
'@ | Set-Content -LiteralPath (Join-Path $stubDirectory 'dotnet.cmd') -Encoding ASCII

    Write-EnvFixture -Root $tempRoot -IncludeWake $true -InterfaceAddress '192.168.1.34' -BroadcastAddress '192.168.1.255'
    $configured = Invoke-Launcher -Launcher $launcher -StubDirectory $stubDirectory -StaleInterface '' -StaleBroadcast ''
    if ($configured.ExitCode -ne 0 -or $configured.Stdout -notmatch '(?m)^WAKE_INTERFACE=192\.168\.1\.34\r?$' -or
        $configured.Stdout -notmatch '(?m)^WAKE_BROADCAST=192\.168\.1\.255\r?$') {
        throw 'Configured WOL addresses must reach the local Controller process as root .NET configuration keys.'
    }

    Write-EnvFixture -Root $tempRoot -IncludeWake $true -InterfaceAddress 'not-an-ip' -BroadcastAddress '192.168.1.255'
    $invalid = Invoke-Launcher -Launcher $launcher -StubDirectory $stubDirectory -StaleInterface '' -StaleBroadcast ''
    if ($invalid.ExitCode -eq 0 -or $invalid.Stdout -match '(?m)^DOTNET_LAUNCHED\r?$') {
        throw 'Invalid WOL addresses must be rejected before launching the Controller.'
    }

    Write-EnvFixture -Root $tempRoot -IncludeWake $false
    $optional = Invoke-Launcher -Launcher $launcher -StubDirectory $stubDirectory `
        -StaleInterface '' -StaleBroadcast ''
    if ($optional.ExitCode -ne 0 -or $optional.Stdout -notmatch '(?m)^WAKE_INTERFACE=\r?$' -or
        $optional.Stdout -notmatch '(?m)^WAKE_BROADCAST=\r?$') {
        throw 'Omitted optional WOL values must not block starting the local Controller.'
    }

    Write-Output 'Controller local WOL configuration mapping and validation passed.'
} finally {
    if (Test-Path -LiteralPath $resolvedTempRoot) {
        $checkTarget = [IO.Path]::GetFullPath($resolvedTempRoot)
        if (-not $checkTarget.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove a temporary launcher fixture outside the system temp directory.'
        }
        Remove-Item -LiteralPath $checkTarget -Recurse -Force
    }
}
