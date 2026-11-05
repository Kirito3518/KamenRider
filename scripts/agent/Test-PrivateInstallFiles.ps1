$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('kr-private-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$currentSid=[Security.Principal.WindowsIdentity]::GetCurrent().User
$failures=@()
try {
 $existing=Join-Path $root 'existing';New-Item -ItemType Directory -Path $existing | Out-Null
 $child=Join-Path $existing 'insecure.txt';[IO.File]::WriteAllText($child,'untrusted')
 $acl=Get-Acl -LiteralPath $child
 $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'),[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow));Set-Acl -LiteralPath $child -AclObject $acl
 try {Assert-EmptyInstallDirectory $existing;throw 'Nonempty destination was accepted'}catch{if($_.Exception.Message -ne 'Data directory must be empty.'){throw}}
 if([IO.File]::ReadAllText($child) -ne 'untrusted'){throw 'Reject must not mutate existing data'}
 Write-Output 'PASS: pre-existing insecure descendants are rejected without mutation'
} catch {$failures+=$_.Exception.Message}
try {
 $empty=Join-Path $root 'empty';$created=Initialize-PrivateDataDirectory $empty $currentSid
 if(-not $created){throw 'New private data directory creation was not reported.'}
 if(Initialize-PrivateDataDirectory $empty $currentSid){throw 'Existing private data directory was incorrectly reported as newly created.'}
 $acl=Get-Acl -LiteralPath $empty
 $allowed=@($currentSid.Value,'S-1-5-18','S-1-5-32-544')
 if(-not $acl.AreAccessRulesProtected){throw 'Data ACL must be protected'}
 foreach($rule in $acl.Access){if($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowed){throw 'Unexpected data access'}}
 $squatted=Join-Path $root 'squatted-empty';New-Item -ItemType Directory -Path $squatted | Out-Null
 $squattedAcl=Get-Acl -LiteralPath $squatted
 $squattedAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'),[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow))
 Set-Acl -LiteralPath $squatted -AclObject $squattedAcl
 $beforeSddl=(Get-Acl -LiteralPath $squatted).Sddl
 $squatRejected=$false
 try { Initialize-PrivateDataDirectory $squatted $currentSid } catch { $squatRejected=$true }
 if(-not $squatRejected){throw 'A pre-existing empty directory with untrusted access must be rejected, not repaired in place.'}
 if((Get-Acl -LiteralPath $squatted).Sddl -ne $beforeSddl){throw 'Rejecting a squatted data directory must not mutate its ACL.'}
 $readOnly=Join-Path $root 'read-only-publish';New-Item -ItemType Directory -Path $readOnly | Out-Null
 $serviceSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-80-1111111111-2222222222-3333333333-4444444444-5555555555')
 $servicePrivateAcl=[Security.AccessControl.FileSecurity]::new()
 $servicePrivateAcl.SetOwner($currentSid)
 $servicePrivateAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid,[Security.AccessControl.FileSystemRights]::Read,[Security.AccessControl.AccessControlType]::Allow))
 if(-not (Test-TrustedInstallAcl $servicePrivateAcl (Get-TrustedInstallSids @($serviceSid)) -Private)){throw 'The Agent service SID must be allowed to read its private configuration.'}
 if(Test-TrustedInstallAcl $servicePrivateAcl (Get-TrustedInstallSids) -Private){throw 'A private file must not allow an untrusted service SID to read it.'}
 $dataReadOnlyAcl=[Security.AccessControl.DirectorySecurity]::new();$dataReadOnlyAcl.SetOwner($currentSid)
 $dataReadOnlyAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid,[Security.AccessControl.FileSystemRights]::ReadAndExecute,[Security.AccessControl.AccessControlType]::Allow))
 if(Test-TrustedInstallAcl $dataReadOnlyAcl (Get-TrustedInstallSids @($serviceSid)) -RequireModifySid $serviceSid){throw 'A private data directory without Agent service modify rights must be rejected.'}
 foreach($generic in @([int]268435456,[int]1073741824)){
  if(-not (Test-DisallowedInstallAceRights $generic)){throw ('Untrusted generic ACL mask was not rejected: {0}' -f $generic)}
 }
 if(-not (Test-DisallowedInstallAceRights ([int]-2147483648) -Private)){throw 'Untrusted GENERIC_READ access to a private file was not rejected.'}
 Set-PrivateInstallAcl $readOnly $serviceSid -Directory -ReadOnly
 $readOnlyAcl=Get-Acl -LiteralPath $readOnly
 $serviceRules=@($readOnlyAcl.Access | Where-Object {$_.IdentityReference -eq $serviceSid})
 if($serviceRules.Count -ne 1 -or ($serviceRules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -ne [Security.AccessControl.FileSystemRights]::ReadAndExecute){throw 'Service account must read and execute published binaries.'}
 $writeRights=[Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions
 if($serviceRules[0].FileSystemRights -band $writeRights){throw 'Service account must not modify published binaries.'}
 $publish=Join-Path $root 'publish';New-Item -ItemType Directory -Path $publish | Out-Null
 $source=Join-Path $root 'secret.json';[IO.File]::WriteAllText($source,'{"credential":"test-only-secret"}')
 $destination=Join-Path $publish 'appsettings.json';[IO.File]::WriteAllText($destination,'old')
 $acl=Get-Acl -LiteralPath $destination;$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'),[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow));Set-Acl -LiteralPath $destination -AclObject $acl
 Publish-PrivateConfiguration $source $publish $currentSid
 if([IO.File]::ReadAllText($destination) -ne '{"credential":"test-only-secret"}'){throw 'Secret configuration was not published'}
 $acl=Get-Acl -LiteralPath $destination
 if(-not $acl.AreAccessRulesProtected){throw 'Published ACL must remain protected after atomic replacement'}
 foreach($rule in $acl.Access){if($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowed){throw 'Destination retained broad explicit or inherited ACL'}}
 if(@(Get-ChildItem -LiteralPath $publish -Force).Count -ne 1){throw 'Private staging files leaked'}
 Write-Output 'PASS: config atomically replaces insecure target with private source ACL'
 $lock=[IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
 try {try{Publish-PrivateConfiguration $source $publish $currentSid;throw 'Locked source accepted'}catch{if($_.Exception.Message -eq 'Locked source accepted'){throw}}}finally{$lock.Dispose()}
 if([IO.File]::ReadAllText($destination) -ne '{"credential":"test-only-secret"}'){throw 'Failed publication changed destination'}
 if(@(Get-ChildItem -LiteralPath $publish -Force).Count -ne 1){throw 'Failed publication leaked staging files'}
 Write-Output 'PASS: failed config publication preserves destination and cleans staging'
} catch {$failures+=$_.Exception.Message}
finally {
 $resolved=[IO.Path]::GetFullPath($root)
 $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-private-*'){throw 'Unsafe test cleanup path.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
if($failures.Count){throw ($failures -join '; ')}
Write-Output 'Private installer-file tests passed using temporary files only; no services changed.'
