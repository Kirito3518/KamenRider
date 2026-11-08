$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\public\agent\Update-Agent.ps1') -ControllerUrl 'https://controller.example/' -MachineId ('0' * 32) -OperationId ('1' * 32) -UpdateTicket ('A' * 43) -ReleaseVersion '2.3.4' -TargetVersion '2.3.4' -PackageSha256 ('A' * 64) -TrustedRootThumbprint ('B' * 40) -AdminReturnUrl 'https://admin.example/admin/agent-releases' -DefineFunctionsOnly
Add-Type -AssemblyName System.IO.Compression
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('kr-update-fresh-zip-' + [guid]::NewGuid().ToString('N'))))
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $archive = Join-Path $root 'test.zip'
    $file = [IO.File]::Open($archive,[IO.FileMode]::CreateNew)
    try {
        $zip = [IO.Compression.ZipArchive]::new($file,[IO.Compression.ZipArchiveMode]::Create,$true)
        try {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry('Agent/test.txt').Open())
            try { $writer.Write('fresh process') } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }
    } finally { $file.Dispose() }
    if ($PSVersionTable.PSEdition -eq 'Desktop' -and ('System.IO.Compression.ZipFile' -as [type])) { throw 'Fresh-process test unexpectedly preloaded ZipFile.' }
    $expanded = Expand-AgentUpdatePackageArchive -ArchivePath $archive -Destination (Join-Path $root 'expanded')
    if ([IO.File]::ReadAllText((Join-Path $expanded 'Agent\test.txt')) -ne 'fresh process') { throw 'Fresh-process extraction did not preserve the archive content.' }
    Write-Output 'Fresh-process Agent package extraction passed.'
} finally {
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $root.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($root) -notlike 'kr-update-fresh-zip-*') { throw 'Unsafe fresh-process archive test cleanup path.' }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
