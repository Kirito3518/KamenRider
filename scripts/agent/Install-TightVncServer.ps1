[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)][string]$AllowedSourceAddress,
    [string]$DataDirectory = (Join-Path $env:ProgramData 'KamenRaider\Agent'),
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
$script:TightVncVersion = '2.8.90'
# The admin VNC viewer auto-authenticates with this reviewed value, so opening the
# screen never prompts for a password. VNC remains source-restricted to the Mother
# address and is still only reachable through the authenticated admin gateway.
$script:TightVncViewerPassword = 'KamenVNC'
$script:TightVncInstallerUrl = 'https://www.tightvnc.com/download/2.8.90/tightvnc-2.8.90-gpl-setup-64bit.msi'
$script:TightVncInstallerBytes = 2543616L
$script:TightVncInstallerSha256 = '9693CB4CDC2E7E482E8E3F5890415B57EF87DE3369FA5C337DB516218A1170CE'
$script:TightVncRuleName = 'KamenRaiderAdminVnc'
$script:TightVncRuleDisplayName = 'KamenRaider Admin VNC'

function ConvertTo-AllowedVncSourceAddress {
    param([Parameter(Mandatory)][string]$Address)
    $parsed = $null
    if (-not [Net.IPAddress]::TryParse($Address, [ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'AllowedSourceAddress must be one RFC1918 private IPv4 address.'
    }
    $bytes = $parsed.GetAddressBytes()
    $private = $bytes[0] -eq 10 -or
        ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
    if (-not $private -or $parsed.ToString() -cne $Address) {
        throw 'AllowedSourceAddress must be one RFC1918 private IPv4 address.'
    }
    return $parsed.ToString()
}

function Get-TightVncInstallPlan {
    param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$DataPath)
    $secretDirectory = Join-Path $DataPath 'RemoteDesktop'
    $sourceRule = "$Source-$Source`:0,0.0.0.0-255.255.255.255`:1"
    [pscustomobject]@{
        ProductVersion = $script:TightVncVersion
        Architecture = 'x64'
        InstallerUrl = $script:TightVncInstallerUrl
        InstallerBytes = $script:TightVncInstallerBytes
        InstallerSha256 = $script:TightVncInstallerSha256
        InstallerBundled = $false
        RequiresWindowsFirewallPrivateProfile = $true
        MsiHiddenPasswordProperties = @('VALUE_OF_PASSWORD', 'VALUE_OF_VIEWONLYPASSWORD')
        HiddenPropertiesTransformRequired = $true
        HiddenPropertiesTransformPath = Join-Path $secretDirectory 'tightvnc-hidden-properties.mst'
        RejectWindowsInstallerDebugCommandLineLogging = $true
        DataDirectory = $DataPath
        SecretPath = Join-Path $secretDirectory 'vnc-password.txt'
        ManagedMarkerPath = Join-Path $secretDirectory 'tightvnc-managed.json'
        SecretAcl = @('SYSTEM', 'Administrators')
        MsiProperties = [ordered]@{
            ADDLOCAL = 'Server'
            SERVER_REGISTER_AS_SERVICE = '1'
            SERVER_ADD_FIREWALL_EXCEPTION = '0'
            SERVER_ALLOW_SAS = '0'
            SET_ACCEPTHTTPCONNECTIONS = '1'
            VALUE_OF_ACCEPTHTTPCONNECTIONS = '0'
            SET_ACCEPTRFBCONNECTIONS = '1'
            VALUE_OF_ACCEPTRFBCONNECTIONS = '1'
            SET_RFBPORT = '1'
            VALUE_OF_RFBPORT = '5900'
            SET_BLOCKREMOTEINPUT = '1'
            VALUE_OF_BLOCKREMOTEINPUT = '1'
            SET_USEVNCAUTHENTICATION = '1'
            VALUE_OF_USEVNCAUTHENTICATION = '1'
            SET_IPACCESSCONTROL = '1'
            VALUE_OF_IPACCESSCONTROL = $sourceRule
            SET_PASSWORD = '1'
            SET_VIEWONLYPASSWORD = '1'
        }
        PasswordLength = 8
        Firewall = [pscustomobject]@{
            Name = $script:TightVncRuleName
            DisplayName = $script:TightVncRuleDisplayName
            Direction = 'Inbound'
            Action = 'Allow'
            Protocol = 'TCP'
            LocalPort = 5900
            RemoteAddress = $Source
            Profile = 'Private'
            Program = (Join-Path $env:ProgramFiles 'TightVNC\tvnserver.exe')
        }
    }
}

function Assert-TightVncInstallerMetadata {
    param([Parameter(Mandatory)][psobject]$Metadata)
    if ([long]$Metadata.Bytes -ne $script:TightVncInstallerBytes -or [string]$Metadata.Sha256 -cne $script:TightVncInstallerSha256 -or [string]$Metadata.SignatureStatus -cne 'Valid' -or
        [string]$Metadata.ProductName -notlike 'TightVNC*' -or [string]$Metadata.ProductVersion -notin @('2.8.90', '2.8.90.0') -or
        [string]$Metadata.Manufacturer -notmatch 'GlavSoft' -or [string]$Metadata.Template -notmatch '^x64;' -or
        [string]$Metadata.SignerSubject -notmatch '(?i)(?:^|,\s*)O\s*=\s*OOO GlavSoft(?:,|$)') {
        throw 'Downloaded MSI is not the trusted-signer TightVNC 2.8.90 x64 product published by GlavSoft.'
    }
}

function Get-TightVncInstallDecision {
    param([string[]]$InstalledVersions = @(),[bool]$ManagedInstall = $false,[bool]$InstallationFootprintDetected = $false)
    $versions = @($InstalledVersions | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    if ($versions.Count -eq 0) {
        if ($InstallationFootprintDetected) { throw 'An unmanaged TightVNC service or installation footprint exists; refusing to replace it.' }
        return 'Install'
    }
    if ($versions.Count -ne 1 -or $versions[0] -notin @('2.8.90', '2.8.90.0') -or -not $ManagedInstall) {
        throw 'An unmanaged or incompatible TightVNC installation exists; refusing to replace it.'
    }
    return 'RepairFirewall'
}

function Get-TightVncInstallFootprint {
    $service = @(Get-CimInstance -ClassName Win32_Service -ErrorAction Stop | Where-Object {
        [string]$_.Name -ieq 'tvnserver' -or [string]$_.DisplayName -match '(?i)^TightVNC(?:\s|$)'
    })
    $process = @(Get-Process -Name 'tvnserver' -ErrorAction SilentlyContinue)
    $installDirectories = @((Join-Path $env:ProgramFiles 'TightVNC'))
    if (${env:ProgramFiles(x86)}) { $installDirectories += (Join-Path ${env:ProgramFiles(x86)} 'TightVNC') }
    $existingDirectories = @($installDirectories | Where-Object { Test-Path -LiteralPath $_ })
    $serverPaths = @($installDirectories | ForEach-Object { Join-Path $_ 'tvnserver.exe' })
    $existingServerPaths = @($serverPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    [pscustomobject]@{
        Detected = ($service.Count -gt 0 -or $process.Count -gt 0 -or $existingDirectories.Count -gt 0 -or $existingServerPaths.Count -gt 0)
        ServiceCount = $service.Count
        ProcessCount = $process.Count
        InstallDirectories = $installDirectories
        ExistingServerPaths = $existingServerPaths
    }
}

function Get-TightVncMsiMetadata {
    param([Parameter(Mandatory)][string]$Path)
    $installer = $null
    $database = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($Path, 0)
        $readProperty = {
            param($DatabaseObject,[string]$PropertyName)
            $view = $null
            $record = $null
            try {
                $query = "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$PropertyName'"
                $view = $DatabaseObject.OpenView($query)
                [void]$view.Execute()
                $record = $view.Fetch()
                if ($null -eq $record) { throw "TightVNC MSI is missing property $PropertyName." }
                return [string]$record.StringData(1)
            } finally {
                if ($record) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($record) }
                if ($view) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view) }
            }
        }
        $summary = $database.SummaryInformation(0)
        try {
            $signature = Get-AuthenticodeSignature -LiteralPath $Path
            [pscustomobject]@{
                Bytes = (Get-Item -LiteralPath $Path -Force).Length
                Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
                SignatureStatus = [string]$signature.Status
                SignerSubject = if ($signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { '' }
                ProductName = & $readProperty $database 'ProductName'
                ProductVersion = & $readProperty $database 'ProductVersion'
                Manufacturer = & $readProperty $database 'Manufacturer'
                ProductCode = & $readProperty $database 'ProductCode'
                Template = [string]$summary.Property(7)
            }
        } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($summary) }
    } finally {
        if ($database) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
        if ($installer) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer) }
    }
}

function Merge-TightVncHiddenPropertyList {
    param([AllowEmptyString()][string]$PropertyList,[string[]]$AdditionalProperty = @())
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $values = @($PropertyList -split ';') + @($AdditionalProperty)
    $merged = foreach ($value in $values) {
        $name = ([string]$value).Trim()
        if ($name -and $seen.Add($name)) { $name }
    }
    return ($merged -join ';')
}

function Read-TightVncMsiProperty {
    param([Parameter(Mandatory)][object]$Database,[Parameter(Mandatory)][string]$PropertyName)
    if ($PropertyName -notmatch '^[A-Za-z][A-Za-z0-9_]*$') { throw 'Invalid Windows Installer property name.' }
    $view = $null
    $record = $null
    try {
        $query = "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$PropertyName'"
        $view = $Database.OpenView($query)
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { return $null }
        return [string]$record.StringData(1)
    } finally {
        if ($record) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($record) }
        if ($view) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view) }
    }
}

function New-TightVncHiddenPropertiesTransform {
    param([Parameter(Mandatory)][string]$InstallerPath,[Parameter(Mandatory)][string]$TransformPath)
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($TransformPath))
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'TightVNC transform directory is missing.' }
    $token = [Guid]::NewGuid().ToString('N')
    $workDatabasePath = Join-Path $parent ('.tightvnc-transform-' + $token + '.msi')
    $verificationDatabasePath = Join-Path $parent ('.tightvnc-transform-verify-' + $token + '.msi')
    $temporaryTransformPath = Join-Path $parent ('.tightvnc-transform-' + $token + '.mst')
    $installer = $null
    $baseDatabase = $null
    $customDatabase = $null
    $verificationDatabase = $null
    $customView = $null
    $customRecord = $null
    try {
        Copy-Item -LiteralPath $InstallerPath -Destination $workDatabasePath -ErrorAction Stop
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $baseDatabase = $installer.OpenDatabase($InstallerPath, 0)
        $customDatabase = $installer.OpenDatabase($workDatabasePath, 1)
        $existingHiddenProperties = Read-TightVncMsiProperty -Database $customDatabase -PropertyName 'MsiHiddenProperties'
        $hiddenProperties = Merge-TightVncHiddenPropertyList -PropertyList ([string]$existingHiddenProperties) `
            -AdditionalProperty @('VALUE_OF_PASSWORD', 'VALUE_OF_VIEWONLYPASSWORD')
        $customView = $customDatabase.OpenView("SELECT ``Property``, ``Value`` FROM ``Property`` WHERE ``Property`` = 'MsiHiddenProperties'")
        [void]$customView.Execute()
        $customRecord = $customView.Fetch()
        if ($customRecord) {
            $customRecord.StringData(2) = $hiddenProperties
            [void]$customView.Modify(2, $customRecord)
        } else {
            $customRecord = $installer.CreateRecord(2)
            $customRecord.StringData(1) = 'MsiHiddenProperties'
            $customRecord.StringData(2) = $hiddenProperties
            [void]$customView.Modify(1, $customRecord)
        }
        [void]$customDatabase.Commit()
        if (-not $customDatabase.GenerateTransform($baseDatabase, $temporaryTransformPath)) {
            throw 'Windows Installer did not generate the TightVNC password-hiding transform.'
        }
        [void]$customDatabase.CreateTransformSummaryInfo($baseDatabase, $temporaryTransformPath, 0, 2)
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($customRecord)
        $customRecord = $null
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($customView)
        $customView = $null
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($customDatabase)
        $customDatabase = $null
        Copy-Item -LiteralPath $InstallerPath -Destination $verificationDatabasePath -ErrorAction Stop
        $verificationDatabase = $installer.OpenDatabase($verificationDatabasePath, 1)
        [void]$verificationDatabase.ApplyTransform($temporaryTransformPath, 0)
        $verifiedHiddenProperties = Read-TightVncMsiProperty -Database $verificationDatabase -PropertyName 'MsiHiddenProperties'
        foreach ($required in @('VALUE_OF_PASSWORD', 'VALUE_OF_VIEWONLYPASSWORD')) {
            if ($required -notin @(([string]$verifiedHiddenProperties) -split ';')) {
                throw 'TightVNC password-hiding transform did not protect both MSI password properties.'
            }
        }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($verificationDatabase)
        $verificationDatabase = $null
        if (Test-Path -LiteralPath $TransformPath) {
            Assert-TightVncPrivateFile -Path $TransformPath
            [IO.File]::Replace($temporaryTransformPath, $TransformPath, $null)
        } else {
            [IO.File]::Move($temporaryTransformPath, $TransformPath)
        }
        return [pscustomobject]@{ Path = $TransformPath; HiddenProperties = $verifiedHiddenProperties }
    } finally {
        foreach ($comObject in @($customRecord, $customView, $verificationDatabase, $customDatabase, $baseDatabase, $installer)) {
            if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($comObject)
            }
        }
        foreach ($path in @($workDatabasePath, $verificationDatabasePath, $temporaryTransformPath)) {
            if (Test-Path -LiteralPath $path) {
                $resolved = [IO.Path]::GetFullPath($path)
                $parentPrefix = [IO.Path]::GetFullPath($parent).TrimEnd('\') + '\'
                if (-not $resolved.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                    [IO.Path]::GetFileName($resolved) -notlike '.tightvnc-transform-*.msi' -and
                    [IO.Path]::GetFileName($resolved) -notlike '.tightvnc-transform-*.mst' -and
                    [IO.Path]::GetFileName($resolved) -notlike '.tightvnc-transform-verify-*.msi') {
                    throw 'Unsafe TightVNC transform temporary cleanup path.'
                }
                Remove-Item -LiteralPath $resolved -Force
            }
        }
    }
}

function Set-TightVncPrivateFileAcl {
    param([Parameter(Mandatory)][string]$Path)
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $adminSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $acl.SetOwner($adminSid)
    foreach ($sid in @($systemSid, $adminSid)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-TightVncPrivateFile -Path $Path
}

function Test-WindowsInstallerCommandLineLoggingEnabled {
    param([Parameter(Mandatory)][int]$DebugValue)
    return (($DebugValue -band 4) -ne 0)
}

function Get-TightVncServiceFailureDisposition {
    param([AllowNull()][object]$Service)
    if ($null -eq $Service) { return 'Missing' }
    return [string]$Service.Status
}

function Test-TightVncFailureStopRequired {
    param([ValidateSet('Install', 'RepairFirewall')][string]$Decision,[bool]$MsiInstallAttempted,[bool]$RepairMutationStarted)
    return ($RepairMutationStarted -or ($Decision -eq 'Install' -and $MsiInstallAttempted))
}

function Get-TightVncInstalledVersions {
    $locations = @(
        'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $products = foreach ($location in $locations) {
        Get-ItemProperty -Path $location -ErrorAction SilentlyContinue | Where-Object { [string]$_.DisplayName -match '^TightVNC(?:\s|$)' }
    }
    $versions = foreach ($product in $products) {
        $version = [string]$product.DisplayVersion
        if ([string]::IsNullOrWhiteSpace($version)) {
            if ([string]$product.DisplayName -match '2\.\d+\.\d+') { $version = $Matches[0] } else { $version = 'unknown' }
        }
        $version
    }
    return @($versions | Sort-Object -Unique)
}

function Assert-TightVncDataDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not [IO.Path]::IsPathRooted($Path)) { throw 'DataDirectory must be an absolute path.' }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Agent data directory must be a real directory.' }
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) { throw 'Agent data directory must have a protected private ACL.' }
}

function Assert-TightVncPrivateDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'VNC credential directory must be a real directory.' }
    $acl = Get-Acl -LiteralPath $Path
    $adminSid = 'S-1-5-32-544'
    $systemSid = 'S-1-5-18'
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if (-not $acl.AreAccessRulesProtected -or $owner -notin @($adminSid, $systemSid)) { throw 'VNC credential directory ACL is not private.' }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.IdentityReference.Value -notin @($adminSid, $systemSid) -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
            throw 'VNC credential directory grants access outside SYSTEM and Administrators.'
        }
    }
}

function New-TightVncPrivateDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) { Assert-TightVncPrivateDirectory $Path; return }
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $adminSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $acl.SetOwner($adminSid)
    foreach ($sid in @($systemSid, $adminSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit', [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-TightVncPrivateDirectory $Path
}

function Assert-TightVncPrivateFile {
    param([Parameter(Mandatory)][string]$Path)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'VNC credential file must be a regular file.' }
    $acl = Get-Acl -LiteralPath $Path
    $adminSid = 'S-1-5-32-544'
    $systemSid = 'S-1-5-18'
    if (-not $acl.AreAccessRulesProtected -or $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @($adminSid, $systemSid)) {
        throw 'VNC credential file ACL is not private.'
    }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.IdentityReference.Value -notin @($adminSid, $systemSid) -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
            throw 'VNC credential file grants access outside SYSTEM and Administrators.'
        }
    }
}

function Write-TightVncPrivateFile {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Contents,[switch]$Replace)
    $parent = Split-Path -Parent $Path
    Assert-TightVncPrivateDirectory $parent
    $exists = Test-Path -LiteralPath $Path
    if ($exists -and -not $Replace) { throw 'VNC credential file already exists.' }
    if ($exists) { Assert-TightVncPrivateFile $Path }
    $temporary = Join-Path $parent ('.vnc-secret-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $file = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $file.Dispose()
    try {
        $acl = [Security.AccessControl.FileSecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $adminSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        $acl.SetOwner($adminSid)
        foreach ($sid in @($systemSid, $adminSid)) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow))
        }
        Set-Acl -LiteralPath $temporary -AclObject $acl
        [IO.File]::WriteAllText($temporary, $Contents, [Text.UTF8Encoding]::new($false))
        Assert-TightVncPrivateFile $temporary
        if ($exists) { [IO.File]::Replace($temporary, $Path, $null) } else { [IO.File]::Move($temporary, $Path) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    Assert-TightVncPrivateFile $Path
}

function New-TightVncPassword {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_'
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $buffer = New-Object byte[] 256
    $characters = New-Object 'System.Collections.Generic.List[char]'
    try {
        while ($characters.Count -lt 8) {
            $random.GetBytes($buffer)
            foreach ($value in $buffer) {
                if ($value -lt 192) { $characters.Add($alphabet[[int]($value % 64)]) }
                if ($characters.Count -eq 8) { break }
            }
        }
    } finally { $random.Dispose() }
    return -join $characters.ToArray()
}

function Get-TightVncManagedMarker {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$CredentialPath)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    Assert-TightVncPrivateFile $Path
    try { $marker = [IO.File]::ReadAllText($Path) | ConvertFrom-Json -ErrorAction Stop } catch { throw 'Existing KamenRaider VNC marker is invalid.' }
    if ($marker.SchemaVersion -ne 1 -or [string]$marker.ProductVersion -notin @('2.8.90', '2.8.90.0') -or
        [string]::Equals([IO.Path]::GetFullPath([string]$marker.CredentialPath), [IO.Path]::GetFullPath($CredentialPath), [StringComparison]::OrdinalIgnoreCase) -eq $false) {
        throw 'Existing KamenRaider VNC marker does not match the supported installation.'
    }
    if (-not (Test-Path -LiteralPath $CredentialPath -PathType Leaf)) { throw 'Managed VNC credential file is missing.' }
    Assert-TightVncPrivateFile $CredentialPath
    return $marker
}

function Set-TightVncFirewallRule {
    param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Program,[switch]$Managed)
    $existing = @(Get-NetFirewallRule -Name $script:TightVncRuleName -ErrorAction SilentlyContinue)
    if ($existing.Count -and -not $Managed) { throw 'A firewall rule already uses the KamenRaider VNC rule name.' }
    if ($existing.Count) {
        foreach ($rule in $existing) {
            if ($rule.DisplayName -cne $script:TightVncRuleDisplayName -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow') {
                throw 'Existing KamenRaider VNC firewall rule has an unexpected identity.'
            }
            Remove-NetFirewallRule -Name $script:TightVncRuleName -ErrorAction Stop
        }
    }
    New-NetFirewallRule -Name $script:TightVncRuleName -DisplayName $script:TightVncRuleDisplayName -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort 5900 -RemoteAddress $Source -Profile Private -Program $Program -Enabled True -ErrorAction Stop | Out-Null
}

function Assert-TightVncService {
    $services = @(Get-CimInstance -ClassName Win32_Service -Filter "Name='tvnserver'" -ErrorAction Stop)
    if ($services.Count -ne 1 -or [string]$services[0].StartMode -ne 'Auto') { throw 'TightVNC Server service is missing or not configured for automatic startup.' }
    $expected = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'TightVNC\tvnserver.exe'))
    $match = [regex]::Match([string]$services[0].PathName, '^\s*(?:"(?<quoted>[^"]+\.exe)"|(?<plain>.*?\.exe))(?:\s|$)', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $image = if ($match.Groups['quoted'].Success) { $match.Groups['quoted'].Value } else { $match.Groups['plain'].Value }
    if (-not $match.Success -or -not [string]::Equals([IO.Path]::GetFullPath($image), $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'TightVNC Server service binary path is not the expected vendor installation.'
    }
    return Get-Service -Name 'tvnserver' -ErrorAction Stop
}

function Stop-TightVncService {
    $service = Get-Service -Name 'tvnserver' -ErrorAction SilentlyContinue
    if (-not $service) { return }
    $service = Assert-TightVncService
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name 'tvnserver' -Force -ErrorAction Stop
    }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $service = Get-Service -Name 'tvnserver' -ErrorAction SilentlyContinue
        if (-not $service -or $service.Status -eq [ServiceProcess.ServiceControllerStatus]::Stopped) { return }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt 30)
    throw 'TightVNC Server service did not stop.'
}

function Assert-TightVncPrivateFirewallEnabled {
    $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -Name Private -ErrorAction Stop)
    if ($profiles.Count -ne 1 -or -not [bool]$profiles[0].Enabled) {
        throw 'Enable the Windows Firewall Private profile before installing or configuring TightVNC.'
    }
}

function Assert-WindowsInstallerPasswordLoggingSafe {
    $policyPaths = @(
        'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Installer',
        'Registry::HKEY_CURRENT_USER\SOFTWARE\Policies\Microsoft\Windows\Installer'
    )
    foreach ($path in $policyPaths) {
        $policy = Get-ItemProperty -LiteralPath $path -Name Debug -ErrorAction SilentlyContinue
        if ($null -ne $policy) {
            $debugValue = 0
            if (-not [int]::TryParse([string]$policy.Debug, [ref]$debugValue)) { throw 'Windows Installer Debug policy value is invalid; VNC password setup was refused.' }
            if (Test-WindowsInstallerCommandLineLoggingEnabled -DebugValue $debugValue) {
                throw 'Windows Installer Debug policy bit 0x04 can log command-line properties; VNC password setup was refused.'
            }
        }
    }
}

function Start-TightVncService {
    $service = Assert-TightVncService
    if ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Stopped) { Start-Service -Name 'tvnserver' -ErrorAction Stop }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $service = Get-Service -Name 'tvnserver' -ErrorAction Stop
        if ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running) { break }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt 30)
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Running) { throw 'TightVNC Server service did not start.' }
}

function Initialize-WindowsInstallerNativeType {
    if (-not ('KamenRaider.Install.WindowsInstallerNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace KamenRaider.Install {
    public static class WindowsInstallerNative {
        [DllImport("msi.dll", EntryPoint = "MsiInstallProductW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern UInt32 InstallProduct(String packagePath, String propertyValues);

        [DllImport("msi.dll", EntryPoint = "MsiConfigureProductExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern UInt32 ConfigureProduct(String productCode, Int32 installLevel, Int32 installState, String propertyValues);

        [DllImport("msi.dll", EntryPoint = "MsiSetInternalUI", ExactSpelling = true)]
        public static extern UInt32 SetInternalUI(UInt32 level, ref IntPtr window);
    }
}
'@
    }
}

function Invoke-TightVncMsiInstall {
    param([Parameter(Mandatory)][string]$InstallerPath,[Parameter(Mandatory)][string]$TransformPath,[Parameter(Mandatory)][string]$Password,[string]$ProductCode,[switch]$FreshInstall)
    $sourceRule = "$script:ValidatedVncSource-$script:ValidatedVncSource`:0,0.0.0.0-255.255.255.255`:1"
    $properties = @(
        'ADDLOCAL=Server',
        'SERVER_REGISTER_AS_SERVICE=1',
        'SERVER_ADD_FIREWALL_EXCEPTION=0',
        'SERVER_ALLOW_SAS=0',
        'SET_ACCEPTHTTPCONNECTIONS=1', 'VALUE_OF_ACCEPTHTTPCONNECTIONS=0',
        'SET_ACCEPTRFBCONNECTIONS=1', 'VALUE_OF_ACCEPTRFBCONNECTIONS=1',
        'SET_RFBPORT=1', 'VALUE_OF_RFBPORT=5900',
        'SET_BLOCKREMOTEINPUT=1', 'VALUE_OF_BLOCKREMOTEINPUT=1',
        'SET_USEVNCAUTHENTICATION=1', 'VALUE_OF_USEVNCAUTHENTICATION=1',
        'SET_IPACCESSCONTROL=1', "VALUE_OF_IPACCESSCONTROL=$sourceRule",
        ('TRANSFORMS="' + $TransformPath + '"'), 'TRANSFORMSSECURE=1',
        'REBOOT=ReallySuppress'
    )
    $properties += @('SET_PASSWORD=1', "VALUE_OF_PASSWORD=$Password", 'SET_VIEWONLYPASSWORD=1', "VALUE_OF_VIEWONLYPASSWORD=$Password")
    Initialize-WindowsInstallerNativeType
    $window = [IntPtr]::Zero
    $previousUiLevel = [KamenRaider.Install.WindowsInstallerNative]::SetInternalUI(2, [ref]$window)
    try {
        $propertyValues = $properties -join ' '
        if ($FreshInstall) {
            $result = [KamenRaider.Install.WindowsInstallerNative]::InstallProduct($InstallerPath, $propertyValues)
        } else {
            if ($ProductCode -notmatch '^\{[A-Fa-f0-9-]{36}\}$') { throw 'TightVNC MSI product code is invalid; maintenance configuration was refused.' }
            $result = [KamenRaider.Install.WindowsInstallerNative]::ConfigureProduct($ProductCode, 0, 5, $propertyValues)
        }
    } finally {
        $restoreWindow = $window
        [void][KamenRaider.Install.WindowsInstallerNative]::SetInternalUI([uint32]$previousUiLevel, [ref]$restoreWindow)
    }
    if ($result -notin @(0, 3010)) { throw "TightVNC MSI installation failed with Windows Installer error code $result." }
    return $result
}

function Invoke-TightVncMsiUninstall {
    param([Parameter(Mandatory)][string]$ProductCode,[Parameter(Mandatory)][string]$TransformPath)
    if ($ProductCode -notmatch '^\{[A-Fa-f0-9-]{36}\}$') { throw 'TightVNC MSI product code is invalid; cleanup was refused.' }
    $exe = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    $arguments = @('/x', $ProductCode, ('TRANSFORMS="' + $TransformPath + '"'), 'TRANSFORMSSECURE=1', '/quiet', '/norestart')
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -notin @(0, 1605, 3010)) { throw "TightVNC MSI rollback failed with exit code $($process.ExitCode)." }
}

function Write-TightVncManagedMarker {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$CredentialPath,[Parameter(Mandatory)][string]$Source,[switch]$Replace)
    $json = ConvertTo-Json -InputObject ([ordered]@{ SchemaVersion = 1; ProductVersion = $script:TightVncVersion; CredentialPath = $CredentialPath; AllowedSourceAddress = $Source }) -Depth 3
    Write-TightVncPrivateFile -Path $Path -Contents $json -Replace:$Replace
}

function Remove-TightVncPrivateState {
    param([string]$CredentialPath,[string]$MarkerPath)
    foreach ($path in @($CredentialPath, $MarkerPath)) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            Assert-TightVncPrivateFile $path
            Remove-Item -LiteralPath $path -Force
        }
    }
}

$script:ValidatedVncSource = ConvertTo-AllowedVncSourceAddress $AllowedSourceAddress
if (-not [IO.Path]::IsPathRooted($DataDirectory)) { throw 'DataDirectory must be an absolute path.' }
$script:VncPlan = Get-TightVncInstallPlan -Source $script:ValidatedVncSource -DataPath ([IO.Path]::GetFullPath($DataDirectory))
if ($Plan) { $script:VncPlan; return }

if ($env:OS -ne 'Windows_NT') { throw 'Windows is required.' }
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run TightVNC setup from an elevated PowerShell session.'
}
if (-not $PSCmdlet.ShouldProcess($script:VncPlan.Firewall.Name, 'Install or repair read-only TightVNC Server and its source-restricted firewall rule')) { return }
Assert-TightVncPrivateFirewallEnabled
Assert-WindowsInstallerPasswordLoggingSafe
Assert-TightVncDataDirectory -Path $script:VncPlan.DataDirectory
$credentialDirectory = Join-Path $script:VncPlan.DataDirectory 'RemoteDesktop'
New-TightVncPrivateDirectory -Path $credentialDirectory
$credentialPath = $script:VncPlan.SecretPath
$markerPath = $script:VncPlan.ManagedMarkerPath
$marker = Get-TightVncManagedMarker -Path $markerPath -CredentialPath $credentialPath
$versions = @(Get-TightVncInstalledVersions)
$installationFootprint = Get-TightVncInstallFootprint
$decision = Get-TightVncInstallDecision -InstalledVersions $versions -ManagedInstall ([bool]$marker) `
    -InstallationFootprintDetected $installationFootprint.Detected
$serverPath = $script:VncPlan.Firewall.Program
$rule = @(Get-NetFirewallRule -Name $script:TightVncRuleName -ErrorAction SilentlyContinue)
if ($decision -eq 'Install' -and $rule.Count -gt 0 -and -not $marker) { throw 'A KamenRaider VNC firewall rule exists without its private installation marker.' }

$password = $null
$installerPath = $null
$transformPath = $script:VncPlan.HiddenPropertiesTransformPath
$metadata = $null
$msiInstalledThisRun = $false
$msiRollbackSucceeded = $false
$transformWrittenThisRun = $false
$firewallChangedThisRun = $false
$repairMutationStarted = $false
$stateWrittenThisRun = $false
$existingMarker = [bool]$marker
try {
    if ($decision -eq 'RepairFirewall') {
        $password = $script:TightVncViewerPassword
    } else {
        if ($marker) { Remove-TightVncPrivateState -CredentialPath $credentialPath -MarkerPath $markerPath; $marker = $null }
        $password = $script:TightVncViewerPassword
    }

    $installerPath = Join-Path $credentialDirectory ('.tightvnc-' + [Guid]::NewGuid().ToString('N') + '.msi')
    Invoke-WebRequest -UseBasicParsing -Uri $script:TightVncInstallerUrl -OutFile $installerPath -ErrorAction Stop
    $metadata = Get-TightVncMsiMetadata -Path $installerPath
    Assert-TightVncInstallerMetadata $metadata
    if ([string]::IsNullOrWhiteSpace([string]$metadata.ProductCode)) { throw 'TightVNC MSI ProductCode is missing.' }
    $null = New-TightVncHiddenPropertiesTransform -InstallerPath $installerPath -TransformPath $transformPath
    $transformWrittenThisRun = $true
    Set-TightVncPrivateFileAcl -Path $transformPath

    if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf) -and $decision -eq 'RepairFirewall') { throw 'Managed TightVNC Server binary is missing.' }
    if ($decision -eq 'RepairFirewall') {
        $service = Assert-TightVncService
        $repairMutationStarted = $true
        Stop-TightVncService
        Set-TightVncFirewallRule -Source $script:ValidatedVncSource -Program $serverPath -Managed
        $firewallChangedThisRun = $true
        Invoke-TightVncMsiInstall -InstallerPath $installerPath -TransformPath $transformPath -Password $password -ProductCode ([string]$metadata.ProductCode) | Out-Null
    } else {
        Set-TightVncFirewallRule -Source $script:ValidatedVncSource -Program $serverPath
        $firewallChangedThisRun = $true
        $msiInstalledThisRun = $true
        Invoke-TightVncMsiInstall -InstallerPath $installerPath -TransformPath $transformPath -Password $password -FreshInstall | Out-Null
    }

    Start-TightVncService
    if (Test-Path -LiteralPath $credentialPath) {
        Write-TightVncPrivateFile -Path $credentialPath -Contents $password -Replace
        $stateWrittenThisRun = $true
    } else {
        Write-TightVncPrivateFile -Path $credentialPath -Contents $password
        $stateWrittenThisRun = $true
    }
    Write-TightVncManagedMarker -Path $markerPath -CredentialPath $credentialPath -Source $script:ValidatedVncSource -Replace:$existingMarker
    $stateWrittenThisRun = $true
} catch {
    $original = $_
    $failureStopRequired = Test-TightVncFailureStopRequired -Decision $decision -MsiInstallAttempted $msiInstalledThisRun -RepairMutationStarted $repairMutationStarted
    if ($failureStopRequired) { try { Stop-TightVncService } catch { } }
    if ($firewallChangedThisRun) {
        Get-NetFirewallRule -Name $script:TightVncRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    }
    if ($decision -eq 'Install' -and $msiInstalledThisRun -and $metadata) {
        try {
            Invoke-TightVncMsiUninstall -ProductCode ([string]$metadata.ProductCode) -TransformPath $transformPath
            $msiRollbackSucceeded = $true
        } catch { Write-Warning 'TightVNC rollback did not finish; Windows Firewall was not left with the KamenRaider VNC rule.' }
    }
    if ($stateWrittenThisRun -and -not $existingMarker) { Remove-TightVncPrivateState -CredentialPath $credentialPath -MarkerPath $markerPath }
    if ($decision -eq 'Install' -and $transformWrittenThisRun -and (-not $msiInstalledThisRun -or $msiRollbackSucceeded) -and (Test-Path -LiteralPath $transformPath)) {
        try {
            Assert-TightVncPrivateFile -Path $transformPath
            Remove-Item -LiteralPath $transformPath -Force
        } catch { Write-Warning 'The failed install left its non-secret TightVNC transform file in the protected Agent data directory.' }
    }
    $serviceFailureDisposition = Get-TightVncServiceFailureDisposition -Service (Get-Service -Name 'tvnserver' -ErrorAction SilentlyContinue)
    if ($decision -eq 'RepairFirewall') {
        if (-not $repairMutationStarted) {
            Write-Warning 'TightVNC repair failed before VNC service or firewall changes; existing VNC settings were left as they were. Fix the reported preflight error, then rerun the admin install command.'
        } else {
            switch ($serviceFailureDisposition) {
                'Stopped' { Write-Warning 'TightVNC repair failed. VNC remains stopped until repaired; rerun the admin install command to retry. The protected credential was preserved.' }
                'Missing' { Write-Warning 'TightVNC repair failed because the service is missing; VNC is unavailable. Inspect the installation, then rerun the admin install command. The protected credential was preserved.' }
                default { Write-Warning 'TightVNC repair failed and the service could not be confirmed stopped. Do not expose this machine; inspect the service and firewall, then rerun the admin install command.' }
            }
        }
    } elseif ($decision -eq 'Install' -and $msiInstalledThisRun -and -not $msiRollbackSucceeded) {
        switch ($serviceFailureDisposition) {
            'Stopped' { Write-Warning 'TightVNC install rollback failed, but its service is stopped. Inspect the remaining installation state, then rerun the admin install command.' }
            'Missing' { Write-Warning 'TightVNC install rollback failed and left no VNC service. Inspect the remaining installation state, then rerun the admin install command.' }
            default { Write-Warning 'TightVNC fresh install rollback failed and the VNC service is not confirmed stopped. Do not expose this machine; inspect the service and firewall.' }
        }
    } elseif ($decision -eq 'Install' -and $msiInstalledThisRun -and $serviceFailureDisposition -notin @('Stopped', 'Missing')) {
        Write-Warning 'TightVNC installation failed and the VNC service is not confirmed stopped. Do not expose this machine; inspect the service and firewall.'
    }
    throw $original
} finally {
    if ($installerPath -and (Test-Path -LiteralPath $installerPath)) {
        $resolved = [IO.Path]::GetFullPath($installerPath)
        $expectedParent = [IO.Path]::GetFullPath($credentialDirectory).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike '.tightvnc-*.msi') { throw 'Unsafe TightVNC MSI cleanup path.' }
        Remove-Item -LiteralPath $resolved -Force
    }
}

if ($decision -eq 'Install') {
    Write-Host ('VNC viewer password for this machine: {0}' -f $password)
    Write-Host ('The password is also saved with SYSTEM/Administrators-only access at: {0}' -f $credentialPath)
} else {
    Write-Output 'Managed TightVNC Server was reconfigured and its firewall source was verified; the existing VNC password was preserved.'
}
if (Test-Path -LiteralPath $markerPath) {
    $finalMarker = Get-TightVncManagedMarker -Path $markerPath -CredentialPath $credentialPath
    if ([string]$finalMarker.AllowedSourceAddress -ne $script:ValidatedVncSource) { throw 'KamenRaider VNC marker did not preserve the configured Controller source address.' }
}
