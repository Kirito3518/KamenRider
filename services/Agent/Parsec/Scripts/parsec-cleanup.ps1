# KamenRaider Agent - Parsec rental-end cleanup.
# Ported from the owner's LordX3d agent-parsec cleanupRentalSession through
# 0jay-v2 agent/parsec_script.go: close parsecd in the desktop session, forget
# the account (app_session_id, user.bin) in both Parsec data folders and empty
# the clipboard. The script deletes itself first; it never touches credentials.
$ErrorActionPreference = 'Stop'
try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch { }
$workDir = __WORK_DIR__
$statusPath = Join-Path $workDir 'status.json'
$log = Join-Path $workDir 'steps.log'
function Write-ParsecLog([string]$Message) {
  try { ('[' + (Get-Date -Format 'HH:mm:ss') + '] ' + $Message) | Out-File -LiteralPath $log -Append -Encoding utf8 } catch { }
}
function Write-ParsecStatus([hashtable]$Status) {
  $temporary = $statusPath + '.tmp'
  ConvertTo-Json -InputObject $Status -Depth 4 -Compress | Set-Content -LiteralPath $temporary -Encoding UTF8
  Move-Item -LiteralPath $temporary -Destination $statusPath -Force
}

try {
$currentSessionId = (Get-Process -Id $PID).SessionId
Get-Process -Name parsecd -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq $currentSessionId } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
$parsecTextConfigs = @(
  (Join-Path $env:APPDATA 'Parsec\config.txt'),
  (Join-Path $env:ProgramData 'Parsec\config.txt')
)
foreach ($parsecConfig in $parsecTextConfigs) {
  if (Test-Path $parsecConfig) {
    try {
      $lines = @(Get-Content $parsecConfig | Where-Object { $_ -notmatch '^app_session_id=' })
      [System.IO.File]::WriteAllLines($parsecConfig, [string[]]$lines)
    } catch { Write-ParsecLog 'WARNING: Could not update a Parsec config.txt' }
  }
}
$parsecJsonConfigs = @(
  (Join-Path $env:APPDATA 'Parsec\config.json'),
  (Join-Path $env:ProgramData 'Parsec\config.json')
)
foreach ($jsonPath in $parsecJsonConfigs) {
  if (Test-Path $jsonPath) {
    try {
      $json = Get-Content -Raw $jsonPath | ConvertFrom-Json
      if ($json.PSObject.Properties['app_session_id']) {
        $json.PSObject.Properties.Remove('app_session_id')
        $json | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $jsonPath
      }
    } catch { Write-ParsecLog 'WARNING: Could not update a Parsec config.json' }
  }
}
Remove-Item (Join-Path $env:APPDATA 'Parsec\user.bin') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'Parsec\user.bin') -Force -ErrorAction SilentlyContinue
try {
  Add-Type -AssemblyName System.Windows.Forms
  [System.Windows.Forms.Clipboard]::Clear()
} catch { }
Write-ParsecLog 'Rental session cleared'
Write-ParsecStatus @{ automationCompleted=$true; error=$null }
} catch {
  Write-ParsecLog ('FATAL: ' + $_.Exception.Message)
  Write-ParsecStatus @{ automationCompleted=$false; error=$_.Exception.Message }
}
