$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'Install-TightVncServer.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('kr-tightvnc-plan-' + [Guid]::NewGuid().ToString('N'))
$dataDirectory = Join-Path $root 'AgentData'
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $tightVncPlan = . $installer -AllowedSourceAddress '192.168.1.10' -DataDirectory $dataDirectory -Plan
    foreach ($functionName in @('Merge-TightVncHiddenPropertyList', 'Test-WindowsInstallerCommandLineLoggingEnabled', 'Get-TightVncServiceFailureDisposition', 'Test-TightVncFailureStopRequired')) {
        if (-not (Get-Command $functionName -ErrorAction SilentlyContinue)) { throw "TightVNC security helper is missing: $functionName" }
    }
    $mergedHiddenProperties = Merge-TightVncHiddenPropertyList -PropertyList 'ExecSecureObjects_64;VALUE_OF_PASSWORD' -AdditionalProperty @('VALUE_OF_PASSWORD', 'VALUE_OF_VIEWONLYPASSWORD')
    if ($mergedHiddenProperties -cne 'ExecSecureObjects_64;VALUE_OF_PASSWORD;VALUE_OF_VIEWONLYPASSWORD') {
        throw 'TightVNC hidden-property merge must preserve existing properties and add both password properties once.'
    }
    foreach ($debugCase in @(
        @{ Value = 0; LogsCommandLine = $false },
        @{ Value = 3; LogsCommandLine = $false },
        @{ Value = 4; LogsCommandLine = $true },
        @{ Value = 5; LogsCommandLine = $true },
        @{ Value = 6; LogsCommandLine = $true },
        @{ Value = 7; LogsCommandLine = $true }
    )) {
        if ((Test-WindowsInstallerCommandLineLoggingEnabled -DebugValue $debugCase.Value) -ne $debugCase.LogsCommandLine) {
            throw "Windows Installer Debug policy bitmask was misread: $($debugCase.Value)"
        }
    }
    if ((Get-TightVncServiceFailureDisposition -Service $null) -ne 'Missing' -or
        (Get-TightVncServiceFailureDisposition -Service ([pscustomobject]@{ Status = 'Stopped' })) -ne 'Stopped' -or
        (Get-TightVncServiceFailureDisposition -Service ([pscustomobject]@{ Status = 'Running' })) -ne 'Running') {
        throw 'TightVNC repair failure messaging must distinguish a missing, stopped, or still-running service.'
    }
    foreach ($stopCase in @(
        @{ Decision = 'RepairFirewall'; MsiAttempted = $false; RepairMutation = $false; Expected = $false },
        @{ Decision = 'RepairFirewall'; MsiAttempted = $false; RepairMutation = $true; Expected = $true },
        @{ Decision = 'Install'; MsiAttempted = $false; RepairMutation = $false; Expected = $false },
        @{ Decision = 'Install'; MsiAttempted = $true; RepairMutation = $false; Expected = $true }
    )) {
        if ((Test-TightVncFailureStopRequired -Decision $stopCase.Decision -MsiInstallAttempted $stopCase.MsiAttempted -RepairMutationStarted $stopCase.RepairMutation) -ne $stopCase.Expected) {
            throw "TightVNC failure cleanup would stop an unmodified service: $($stopCase | ConvertTo-Json -Compress)"
        }
    }
    if ($tightVncPlan.ProductVersion -ne '2.8.90' -or $tightVncPlan.Architecture -ne 'x64') { throw 'TightVNC plan must pin supported product version and architecture.' }
    if ($tightVncPlan.InstallerUrl -cne 'https://www.tightvnc.com/download/2.8.90/tightvnc-2.8.90-gpl-setup-64bit.msi' -or
        $tightVncPlan.InstallerBytes -ne 2543616 -or $tightVncPlan.InstallerSha256 -cne '9693CB4CDC2E7E482E8E3F5890415B57EF87DE3369FA5C337DB516218A1170CE') {
        throw 'TightVNC plan must pin the official x64 2.8.90 MSI URL, byte size, and SHA-256.'
    }
    if ($tightVncPlan.StageMsiProperties.SERVER_REGISTER_AS_SERVICE -ne '0' -or
        $tightVncPlan.StageMsiProperties.VALUE_OF_ACCEPTRFBCONNECTIONS -ne '0' -or
        $tightVncPlan.StageMsiProperties.VALUE_OF_IPACCESSCONTROL -ne '0.0.0.0-255.255.255.255:1' -or
        $tightVncPlan.RequiresWindowsFirewallPrivateProfile -ne $true) {
        throw 'TightVNC staging must keep the service unregistered and deny every connection until the scoped firewall rule is installed.'
    }
    if ($tightVncPlan.HiddenPropertiesTransformRequired -ne $true -or
        $tightVncPlan.HiddenPropertiesTransformPath -ne (Join-Path $dataDirectory 'RemoteDesktop\tightvnc-hidden-properties.mst') -or
        @($tightVncPlan.MsiHiddenPasswordProperties).Count -ne 2 -or
        'VALUE_OF_PASSWORD' -notin $tightVncPlan.MsiHiddenPasswordProperties -or
        'VALUE_OF_VIEWONLYPASSWORD' -notin $tightVncPlan.MsiHiddenPasswordProperties -or
        $tightVncPlan.RejectWindowsInstallerDebugCommandLineLogging -ne $true -or
        $tightVncPlan.MsiProperties.Contains('MsiHiddenProperties')) {
        throw 'TightVNC password properties must be hidden by an authored MSI transform and command-line property logging must be refused.'
    }
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
        $tightVncPlan.Firewall.LocalPort -ne 5900 -or $tightVncPlan.Firewall.RemoteAddress -ne '192.168.1.10' -or $tightVncPlan.Firewall.Profile -ne 'Private') {
        throw 'VNC firewall plan must be limited to Controller IPv4, TCP 5900 and the Private profile.'
    }
    if ($tightVncPlan.SecretPath -ne (Join-Path $dataDirectory 'RemoteDesktop\vnc-password.txt') -or
        @($tightVncPlan.SecretAcl).Count -ne 2 -or 'SYSTEM' -notin $tightVncPlan.SecretAcl -or 'Administrators' -notin $tightVncPlan.SecretAcl) {
        throw 'VNC password plan must use the local private Agent data directory with only SYSTEM and Administrators access.'
    }
    if (Test-Path -LiteralPath $dataDirectory) { throw 'TightVNC plan must not create the Agent data directory.' }

    $installerSource = [IO.File]::ReadAllText($installer)
    $msiFunctionStart = $installerSource.IndexOf('function Invoke-TightVncMsiInstall {', [StringComparison]::Ordinal)
    $msiFunctionEnd = $installerSource.IndexOf('function Invoke-TightVncMsiUninstall {', $msiFunctionStart, [StringComparison]::Ordinal)
    if ($msiFunctionStart -lt 0 -or $msiFunctionEnd -le $msiFunctionStart) { throw 'TightVNC MSI installation helper is missing.' }
    $msiFunctionSource = $installerSource.Substring($msiFunctionStart, $msiFunctionEnd - $msiFunctionStart)
    if (-not $msiFunctionSource.Contains('WindowsInstallerNative]::InstallProduct') -or $msiFunctionSource.Contains('Start-Process')) {
        throw 'The VNC password must not be placed in a child msiexec process command line.'
    }
    $stageInstallPosition = $installerSource.IndexOf('Invoke-TightVncMsiInstall -InstallerPath $installerPath -TransformPath $transformPath -Password $password -StageOnly', [StringComparison]::Ordinal)
    $scopedFirewallPosition = $installerSource.IndexOf('Set-TightVncFirewallRule -Source $script:ValidatedVncSource -Program $serverPath', $stageInstallPosition, [StringComparison]::Ordinal)
    $finalInstallPosition = $installerSource.IndexOf('Invoke-TightVncMsiInstall -InstallerPath $installerPath -TransformPath $transformPath -Password $password -ProductCode', $scopedFirewallPosition, [StringComparison]::Ordinal)
    $serviceStartPosition = $installerSource.IndexOf('Start-TightVncService', $finalInstallPosition, [StringComparison]::Ordinal)
    if ($stageInstallPosition -lt 0 -or $scopedFirewallPosition -lt $stageInstallPosition -or
        $finalInstallPosition -lt $scopedFirewallPosition -or $serviceStartPosition -lt $finalInstallPosition) {
        throw 'Fresh TightVNC setup must stage deny-all without a service, add the exact firewall rule, then install and start the service.'
    }
    Initialize-WindowsInstallerNativeType
    if (-not ('KamenRaider.Install.WindowsInstallerNative' -as [type])) { throw 'In-process Windows Installer API binding did not load.' }
    if ($installerSource -notmatch 'MsiConfigureProductExW' -or -not $installerSource.Contains('WindowsInstallerNative]::ConfigureProduct')) {
        throw 'Final MSI properties must use MsiConfigureProductEx for an installed product.'
    }
    if (-not $installerSource.Contains('GenerateTransform') -or -not $installerSource.Contains('CreateTransformSummaryInfo') -or
        -not $installerSource.Contains('ApplyTransform') -or -not $installerSource.Contains('TRANSFORMSSECURE=1') -or
        -not $installerSource.Contains('$restoreWindow = $window') -or
        -not $installerSource.Contains('ref IntPtr window') -or
        -not $installerSource.Contains('repair failed before VNC service or firewall changes') -or
        -not $installerSource.Contains('fresh install rollback failed and the VNC service is not confirmed stopped') -or
        -not $installerSource.Contains('(-not $msiInstalledThisRun -or $msiRollbackSucceeded)') -or
        -not $installerSource.Contains('Windows Installer Debug policy bit 0x04')) {
        throw 'Password hiding must be authored in a product-validated secure MSI transform, restore the prior Windows Installer UI owner, and preserve it when rollback fails.'
    }

    $privatePlan = . $installer -AllowedSourceAddress '192.168.50.4' -DataDirectory $dataDirectory -Plan
    if ($privatePlan.Firewall.RemoteAddress -ne '192.168.50.4') { throw 'VNC firewall plan must preserve the exact Controller source IPv4.' }
    foreach ($invalid in @('203.0.113.4', '192.168.1.0/24', '::1', '127.0.0.1', '0.0.0.0', '169.254.1.3')) {
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

    $validMetadata = [pscustomobject]@{ Bytes = 2543616L; Sha256 = '9693CB4CDC2E7E482E8E3F5890415B57EF87DE3369FA5C337DB516218A1170CE'; SignatureStatus = 'Valid'; SignerSubject = 'E=info@glavsoft.com, CN=OOO GlavSoft, O=OOO GlavSoft, L=Tomsk, S=Tomsk Oblast, C=RU'; ProductName = 'TightVNC'; ProductVersion = '2.8.90.0'; Manufacturer = 'GlavSoft LLC.'; Template = 'x64;1033' }
    Assert-TightVncInstallerMetadata $validMetadata
    foreach ($badMetadata in @(
        [pscustomobject]@{ Bytes = 2543615L; Sha256 = '9693CB4CDC2E7E482E8E3F5890415B57EF87DE3369FA5C337DB516218A1170CE'; SignatureStatus = 'Valid'; SignerSubject = 'CN=OOO GlavSoft, O=OOO GlavSoft'; ProductName = 'TightVNC'; ProductVersion = '2.8.90.0'; Manufacturer = 'GlavSoft LLC.'; Template = 'x64;1033' },
        [pscustomobject]@{ Bytes = 2543616L; Sha256 = '9693CB4CDC2E7E482E8E3F5890415B57EF87DE3369FA5C337DB516218A1170CE'; SignatureStatus = 'NotSigned'; SignerSubject = 'CN=OOO GlavSoft, O=OOO GlavSoft'; ProductName = 'TightVNC'; ProductVersion = '2.8.90.0'; Manufacturer = 'GlavSoft LLC.'; Template = 'x64;1033' },
        [pscustomobject]@{ Bytes = 2543616L; Sha256 = '9693CB4CDC2E7E482E8E3F5890415B57EF87DE3369FA5C337DB516218A1170CE'; SignatureStatus = 'Valid'; SignerSubject = 'CN=OOO GlavSoft, O=OOO GlavSoft'; ProductName = 'TightVNC'; ProductVersion = '2.8.89'; Manufacturer = 'GlavSoft LLC.'; Template = 'x64;1033' },
        [pscustomobject]@{ Bytes = 2543616L; Sha256 = '9693CB4CDC2E7E482E8E3F5890415B57EF87DE3369FA5C337DB516218A1170CE'; SignatureStatus = 'Valid'; SignerSubject = 'CN=OOO GlavSoft, O=OOO GlavSoft'; ProductName = 'Other Product'; ProductVersion = '2.8.90.0'; Manufacturer = 'GlavSoft LLC.'; Template = 'x64;1033' },
        [pscustomobject]@{ Bytes = 2543616L; Sha256 = '9693CB4CDC2E7E482E8E3F5890415B57EF87DE3369FA5C337DB516218A1170CE'; SignatureStatus = 'Valid'; SignerSubject = 'CN=Unknown Publisher, O=Unknown Publisher'; ProductName = 'TightVNC'; ProductVersion = '2.8.90.0'; Manufacturer = 'GlavSoft LLC.'; Template = 'x64;1033' },
        [pscustomobject]@{ Bytes = 2543616L; Sha256 = '9693CB4CDC2E7E482E8E3F5890415B57EF87DE3369FA5C337DB516218A1170CE'; SignatureStatus = 'Valid'; SignerSubject = 'CN=OOO GlavSoft, O=OOO GlavSoft'; ProductName = 'TightVNC'; ProductVersion = '2.8.90.0'; Manufacturer = 'GlavSoft LLC.'; Template = 'Intel;1033' },
        [pscustomobject]@{ Bytes = 2543616L; Sha256 = ('0' * 64); SignatureStatus = 'Valid'; SignerSubject = 'CN=OOO GlavSoft, O=OOO GlavSoft'; ProductName = 'TightVNC'; ProductVersion = '2.8.90.0'; Manufacturer = 'GlavSoft LLC.'; Template = 'x64;1033' }
    )) {
        $rejected = $false
        try { Assert-TightVncInstallerMetadata $badMetadata } catch { $rejected = $true }
        if (-not $rejected) { throw "Untrusted TightVNC MSI metadata was accepted: $($badMetadata | ConvertTo-Json -Compress)" }
    }
    foreach ($footprintCase in @(
        @{ Versions = @(); Managed = $false; Footprint = $false; Expected = 'Install' },
        @{ Versions = @('2.8.90'); Managed = $true; Footprint = $true; Expected = 'RepairFirewall' }
    )) {
        if ((Get-TightVncInstallDecision -InstalledVersions $footprintCase.Versions -ManagedInstall $footprintCase.Managed -InstallationFootprintDetected $footprintCase.Footprint) -ne $footprintCase.Expected) {
            throw "TightVNC install footprint decision was incorrect: $($footprintCase | ConvertTo-Json -Compress)"
        }
    }
    $orphanServiceRejected = $false
    try { Get-TightVncInstallDecision -InstalledVersions @() -ManagedInstall $false -InstallationFootprintDetected $true | Out-Null } catch { $orphanServiceRejected = $true }
    if (-not $orphanServiceRejected) { throw 'An orphan TightVNC service or installation directory must be rejected.' }
    Write-Output 'TightVNC installer plan, source firewall scope, product decision and MSI trust tests passed; no MSI or service was installed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-tightvnc-plan-*') { throw 'Unsafe TightVNC plan test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
