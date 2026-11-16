[CmdletBinding()]
param(
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:BlockRuleName = 'KamenRaider Stream Guard - Sunshine'
$script:SunshinePath = 'C:\Program Files\Sunshine\sunshine.exe'
$script:SunshineServiceName = 'SunshineService'
$script:ApiUri = 'https://localhost:47990/api/clients/list'

function Test-AnyFilterValue {
    param([AllowNull()][string]$Value)

    return [string]::IsNullOrWhiteSpace($Value) -or
        $Value -eq '*' -or
        $Value -ieq 'Any'
}

function Test-ManagedSunshineBlock {
    $rules = @(Get-NetFirewallRule -Name $script:BlockRuleName -PolicyStore ActiveStore -ErrorAction Stop)
    if ($rules.Count -ne 1) { return $false }

    $rule = $rules[0]
    $application = $rule | Get-NetFirewallApplicationFilter -ErrorAction Stop
    $service = $rule | Get-NetFirewallServiceFilter -ErrorAction Stop
    $port = $rule | Get-NetFirewallPortFilter -ErrorAction Stop
    $address = $rule | Get-NetFirewallAddressFilter -ErrorAction Stop
    $interface = $rule | Get-NetFirewallInterfaceTypeFilter -ErrorAction Stop
    $overrideProperty = $rule.PSObject.Properties['OverrideBlockRules']
    $overrideBlockRules = $false
    if ($null -ne $overrideProperty) { $overrideBlockRules = [bool]$overrideProperty.Value }

    $actualPath = $null
    if (-not [string]::IsNullOrWhiteSpace($application.Program)) {
        try { $actualPath = [IO.Path]::GetFullPath($application.Program) } catch { return $false }
    }

    return ($rule.Enabled -eq 'True') -and
        $rule.Name -eq $script:BlockRuleName -and
        $rule.Action -eq 'Block' -and
        $rule.Direction -eq 'Inbound' -and
        $rule.Profile -eq 'Any' -and
        [string]::Equals($actualPath, $script:SunshinePath, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-AnyFilterValue $service.Service) -and
        $port.Protocol -eq 'Any' -and
        (Test-AnyFilterValue $port.LocalPort) -and
        (Test-AnyFilterValue $port.RemotePort) -and
        (Test-AnyFilterValue $address.LocalAddress) -and
        (Test-AnyFilterValue $address.RemoteAddress) -and
        ($interface.InterfaceType -eq 'Any' -or $interface.InterfaceType -eq 'All') -and
        $rule.EdgeTraversalPolicy -eq 'Block' -and
        -not $overrideBlockRules
}

function Test-WindowsFirewallReady {
    try {
        # ActiveStore reports the effective profile state after policy merging.
        # Require all profiles because the machine's active network can change
        # while this diagnostic is running.
        $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -All -ErrorAction Stop)
        foreach ($name in @('Domain', 'Private', 'Public')) {
            $matchingProfiles = @($profiles | Where-Object { [string]$_.Name -eq $name })
            if ($matchingProfiles.Count -ne 1) { return $false }
            if ([string]$matchingProfiles[0].Enabled -ne 'True') { return $false }
        }

        $firewallService = Get-Service -Name 'MpsSvc' -ErrorAction Stop
        return $firewallService.Status.ToString() -eq 'Running'
    }
    catch {
        # Missing cmdlets, inaccessible policy, and service lookup failures all
        # fail closed. The caller reports the same safe precondition failure.
        return $false
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Set-SunshinePortWaitFailure {
    param([string]$SunshineState)

    if ($SunshineState -eq 'Running') { return $false }

    $guardState = (Get-Service -Name 'KamenRaider Stream Guard' -ErrorAction Stop).Status.ToString()
    $result.SunshineStateDuringPortWait = $SunshineState
    $result.GuardDuringPortWait = $guardState
    $result.Status = 'SunshineNotRunningDuringPortWait'
    $result.FailureCategory = 'SunshineNotRunningDuringPortWait'
    $result.FailureDetail = if ($guardState -eq 'Running') {
        'Sunshine state during the port wait: ' + $SunshineState + '. Stream Guard remained Running; Guard enforcement is possible.'
    }
    else {
        'Sunshine state during the port wait: ' + $SunshineState + '.'
    }
    return $true
}

function Test-AuthenticationFailure {
    param([System.Exception]$Exception)

    $current = $Exception
    while ($null -ne $current) {
        if ($current -is [System.Security.Authentication.AuthenticationException]) { return $true }
        $current = $current.InnerException
    }
    return $false
}

function Get-SanitizedFailureCategory {
    param([System.Exception]$Exception)

    if (Test-AuthenticationFailure $Exception) { return 'TlsValidationFailed' }
    if ($Exception -is [System.Threading.Tasks.TaskCanceledException]) { return 'TimedOut' }
    if ($Exception -is [System.Net.Http.HttpRequestException]) { return 'HttpsRequestFailed' }
    if ($Exception -is [System.UnauthorizedAccessException]) { return 'AccessDenied' }
    return $Exception.GetType().Name
}

$result = [ordered]@{
    Mode = if ($Plan) { 'Plan' } else { 'Run' }
    Status = 'NotStarted'
    Preflight = 'NotRun'
    AgentService = 'Unknown'
    GuardService = 'Unknown'
    WindowsFirewallBefore = $false
    WindowsFirewallAfter = $null
    SunshineBefore = 'Unknown'
    SunshineAfter = 'Unknown'
    FirewallBlockBefore = $false
    FirewallBlockAfter = $null
    PortOpen = $false
    SunshineStateDuringPortWait = 'NotChecked'
    GuardDuringPortWait = 'NotChecked'
    ApiStatus = 'NotAttempted'
    FailureCategory = $null
    FailureDetail = $null
    CleanupStatus = 'NotNeeded'
}

$startAttempted = $false
$initialSunshineState = $null
$httpClient = $null
$httpHandler = $null

try {
    if ($env:OS -ne 'Windows_NT') { throw [PlatformNotSupportedException]::new() }

    $agent = Get-Service -Name 'KamenRaider Agent' -ErrorAction Stop
    $guard = Get-Service -Name 'KamenRaider Stream Guard' -ErrorAction Stop
    $sunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop

    $result.AgentService = $agent.Status.ToString()
    $result.GuardService = $guard.Status.ToString()
    $result.SunshineBefore = $sunshine.Status.ToString()
    $initialSunshineState = $sunshine.Status.ToString()
    $result.WindowsFirewallBefore = Test-WindowsFirewallReady
    $result.FirewallBlockBefore = Test-ManagedSunshineBlock

    if ($result.AgentService -ne 'Running' -or $result.GuardService -ne 'Running') {
        $result.Preflight = 'Failed'
        $result.Status = 'PreflightFailed'
        $result.FailureCategory = 'AgentOrGuardNotRunning'
    }
    elseif (-not $result.WindowsFirewallBefore) {
        $result.Preflight = 'Failed'
        $result.Status = 'PreflightFailed'
        $result.FailureCategory = 'WindowsFirewallNotReady'
    }
    elseif (-not $result.FirewallBlockBefore) {
        $result.Preflight = 'Failed'
        $result.Status = 'PreflightFailed'
        $result.FailureCategory = 'ManagedBlockNotVerified'
    }
    elseif ($initialSunshineState -notin @('Stopped', 'Running')) {
        $result.Preflight = 'Failed'
        $result.Status = 'PreflightFailed'
        $result.FailureCategory = 'SunshineServiceNotStable'
    }
    else {
        $result.Preflight = 'Passed'

        if ($Plan) {
            $result.Status = 'PlanOnly'
        }
        elseif (-not (Test-IsAdministrator)) {
            $result.Status = 'ElevationRequired'
            $result.FailureCategory = 'RunFromElevatedPowerShell'
        }
        else {
            if ($initialSunshineState -eq 'Stopped') {
                $startAttempted = $true
                $result.CleanupStatus = 'WillRestoreStopped'
                Start-Service -Name $script:SunshineServiceName -ErrorAction Stop
            }

            $serviceDeadline = [DateTime]::UtcNow.AddSeconds(20)
            do {
                $sunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop
                if ($sunshine.Status -eq 'Running') { break }
                if ([DateTime]::UtcNow -ge $serviceDeadline) {
                    $result.Status = 'SunshineStartTimedOut'
                    $result.FailureCategory = 'ServiceStartTimeout'
                    break
                }
                Start-Sleep -Milliseconds 250
            } while ($true)

            if ($result.Status -eq 'NotStarted') {
                $result.WindowsFirewallAfter = Test-WindowsFirewallReady
                if (-not $result.WindowsFirewallAfter) {
                    $result.Status = 'FirewallPreconditionChanged'
                    $result.FailureCategory = 'WindowsFirewallNotReady'
                }
                elseif (-not (Test-ManagedSunshineBlock)) {
                    $result.Status = 'FirewallPreconditionChanged'
                    $result.FailureCategory = 'ManagedBlockNotVerified'
                }
                elseif ((Get-Service -Name 'KamenRaider Agent').Status -ne 'Running' -or
                    (Get-Service -Name 'KamenRaider Stream Guard').Status -ne 'Running') {
                    $result.Status = 'AgentOrGuardStopped'
                    $result.FailureCategory = 'ServicePreconditionChanged'
                }
                else {
                    $portDeadline = [DateTime]::UtcNow.AddSeconds(20)
                    while ([DateTime]::UtcNow -lt $portDeadline) {
                        $tcpClient = New-Object System.Net.Sockets.TcpClient
                        try {
                            $connectTask = $tcpClient.ConnectAsync('localhost', 47990)
                            if ($connectTask.Wait(600)) {
                                $connectTask.GetAwaiter().GetResult()
                                $result.PortOpen = $true
                                break
                            }
                        }
                        catch { }
                        finally { $tcpClient.Dispose() }
                        $sunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop
                        if (Set-SunshinePortWaitFailure -SunshineState $sunshine.Status.ToString()) { break }
                        Start-Sleep -Milliseconds 250
                    }

                    if (-not $result.PortOpen) {
                        if ($result.Status -eq 'NotStarted') {
                            $result.Status = 'ApiPortTimedOut'
                            $result.FailureCategory = 'LoopbackPortTimeout'
                        }
                    }
                    else {
                        $result.WindowsFirewallAfter = Test-WindowsFirewallReady
                        if (-not $result.WindowsFirewallAfter) {
                            $result.Status = 'FirewallPreconditionChanged'
                            $result.FailureCategory = 'WindowsFirewallNotReady'
                        }
                        elseif (-not (Test-ManagedSunshineBlock)) {
                            $result.Status = 'FirewallPreconditionChanged'
                            $result.FailureCategory = 'ManagedBlockNotVerified'
                        }
                        elseif ((Get-Service -Name 'KamenRaider Agent').Status -ne 'Running' -or
                            (Get-Service -Name 'KamenRaider Stream Guard').Status -ne 'Running') {
                            $result.Status = 'AgentOrGuardStopped'
                            $result.FailureCategory = 'ServicePreconditionChanged'
                        }
                        else {
                            Add-Type -AssemblyName System.Net.Http
                            $httpHandler = New-Object System.Net.Http.HttpClientHandler
                            $httpHandler.UseProxy = $false
                            $httpHandler.AllowAutoRedirect = $false
                            # Leave the certificate callback unset so Windows/.NET performs normal TLS validation.
                            $httpClient = New-Object System.Net.Http.HttpClient($httpHandler)
                            $httpClient.Timeout = [TimeSpan]::FromSeconds(8)

                            try {
                                $response = $httpClient.GetAsync($script:ApiUri).GetAwaiter().GetResult()
                                $result.ApiStatus = 'HTTP ' + [int]$response.StatusCode
                                $result.Status = 'RequestCompleted'
                                $response.Dispose()
                            }
                            catch {
                                $result.Status = 'RequestFailed'
                                $result.ApiStatus = Get-SanitizedFailureCategory $_.Exception
                                $result.FailureCategory = $result.ApiStatus
                            }
                        }
                    }
                }
            }
        }
    }
}
catch {
    $result.Status = 'ProbeFailed'
    $result.FailureCategory = Get-SanitizedFailureCategory $_.Exception
}
finally {
    if ($null -ne $httpClient) { $httpClient.Dispose() }
    if ($null -ne $httpHandler) { $httpHandler.Dispose() }

    if (-not $Plan -and $startAttempted) {
        try {
            $sunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop
            if ($sunshine.Status -ne 'Stopped') {
                Stop-Service -Name $script:SunshineServiceName -ErrorAction Stop
            }

            $stopDeadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                $sunshine = Get-Service -Name $script:SunshineServiceName -ErrorAction Stop
                if ($sunshine.Status -eq 'Stopped') { break }
                if ([DateTime]::UtcNow -ge $stopDeadline) { throw [TimeoutException]::new() }
                Start-Sleep -Milliseconds 250
            } while ($true)
            $result.CleanupStatus = 'RestoredStopped'
        }
        catch {
            $result.CleanupStatus = 'StopFailed:' + (Get-SanitizedFailureCategory $_.Exception)
        }
    }

    try { $result.SunshineAfter = (Get-Service -Name $script:SunshineServiceName -ErrorAction Stop).Status.ToString() }
    catch { $result.SunshineAfter = 'Unavailable' }
    $result.WindowsFirewallAfter = Test-WindowsFirewallReady
    try { $result.FirewallBlockAfter = Test-ManagedSunshineBlock }
    catch { $result.FirewallBlockAfter = $false }

    [pscustomobject]$result
}
