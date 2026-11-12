[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$script:MaximumAgentMaintenanceHelperBytes = 157286400

if (-not ('KamenRaider.Publish.NativeFileInfoV1' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KamenRaider.Publish {
    public static class NativeFileInfoV1 {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation {
            public uint FileAttributes;
            public FileTime CreationTime;
            public FileTime LastAccessTime;
            public FileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle handle, out ByHandleFileInformation information);

        public static uint LinkCount(string path) {
            using (SafeFileHandle handle = CreateFile(path, 0x80, 0x7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return information.NumberOfLinks;
            }
        }
    }
}
'@
}

function Assert-AgentMaintenanceNoReparseAncestors {
    param([Parameter(Mandatory)][string]$Path)
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Agent maintenance publish path contains a reparse point: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent -or $parent.FullName -ceq $current) { break }
        $current = $parent.FullName
    }
}

function Assert-AgentMaintenanceRegularSingleLinkFile {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Description)
    Assert-AgentMaintenanceNoReparseAncestors -Path $Path
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Description must be a regular non-reparse file."
    }
    if ([KamenRaider.Publish.NativeFileInfoV1]::LinkCount($Path) -ne 1) {
        throw "$Description must have exactly one hard link."
    }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ConvertTo-UpperHex ($algorithm.ComputeHash($stream)) }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function ConvertTo-UpperHex {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    return [BitConverter]::ToString($Bytes).Replace('-', '')
}

function Get-Sha256HexFromBytes {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ConvertTo-UpperHex ($algorithm.ComputeHash($Bytes)) }
    finally { $algorithm.Dispose() }
}

function Test-PathFullyQualified {
    param([Parameter(Mandatory)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path -match '\A[A-Za-z]:[\\/]') { return $true }
    return $Path.StartsWith('\\',[StringComparison]::Ordinal)
}

function Get-PortableExecutableMachine {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 256) { throw 'Maintenance helper is too short to be a PE image.' }
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Maintenance helper has no DOS header.' }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 64 -or $peOffset -gt $stream.Length - 6) { throw 'Maintenance helper has an invalid PE header offset.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Maintenance helper has no PE signature.' }
        return $reader.ReadUInt16()
    }
    finally { $stream.Dispose() }
}

function Write-AtomicBytes {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][byte[]]$Bytes)
    $temporary = Join-Path ([IO.Path]::GetDirectoryName($Path)) ('.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $file = [IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,65536,[IO.FileOptions]::WriteThrough)
    try { $file.Write($Bytes,0,$Bytes.Length); $file.Flush($true) }
    finally { $file.Dispose() }
    try {
        if (Test-Path -LiteralPath $Path) {
            $existing = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
            if ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Existing deployment manifest must be a regular non-reparse file.'
            }
            [IO.File]::Replace($temporary,$Path,$null)
        }
        else {
            [IO.File]::Move($temporary,$Path)
        }
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$output = [IO.Path]::GetFullPath($OutputRoot)
$project = Join-Path $repo 'services\AgentMaintenance\AgentMaintenance.csproj'
$wrapper = Join-Path $repo 'public\agent\Update-Agent.ps1'
$publicAgentRoot = Join-Path $output 'public\agent'
$maintenanceRoot = Join-Path $publicAgentRoot 'maintenance'
$stageRoot = Join-Path $output ('.agent-maintenance-publish-' + [guid]::NewGuid().ToString('N'))

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw 'AgentMaintenance project is missing.' }
if (-not (Test-Path -LiteralPath $wrapper -PathType Leaf)) { throw 'Deployment wrapper is missing.' }
if (-not (Test-PathFullyQualified $output)) { throw 'OutputRoot must be an absolute path.' }
Assert-AgentMaintenanceNoReparseAncestors -Path $publicAgentRoot
Assert-AgentMaintenanceNoReparseAncestors -Path $maintenanceRoot
New-Item -ItemType Directory -Path $publicAgentRoot -Force | Out-Null
New-Item -ItemType Directory -Path $maintenanceRoot -Force | Out-Null
Assert-AgentMaintenanceNoReparseAncestors -Path $publicAgentRoot
Assert-AgentMaintenanceNoReparseAncestors -Path $maintenanceRoot

Assert-AgentMaintenanceRegularSingleLinkFile -Path $wrapper -Description 'Deployment wrapper source'
$wrapperSourceInfo = Get-Item -LiteralPath $wrapper -Force
if ($wrapperSourceInfo.Length -lt 1 -or $wrapperSourceInfo.Length -gt 2097152) {
    throw 'Deployment wrapper source must be between 1 byte and 2097152 bytes.'
}
$servedWrapper = Join-Path $publicAgentRoot 'Update-Agent.ps1'
if (-not (Test-Path -LiteralPath $servedWrapper -PathType Leaf)) {
    throw 'The web-served deployment wrapper is missing from OutputRoot.'
}
$servedWrapperInfo = Get-Item -LiteralPath $servedWrapper -Force
Assert-AgentMaintenanceRegularSingleLinkFile -Path $servedWrapper -Description 'Web-served deployment wrapper'
if ($servedWrapperInfo.Length -lt 1 -or $servedWrapperInfo.Length -gt 2097152) {
    throw 'The web-served deployment wrapper must be a bounded regular non-reparse file.'
}
$wrapperBytes = [IO.File]::ReadAllBytes($wrapper)
$servedWrapperBytes = [IO.File]::ReadAllBytes($servedWrapper)
if ($wrapperBytes.Length -ne $servedWrapperBytes.Length -or
    (Get-Sha256HexFromBytes $wrapperBytes) -cne (Get-Sha256HexFromBytes $servedWrapperBytes)) {
    throw 'The web-served deployment wrapper differs from the repository wrapper source.'
}

New-Item -ItemType Directory -Path $stageRoot | Out-Null

try {
    $helpers = [ordered]@{}
    foreach ($runtime in @('win-x64','win-arm64')) {
        $rid = $runtime
        $publishDirectory = Join-Path $stageRoot $rid
        & $DotNetPath publish $project --configuration $Configuration --runtime $rid --self-contained true `
            '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' `
            '-p:EnableCompressionInSingleFile=true' '-p:DebugType=None' '--output' $publishDirectory
        if ($LASTEXITCODE -ne 0) { throw "AgentMaintenance publish failed for $rid with exit code $LASTEXITCODE." }
        $helper = Join-Path $publishDirectory 'AgentMaintenance.exe'
        if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) { throw "Single-file AgentMaintenance.exe was not emitted for $rid." }
        Assert-AgentMaintenanceRegularSingleLinkFile -Path $helper -Description "Published AgentMaintenance.exe for $rid"
        $machine = Get-PortableExecutableMachine $helper
        $expectedMachine = if ($rid -eq 'win-x64') { 0x8664 } else { 0xAA64 }
        if ($machine -ne $expectedMachine) { throw "AgentMaintenance.exe machine type does not match $rid." }
        $length = (Get-Item -LiteralPath $helper -Force).Length
        if ($length -lt 1 -or $length -gt $script:MaximumAgentMaintenanceHelperBytes) {
            throw "AgentMaintenance.exe exceeds the declared $($script:MaximumAgentMaintenanceHelperBytes)-byte helper limit for $rid."
        }
        $hash = Get-Sha256Hex $helper
        $relativeUrl = "/agent/maintenance/sha256/$($hash.ToLowerInvariant())/AgentMaintenance.exe"
        $assetDirectory = Join-Path $maintenanceRoot ('sha256\' + $hash.ToLowerInvariant())
        Assert-AgentMaintenanceNoReparseAncestors -Path $assetDirectory
        New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
        Assert-AgentMaintenanceNoReparseAncestors -Path $assetDirectory
        $destination = Join-Path $assetDirectory 'AgentMaintenance.exe'
        if (Test-Path -LiteralPath $destination) {
            Assert-AgentMaintenanceRegularSingleLinkFile -Path $destination -Description "Existing content-addressed asset $relativeUrl"
            $existingDestination = Get-Item -LiteralPath $destination -Force
            if ($existingDestination.Length -ne $length -or (Get-Sha256Hex $destination) -ne $hash) {
                throw "Existing content-addressed asset is invalid: $relativeUrl"
            }
        }
        else {
            $temporary = Join-Path $assetDirectory ('.AgentMaintenance.' + [guid]::NewGuid().ToString('N') + '.tmp')
            [IO.File]::Copy($helper,$temporary)
            Assert-AgentMaintenanceRegularSingleLinkFile -Path $temporary -Description "Staged helper for $rid"
            if ((Get-Item -LiteralPath $temporary -Force).Length -ne $length -or (Get-Sha256Hex $temporary) -ne $hash) {
                Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
                throw "Staged helper changed while publishing: $rid"
            }
            try { [IO.File]::Move($temporary,$destination) }
            catch { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }; throw }
            Assert-AgentMaintenanceRegularSingleLinkFile -Path $destination -Description "Published content-addressed asset $relativeUrl"
        }
        $helpers[$rid] = [ordered]@{ relativeUrl = $relativeUrl; length = $length; sha256 = $hash }
    }

    $wrapperAsset = [ordered]@{
        relativeUrl = '/agent/Update-Agent.ps1'
        length = $wrapperBytes.Length
        sha256 = Get-Sha256HexFromBytes $wrapperBytes
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        helperProtocol = 1
        helpers = $helpers
        wrapper = $wrapperAsset
    }
    $manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 8))
    $manifestPath = Join-Path $publicAgentRoot 'agent-maintenance-assets.json'
    Write-AtomicBytes $manifestPath $manifestBytes
    Write-Output $manifestPath
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($stageRoot)
    $resolvedOutput = [IO.Path]::GetFullPath($output).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($resolvedOutput,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedStage) -notlike '.agent-maintenance-publish-*') { throw 'Unsafe helper publish staging cleanup path.' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
