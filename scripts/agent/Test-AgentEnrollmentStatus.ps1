$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')

$pending = Get-AgentEnrollmentStatus
if ($pending.State -ne 'pending' -or $pending.MachineId -or $pending.Fingerprint) { throw 'Missing enrollment receipt must remain pending without inventing machine identity.' }
if ($pending.Message -notmatch 'service remains installed' -or $pending.Message -notmatch 'retry') { throw 'Pending status must clearly report that enrollment will retry.' }

$fingerprint = 'A' * 64
$enrolled = Get-AgentEnrollmentStatus -Receipt ([pscustomobject]@{
    MachineId = 'machine-42'
    ApprovalStatus = 'pending'
    PublicKeyFingerprint = $fingerprint
})
if ($enrolled.State -ne 'enrolled' -or $enrolled.MachineId -ne 'machine-42' -or $enrolled.ApprovalStatus -ne 'pending' -or $enrolled.Fingerprint -ne $fingerprint) {
    throw 'Enrollment status must preserve machine ID, approval state and public-key fingerprint.'
}
if ($enrolled.Message -notmatch 'MachineId=machine-42' -or $enrolled.Message -notmatch $fingerprint) { throw 'Success output must include the machine ID and fingerprint.' }

$invalid = $false
try { Get-AgentEnrollmentStatus -Receipt ([pscustomobject]@{ MachineId = ''; ApprovalStatus = 'unknown'; PublicKeyFingerprint = 'bad' }) | Out-Null }
catch { $invalid = $true }
if (-not $invalid) { throw 'Malformed enrollment receipts must not be reported as success.' }

Write-Output 'Agent enrollment status tests passed for enrolled and retrying states.'
