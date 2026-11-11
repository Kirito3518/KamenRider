function Publish-AgentPackageFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StagedArchivePath,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$HashFilePath,
        [Parameter(Mandatory)][ValidatePattern('\A[0-9A-Fa-f]{64}\z')][string]$HashValue,
        [scriptblock]$FailureInjector
    )

    $outputDirectory = Split-Path -Parent $ArchivePath
    if (-not [string]::Equals($outputDirectory, (Split-Path -Parent $HashFilePath), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Agent package ZIP and hash must share an output directory.'
    }

    $archiveTemp = Join-Path $outputDirectory ('.AgentPackage-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $hashTemp = Join-Path $outputDirectory ('.AgentPackage-' + [guid]::NewGuid().ToString('N') + '.hash.tmp')
    $archiveBackup = $null
    $hashBackup = $null
    $archiveExisted = Test-Path -LiteralPath $ArchivePath
    $hashExisted = Test-Path -LiteralPath $HashFilePath
    $archivePublished = $false
    $hashPublished = $false

    function Invoke-PublicationFailureHook([string]$Point, [string]$Path) {
        if ($FailureInjector) { & $FailureInjector $Point $Path }
    }

    function Remove-PublicationBackupBestEffort([string]$Path, [string]$Label, [string]$Point) {
        if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
        try {
            Invoke-PublicationFailureHook $Point $Path
            [IO.File]::Delete($Path)
        } catch {
            Write-Warning "Agent package backup cleanup failed for $Label. The original copy remains at '$Path'. $($_.Exception.Message)"
        }
    }

    try {
        [IO.File]::Copy($StagedArchivePath, $archiveTemp, $false)
        [IO.File]::WriteAllText($hashTemp, $HashValue.ToUpperInvariant() + [Environment]::NewLine, [Text.Encoding]::ASCII)

        try {
            Invoke-PublicationFailureHook 'BeforeArchiveSwap' $ArchivePath
            if ($archiveExisted) {
                $archiveBackup = Join-Path $outputDirectory ('.AgentPackage-' + [guid]::NewGuid().ToString('N') + '.bak')
                [IO.File]::Replace($archiveTemp, $ArchivePath, $archiveBackup)
                $archiveTemp = $null
            } else {
                [IO.File]::Move($archiveTemp, $ArchivePath)
                $archiveTemp = $null
            }
            $archivePublished = $true

            Invoke-PublicationFailureHook 'BeforeHashSwap' $HashFilePath
            if ($hashExisted) {
                $hashBackup = Join-Path $outputDirectory ('.AgentPackage-' + [guid]::NewGuid().ToString('N') + '.hash.bak')
                [IO.File]::Replace($hashTemp, $HashFilePath, $hashBackup)
                $hashTemp = $null
            } else {
                [IO.File]::Move($hashTemp, $HashFilePath)
                $hashTemp = $null
            }
            $hashPublished = $true

            Invoke-PublicationFailureHook 'BeforeCommit' $ArchivePath
        } catch {
            $publicationFailure = $_.Exception
            $rollbackFailures = [Collections.Generic.List[string]]::new()

            if ($hashPublished) {
                try {
                    Invoke-PublicationFailureHook 'BeforeRestoreHashBackup' $hashBackup
                    if ($hashExisted -and $hashBackup -and (Test-Path -LiteralPath $hashBackup -PathType Leaf)) {
                        [IO.File]::Copy($hashBackup, $HashFilePath, $true)
                    } elseif (-not $hashExisted -and (Test-Path -LiteralPath $HashFilePath)) {
                        [IO.File]::Delete($HashFilePath)
                    }
                    $hashPublished = $false
                } catch { $rollbackFailures.Add("Could not restore the prior hash file: $($_.Exception.Message)") }
            }

            if ($archivePublished) {
                try {
                    Invoke-PublicationFailureHook 'BeforeRestoreArchiveBackup' $archiveBackup
                    if ($archiveExisted -and $archiveBackup -and (Test-Path -LiteralPath $archiveBackup -PathType Leaf)) {
                        [IO.File]::Copy($archiveBackup, $ArchivePath, $true)
                    } elseif (-not $archiveExisted -and (Test-Path -LiteralPath $ArchivePath)) {
                        [IO.File]::Delete($ArchivePath)
                    }
                    $archivePublished = $false
                } catch { $rollbackFailures.Add("Could not restore the prior ZIP file: $($_.Exception.Message)") }
            }

            if ($rollbackFailures.Count -gt 0) {
                $recoveryPaths = @($archiveBackup, $hashBackup) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
                $message = @(
                    'Agent package publication failed and rollback was incomplete.'
                    'Recovery backup paths:'
                    @($recoveryPaths | ForEach-Object { 'Recovery backup path: ' + $_ })
                    'Rollback errors: ' + ($rollbackFailures -join ' ')
                ) -join [Environment]::NewLine
                throw [IO.IOException]::new($message, $publicationFailure)
            }

            Remove-PublicationBackupBestEffort $archiveBackup 'ZIP' 'BeforeDeleteArchiveBackup'
            Remove-PublicationBackupBestEffort $hashBackup 'hash' 'BeforeDeleteHashBackup'
            throw $publicationFailure
        }

        # The two output files are replaced sequentially; this handles process errors but is not cross-file crash atomic.
        # Once both swaps complete, cleanup failures must not roll back the committed pair.
        Remove-PublicationBackupBestEffort $archiveBackup 'ZIP' 'BeforeDeleteArchiveBackup'
        Remove-PublicationBackupBestEffort $hashBackup 'hash' 'BeforeDeleteHashBackup'
    } finally {
        if ($archiveTemp -and (Test-Path -LiteralPath $archiveTemp -PathType Leaf)) { [IO.File]::Delete($archiveTemp) }
        if ($hashTemp -and (Test-Path -LiteralPath $hashTemp -PathType Leaf)) { [IO.File]::Delete($hashTemp) }
    }
}

Export-ModuleMember -Function Publish-AgentPackageFiles
