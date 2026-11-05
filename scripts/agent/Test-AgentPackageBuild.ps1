$ErrorActionPreference='Stop'
$build=Join-Path $PSScriptRoot 'Build-AgentPackage.ps1'
$outputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('kr-agent-package-build-'+[guid]::NewGuid().ToString('N'))
try {
 $output=@(& $build -OutputDirectory $outputDirectory)
 $result=@($output | Where-Object {$_ -and $_.PSObject.Properties['PackagePath']}) | Select-Object -Last 1
 if(-not $result){throw 'Builder did not return its package result.'}
 $archive=$result.PackagePath
 if(-not (Test-Path -LiteralPath $archive -PathType Leaf)){throw 'Builder did not create its archive.'}
 $actual=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToUpperInvariant()
 $recorded=[IO.File]::ReadAllText($result.HashFilePath).Trim()
 if($actual -ne $recorded -or $actual -ne $result.Sha256){throw 'Generated SHA-256 does not match the archive.'}
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $zip=[IO.Compression.ZipFile]::OpenRead($archive)
 try {
  $entries=@($zip.Entries | ForEach-Object FullName)
  foreach($entry in $entries){if($entry.Contains('\') -or $entry.StartsWith('/') -or $entry.Split('/') -contains '..'){throw "Unsafe or non-portable ZIP path: $entry"}}
  foreach($required in @('Agent/Agent.exe','Install-AgentPackage.ps1','Install-Service.ps1','PrivateInstallFiles.ps1','ServiceInstallTransaction.ps1')){if($required -notin $entries){throw "Package is missing $required."}}
 } finally {$zip.Dispose()}
 Write-Output "Agent package build test passed for $($result.Runtime); SHA256=$actual."
} finally {
 $resolved=[IO.Path]::GetFullPath($outputDirectory)
 $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-agent-package-build-*'){throw 'Unsafe package test cleanup path.'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
