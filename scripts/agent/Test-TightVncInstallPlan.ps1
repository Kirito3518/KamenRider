$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'Install-TightVncServer.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('kr-tightvnc-plan-' + [Guid]::NewGuid().ToString('N'))
$dataDirectory = Join-Path $root 'AgentData'
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $tightVncPlan = . $installer -AllowedSourceAddress '127.0.0.1' -DataDirectory $dataDirectory -Plan
    if ($tightVncPlan.ProductVersion -ne '2.8.90' -or $tightVncPlan.Architecture -ne 'x64') { throw 'TightVNC plan must pin supported product version and architecture.' }
    if ($tightVncPlan.InstallerUrl -cne 'https://www.tightvnc.com/download/2.8.90/tightvnc-2.8.90-gpl-setup-64bit.msi' -or $tightVncPlan.InstallerBytes -ne 2543616) { throw 'TightVNC plan must pin the official x64 2.8.90 MSI and published byte size.' }
    foreach ($expected in @{
        ADDLOCAL = 'Server'
        SERVER_REGISTER_AS_SERVICE = '1'
        SERVER_ADD_FIREWALL_EXCEPTION = '0'
        SET_RFBPORT = '1'
        VALUE_OF_RFBPORT = '5900'
        SET_ACCEPTHTTPCONNECTIONS = '1'
        VALUE_OF_ACCEPTHTTPCONNECTIONS = '0'
        SET_BLOCKREMOTEINPUT = '1'
        VALUE_OF_BLOCKREMOTEINPUT = '1'
        SET_USEVNCAUTHENTICATION = '1'
        VALUE_OF_USEVNCAUTHENTICATION = '1'
        SET_VIEWONLYPASSWORD = '1'
    }.GetEnumerator()) {
        if ([string]$tightVncPlan.MsiProperties[$expected.Key] -cne [string]$expected.Value) { throw "TightVNC MSI property $($expected.Key) was not planned safely." }
    }
    if ($tightVncPlan.MsiProperties.Contains('VALUE_OF_VIEWONLYPASSWORD') -or $tightVncPlan.MsiProperties.Contains('VALUE_OF_PASSWORD')) { throw 'Plan must never expose a generated VNC credential.' }
    if ($tightVncPlan.Firewall.Direction -ne 'Inbound' -or $tightVncPlan.Firewall.Action -ne 'Allow' -or $tightVncPlan.Firewall.Protocol -ne 'TCP' -or
        $tightVncPlan.Firewall.LocalPort -ne 5900 -or $tightVncPlan.Firewall.RemoteAddress -ne '127.0.0.1' -or $tightVncPlan.Firewall.Profile -ne 'Private') {
        throw 'VNC firewall plan must be limited to Controller IPv4, TCP 5900 and the Private profile.'
    }
    if ($tightVncPlan.SecretPath -ne (Join-Path $dataDirectory 'RemoteDesktop\vnc-password.txt') -or
        @($tightVncPlan.SecretAcl).Count -ne 2 -or 'SYSTEM' -notin $tightVncPlan.SecretAcl -or 'Administrators' -notin $tightVncPlan.SecretAcl) {
        throw 'VNC password plan must use the local private Agent data directory with only SYSTEM and Administrators access.'
    }
    if (Test-Path -LiteralPath $dataDirectory) { throw 'TightVNC plan must not create the Agent data directory.' }

    $privatePlan = . $installer -AllowedSourceAddress '192.168.50.4' -DataDirectory $dataDirectory -Plan
    if ($privatePlan.Firewall.RemoteAddress -ne '192.168.50.4') { throw 'VNC firewall plan must preserve the exact Controller source IPv4.' }
    foreach ($invalid in @('203.0.113.4', '192.168.1.0/24', '::1', '0.0.0.0', '169.254.1.3')) {
        $rejected = $false
        try { . $installer -AllowedSourceAddress $invalid -DataDirectory $dataDirectory -Plan | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Unsafe Controller source address was accepted: $invalid" }
    }

    if ((Get-TightVncInstallDecision -InstalledVersions @() -ManagedInstall $false) -ne 'Install') { throw 'Missing TightVNC must select a clean install.' }
    if ((Get-TightVncInstallDecision -InstalledVersions @('2.8.90') -ManagedInstall $true) -ne 'RepairFirewall') { throw 'A KamenRaider-managed 2.8.90 installation must preserve its credential and repair firewall scope.' }
    foreach ($case in @(
        @{ Versions = @('2.8.90'); Managed = $false },
        @{ Versions = @('2.8.89'); Managed = $true },
        @{ Versions = @('4.0.2'); Managed = $true }
    )) {
        $rejected = $false
        try { Get-TightVncInstallDecision -InstalledVersions $case.Versions -ManagedInstall $case.Managed | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Unmanaged or incompatible TightVNC installation was accepted: $($case | ConvertTo-Json -Compress)" }
    }

    $validMetadata = [pscustomobject]@{ Bytes = 2543616L; SignatureStatus = 'Valid'; ProductName = 'TightVNC'; ProductVersion = '2.8.90'; Manufacturer = 'GlavSoft LLC'; Template = 'x64;1033' }
    Assert-TightVncInstallerMetadata $validMetadata
    foreach ($badMetadata in @(
        [pscustomobject]@{ Bytes = 2543615L; SignatureStatus = 'Valid'; ProductName = 'TightVNC'; ProductVersion = '2.8.90'; Manufacturer = 'GlavSoft LLC'; Template = 'x64;1033' },
        [pscustomobject]@{ Bytes = 2543616L; SignatureStatus = 'NotSigned'; ProductName = 'TightVNC'; ProductVersion = '2.8.90'; Manufacturer = 'GlavSoft LLC'; Template = 'x64;1033' },
        [pscustomobject]@{ Bytes = 2543616L; SignatureStatus = 'Valid'; ProductName = 'TightVNC'; ProductVersion = '2.8.89'; Manufacturer = 'GlavSoft LLC'; Template = 'x64;1033' },
        [pscustomobject]@{ Bytes = 2543616L; SignatureStatus = 'Valid'; ProductName = 'Other Product'; ProductVersion = '2.8.90'; Manufacturer = 'GlavSoft LLC'; Template = 'x64;1033' },
        [pscustomobject]@{ Bytes = 2543616L; SignatureStatus = 'Valid'; ProductName = 'TightVNC'; ProductVersion = '2.8.90'; Manufacturer = 'Unknown Publisher'; Template = 'x64;1033' },
        [pscustomobject]@{ Bytes = 2543616L; SignatureStatus = 'Valid'; ProductName = 'TightVNC'; ProductVersion = '2.8.90'; Manufacturer = 'GlavSoft LLC'; Template = 'Intel;1033' }
    )) {
        $rejected = $false
        try { Assert-TightVncInstallerMetadata $badMetadata } catch { $rejected = $true }
        if (-not $rejected) { throw "Untrusted TightVNC MSI metadata was accepted: $($badMetadata | ConvertTo-Json -Compress)" }
    }
    Write-Output 'TightVNC installer plan, source firewall scope, product decision and MSI trust tests passed; no MSI or service was installed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-tightvnc-plan-*') { throw 'Unsafe TightVNC plan test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
