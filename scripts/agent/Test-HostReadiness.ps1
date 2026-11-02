[CmdletBinding()]
param([switch]$Json)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows is required.' }

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$sunshineService = Get-CimInstance Win32_Service -Filter "Name='SunshineService'" -ErrorAction Stop
$controllerService = Get-Service -Name 'KamenRaider Controller' -ErrorAction SilentlyContinue
$agentService = Get-Service -Name 'KamenRaider Agent' -ErrorAction SilentlyContinue
$sunshineExecutable = 'C:\Program Files\Sunshine\Sunshine.exe'
$sunshineVersion = if (Test-Path -LiteralPath $sunshineExecutable -PathType Leaf) {
    (Get-Item -LiteralPath $sunshineExecutable).VersionInfo.ProductVersion
} else { $null }

$wideSunshineAllows = @(
    Get-NetFirewallApplicationFilter -Program $sunshineExecutable -ErrorAction Stop |
        Get-NetFirewallRule |
        Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' } |
        ForEach-Object {
            $rule = $_
            $port = $rule | Get-NetFirewallPortFilter
            if ($port.LocalPort -eq 'Any') {
                [pscustomobject]@{
                    Name = $rule.DisplayName
                    Protocol = [string]$port.Protocol
                    Profile = [string]$rule.Profile
                }
            }
        }
)

$certificateCandidates = @(
    Get-ChildItem Cert:\LocalMachine\My -ErrorAction Stop |
        Where-Object { $_.HasPrivateKey -and $_.NotBefore -le (Get-Date) -and $_.NotAfter -gt (Get-Date) }
)

# This is an inventory only. A valid certificate candidate does not prove
# hostname matching, service-account access, or a trusted Agent chain.
$report = [pscustomobject]@{
    Elevated = $elevated
    ControllerServiceInstalled = $null -ne $controllerService
    AgentServiceInstalled = $null -ne $agentService
    SunshineServiceState = if ($sunshineService) { $sunshineService.State } else { 'NotFound' }
    SunshineServiceStartMode = if ($sunshineService) { $sunshineService.StartMode } else { 'NotFound' }
    SunshineVersion = $sunshineVersion
    WideSunshineInboundAllowRules = $wideSunshineAllows
    LocalMachinePrivateCertificateCandidateCount = $certificateCandidates.Count
    StreamFenceQualified = $false
    ReadyForRental = $false
}
if ($Json) { $report | ConvertTo-Json -Depth 5 } else { $report }
