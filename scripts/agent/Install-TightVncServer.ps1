[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)][string]$AllowedSourceAddress,
    [string]$DataDirectory = (Join-Path $env:ProgramData 'KamenRaider\Agent'),
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
$script:TightVncVersion = '2.8.90'
$script:TightVncInstallerUrl = 'https://www.tightvnc.com/download/2.8.90/tightvnc-2.8.90-gpl-setup-64bit.msi'
$script:TightVncInstallerBytes = 2543616L
$script:TightVncRuleName = 'KamenRaiderAdminVnc'
$script:TightVncRuleDisplayName = 'KamenRaider Admin VNC'

function ConvertTo-AllowedVncSourceAddress {
    param([Parameter(Mandatory)][string]$Address)
    $parsed = $null
    if (-not [Net.IPAddress]::TryParse($Address, [ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'AllowedSourceAddress must be one private or loopback IPv4 address.'
    }
    $bytes = $parsed.GetAddressBytes()
    $private = $bytes[0] -eq 10 -or $bytes[0] -eq 127 -or
        ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
    if (-not $private -or $parsed.ToString() -cne $Address) {
        throw 'AllowedSourceAddress must be one private or loopback IPv4 address.'
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
        InstallerBundled = $false
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
    if ([long]$Metadata.Bytes -ne $script:TightVncInstallerBytes -or [string]$Metadata.SignatureStatus -cne 'Valid' -or
        [string]$Metadata.ProductName -notlike 'TightVNC*' -or [string]$Metadata.ProductVersion -notin @('2.8.90', '2.8.90.0') -or
        [string]$Metadata.Manufacturer -notmatch 'GlavSoft' -or [string]$Metadata.Template -notmatch '^x64;') {
        throw 'Downloaded MSI is not the signed TightVNC 2.8.90 x64 product published by GlavSoft.'
    }
}

function Get-TightVncInstallDecision {
    param([string[]]$InstalledVersions = @(),[bool]$ManagedInstall = $false)
    $versions = @($InstalledVersions | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    if ($versions.Count -eq 0) { return 'Install' }
    if ($versions.Count -ne 1 -or $versions[0] -notin @('2.8.90', '2.8.90.0') -or -not $ManagedInstall) {
        throw 'An unmanaged or incompatible TightVNC installation exists; refusing to replace it.'
    }
    return 'RepairFirewall'
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
                $view.Execute()
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
            [pscustomobject]@{
                Bytes = (Get-Item -LiteralPath $Path -Force).Length
                SignatureStatus = [string](Get-AuthenticodeSignature -LiteralPath $Path).Status
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

function Invoke-TightVncMsiInstall {
    param([Parameter(Mandatory)][string]$InstallerPath,[Parameter(Mandatory)][string]$Password)
    $exe = Join-Path $env:SystemRoot 'System32\msiexec.exe'
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
        'SET_PASSWORD=1', "VALUE_OF_PASSWORD=$Password",
        'SET_VIEWONLYPASSWORD=1', "VALUE_OF_VIEWONLYPASSWORD=$Password"
    )
    $arguments = '/i "' + $InstallerPath + '" /quiet /norestart ' + ($properties -join ' ')
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -notin @(0, 3010)) { throw "TightVNC MSI installation failed with exit code $($process.ExitCode)." }
    return $process.ExitCode
}

function Invoke-TightVncMsiUninstall {
    param([Parameter(Mandatory)][string]$ProductCode)
    if ($ProductCode -notmatch '^\{[A-Fa-f0-9-]{36}\}$') { throw 'TightVNC MSI product code is invalid; cleanup was refused.' }
    $exe = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    $process = Start-Process -FilePath $exe -ArgumentList ('/x ' + $ProductCode + ' /quiet /norestart') -PassThru -Wait -WindowStyle Hidden
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
Assert-TightVncDataDirectory -Path $script:VncPlan.DataDirectory
$credentialDirectory = Join-Path $script:VncPlan.DataDirectory 'RemoteDesktop'
New-TightVncPrivateDirectory -Path $credentialDirectory
$credentialPath = $script:VncPlan.SecretPath
$markerPath = $script:VncPlan.ManagedMarkerPath
$marker = Get-TightVncManagedMarker -Path $markerPath -CredentialPath $credentialPath
$versions = @(Get-TightVncInstalledVersions)
$decision = Get-TightVncInstallDecision -InstalledVersions $versions -ManagedInstall ([bool]$marker)
$serverPath = $script:VncPlan.Firewall.Program
$rule = @(Get-NetFirewallRule -Name $script:TightVncRuleName -ErrorAction SilentlyContinue)
if ($decision -eq 'Install' -and $rule.Count -gt 0 -and -not $marker) { throw 'A KamenRaider VNC firewall rule exists without its private installation marker.' }

$password = $null
$installerPath = $null
$metadata = $null
$msiInstalledThisRun = $false
$firewallChangedThisRun = $false
$stateWrittenThisRun = $false
$existingMarker = [bool]$marker
try {
    if ($decision -eq 'RepairFirewall') {
        $password = [IO.File]::ReadAllText($credentialPath)
        if ($password -notmatch '^[A-Za-z0-9_-]{8}$') { throw 'Managed VNC credential file is invalid.' }
    } else {
        if ($marker) { Remove-TightVncPrivateState -CredentialPath $credentialPath -MarkerPath $markerPath; $marker = $null }
        $password = New-TightVncPassword
    }

    $installerPath = Join-Path $credentialDirectory ('.tightvnc-' + [Guid]::NewGuid().ToString('N') + '.msi')
    Invoke-WebRequest -UseBasicParsing -Uri $script:TightVncInstallerUrl -OutFile $installerPath -ErrorAction Stop
    $signature = Get-AuthenticodeSignature -LiteralPath $installerPath
    $metadata = Get-TightVncMsiMetadata -Path $installerPath
    $metadata.SignatureStatus = [string]$signature.Status
    Assert-TightVncInstallerMetadata $metadata
    if ([string]::IsNullOrWhiteSpace([string]$metadata.ProductCode)) { throw 'TightVNC MSI ProductCode is missing.' }

    if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf) -and $decision -eq 'RepairFirewall') { throw 'Managed TightVNC Server binary is missing.' }
    if ($decision -eq 'RepairFirewall') {
        $service = Assert-TightVncService
        Invoke-TightVncMsiInstall -InstallerPath $installerPath -Password $password | Out-Null
        $firewallManaged = $true
    } else {
        Invoke-TightVncMsiInstall -InstallerPath $installerPath -Password $password | Out-Null
        $msiInstalledThisRun = $true
        if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) { throw 'TightVNC Server binary is missing after installation.' }
        Start-TightVncService
        Write-TightVncPrivateFile -Path $credentialPath -Contents $password
        $stateWrittenThisRun = $true
        $firewallManaged = $true
    }

    Start-TightVncService
    Set-TightVncFirewallRule -Source $script:ValidatedVncSource -Program $serverPath -Managed:$firewallManaged
    $firewallChangedThisRun = $true
    Write-TightVncManagedMarker -Path $markerPath -CredentialPath $credentialPath -Source $script:ValidatedVncSource -Replace:$existingMarker
    $stateWrittenThisRun = $true
} catch {
    $original = $_
    if ($firewallChangedThisRun) {
        Get-NetFirewallRule -Name $script:TightVncRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    }
    if ($msiInstalledThisRun -and $metadata) {
        try { Invoke-TightVncMsiUninstall -ProductCode ([string]$metadata.ProductCode) } catch { Write-Warning 'TightVNC rollback did not finish; Windows Firewall was not left with the KamenRaider VNC rule.' }
    }
    if ($stateWrittenThisRun -and -not $existingMarker) { Remove-TightVncPrivateState -CredentialPath $credentialPath -MarkerPath $markerPath }
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
