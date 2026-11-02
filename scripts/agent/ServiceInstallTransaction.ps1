function Invoke-ServiceInstallTransaction {
 param([scriptblock]$Create,[scriptblock]$Configure,[scriptblock]$Rollback)
 & $Create
 try { & $Configure } catch {
  $original=$_
  try { & $Rollback } catch { throw "Service configuration failed and rollback also failed: $($_.Exception.Message). Do not retry until an administrator verifies the service, data directory and configuration file." }
  Write-Warning 'Service registration was removed, but data directory permissions and configuration files may have changed. An administrator must inspect and recover them before retrying.'
  throw $original
 }
}
