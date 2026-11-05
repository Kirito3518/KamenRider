function Invoke-ServiceInstallTransaction {
 param([scriptblock]$Create,[scriptblock]$Configure,[scriptblock]$Rollback)
 try { & $Create; & $Configure } catch {
  $original=$_
  try { & $Rollback } catch { throw "Service installation failed and rollback also failed: $($_.Exception.Message). Do not retry until an administrator verifies the service, data directory and configuration file." }
  Write-Warning 'Installation rollback was attempted, but data directory permissions and configuration files may have changed. An administrator must inspect them before retrying.'
  throw $original
 }
}
