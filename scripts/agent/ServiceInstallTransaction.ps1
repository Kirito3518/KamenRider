function Invoke-ServiceInstallTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock]$Create,
        [Parameter(Mandatory)][scriptblock]$Configure,
        [Parameter(Mandatory)][scriptblock]$Rollback
    )

    $serviceCreatedByThisRun = $false
    try {
        $serviceCreatedByThisRun = [bool](& $Create)
        if (-not $serviceCreatedByThisRun) { throw 'Service creation did not confirm ownership.' }
        & $Configure
    } catch {
        $original = $_
        if ($serviceCreatedByThisRun) {
            try { & $Rollback }
            catch {
                throw "Service installation failed and rollback also failed: $($_.Exception.Message). Do not retry until an administrator verifies the service, data directory and configuration file."
            }
            Write-Warning 'Installation rollback removed only the service confirmed as created by this invocation. Private data and configuration files may remain and require administrator review.'
        } else {
            Write-Warning 'Service creation ownership was not confirmed; no service was deleted. An administrator must verify the service state before retrying.'
        }
        throw $original
    }
}
