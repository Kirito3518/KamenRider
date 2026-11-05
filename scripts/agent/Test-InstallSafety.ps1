$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'PrivateInstallFiles.ps1')
. (Join-Path $PSScriptRoot 'ServiceInstallTransaction.ps1')
Assert-TrustedInstallParent $env:ProgramFiles
Assert-TrustedInstallParent $env:ProgramData
$temporaryParentRejected=$false
try { Assert-TrustedInstallParent $env:TEMP } catch { $temporaryParentRejected=$true }
if(-not $temporaryParentRejected){throw 'Shared TEMP directories must remain rejected as privileged installation ancestors.'}
$usersSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
$programDataAcl=[Security.AccessControl.DirectorySecurity]::new()
$programDataAcl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
$programDataAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($usersSid,[Security.AccessControl.FileSystemRights]::Write,[Security.AccessControl.InheritanceFlags]::ContainerInherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
if(-not (Test-TrustedProgramDataRootAcl $programDataAcl)){throw 'ProgramData exception should accept the exact standard Users child-creation ACE.'}
$programDataAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($usersSid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow))
if(Test-TrustedProgramDataRootAcl $programDataAcl){throw 'ProgramData exception must reject any additional writable Users ACE.'}
$driveRootAcl=Get-Acl -LiteralPath ([IO.Path]::GetPathRoot($env:SystemRoot))
if(-not (Test-TrustedSystemRootAcl $driveRootAcl)){throw 'System root exception should accept the standard protected Windows root ACL.'}
$authUsersSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-11')
$unsafeDriveRootAcl=[Security.AccessControl.DirectorySecurity]::new()
$unsafeDriveRootAcl.SetSecurityDescriptorSddlForm($driveRootAcl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Owner -bor [Security.AccessControl.AccessControlSections]::Group -bor [Security.AccessControl.AccessControlSections]::Access))
$unsafeDriveRootAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($authUsersSid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow))
if(Test-TrustedSystemRootAcl $unsafeDriveRootAcl){throw 'System root exception must reject any additional writable Authenticated Users ACE.'}
function Get-Service {
 param([string]$Name,[string]$ErrorAction)
 if($Name -eq 'KamenRaider Agent'){return [pscustomobject]@{Name=$Name}}
 return $null
}
$existingServiceRejected=$false
try { Assert-ServiceIsAbsent -ServiceName 'KamenRaider Agent' } catch { $existingServiceRejected=$_.Exception.Message -eq 'Service already exists; use a reviewed upgrade procedure.' }
if(-not $existingServiceRejected){throw 'Existing service guard must reject without deleting the service.'}
Assert-ServiceIsAbsent -ServiceName 'KamenRaider Controller'
Remove-Item Function:\Get-Service -Force
$safe=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$untrusted=[Security.Principal.SecurityIdentifier]::new('S-1-1-0')
$acl=[Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($safe)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($untrusted,[Security.AccessControl.FileSystemRights]::WriteData,[Security.AccessControl.AccessControlType]::Allow))
if(Test-TrustedInstallAcl $acl @($safe)) {throw 'Untrusted write access was accepted.'}
$acl2=[Security.AccessControl.DirectorySecurity]::new()
$acl2.SetOwner($safe)
$acl2.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($untrusted,[Security.AccessControl.FileSystemRights]::ReadAndExecute,[Security.AccessControl.AccessControlType]::Allow))
if(-not (Test-TrustedInstallAcl $acl2 @($safe))) {throw 'Read-only access was rejected.'}
$inheritedOnly=[Security.AccessControl.DirectorySecurity]::new()
$inheritedOnly.SetOwner($safe)
$inheritedOnly.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($untrusted,[Security.AccessControl.FileSystemRights]::WriteData,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::InheritOnly,[Security.AccessControl.AccessControlType]::Allow))
if(-not (Test-TrustedInstallAcl $inheritedOnly @($safe))) {throw 'Inherit-only ACE does not apply to this directory.'}
if(Test-TrustedInstallAcl $inheritedOnly @($safe) -IncludeInheritedChildRules) {throw 'Inherit-only writable ACE could apply to new child.'}
$fileAcl=[Security.AccessControl.FileSecurity]::new()
$fileAcl.SetOwner($safe)
$fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($untrusted,[Security.AccessControl.FileSystemRights]::WriteData,[Security.AccessControl.AccessControlType]::Allow))
if(Test-TrustedInstallAcl $fileAcl @($safe)) {throw 'Untrusted file modification was accepted.'}
$readableFile=[Security.AccessControl.FileSecurity]::new()
$readableFile.SetOwner($safe)
$readableFile.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($untrusted,[Security.AccessControl.FileSystemRights]::ReadAndExecute,[Security.AccessControl.AccessControlType]::Allow))
if(Test-TrustedInstallAcl $readableFile @($safe) -Private) {throw 'Secret file must not be readable by untrusted users.'}
$root=Join-Path ([IO.Path]::GetTempPath()) ('kr-link-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
 $file=Join-Path $root 'source';$other=Join-Path $root 'other';[IO.File]::WriteAllText($file,'test')
 if((Get-InstallFileLinkCount $file) -ne 1){throw 'New file should have one link.'}
 New-Item -ItemType HardLink -Path $other -Target $file | Out-Null
 if((Get-InstallFileLinkCount $file) -ne 2){throw 'Hard links were not detected.'}
} finally {
 $resolved=[IO.Path]::GetFullPath($root);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-link-test-*'){throw 'Unsafe cleanup path.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
$events=[Collections.Generic.List[string]]::new()
try { Invoke-ServiceInstallTransaction -WarningAction SilentlyContinue -Create { $events.Add('create'); return $true } -Configure { $events.Add('configure'); throw 'failure' } -Rollback { $events.Add('rollback') }; throw 'Failure was swallowed' } catch { if($_.Exception.Message -ne 'failure') {throw} }
if(($events -join ',') -ne 'create,configure,rollback'){throw 'Created service was not rolled back on failure.'}
$events.Clear()
try { Invoke-ServiceInstallTransaction -WarningAction SilentlyContinue -Create { $events.Add('create'); return $true } -Configure { $events.Add('start'); throw 'service start failure' } -Rollback { $events.Add('stop'); $events.Add('delete') }; throw 'Service start failure was swallowed' } catch { if($_.Exception.Message -ne 'service start failure') {throw} }
if(($events -join ',') -ne 'create,start,stop,delete'){throw 'Service start failure must stop and delete only the newly created service.'}
$events.Clear()
try { Invoke-ServiceInstallTransaction -WarningAction SilentlyContinue -Create { $events.Add('create'); throw 'create failure' } -Configure { $events.Add('configure') } -Rollback { $events.Add('rollback') }; throw 'Create failure was swallowed' } catch { if($_.Exception.Message -ne 'create failure') {throw} }
if(($events -join ',') -ne 'create'){throw 'Unconfirmed service creation must not delete a service that may belong to another invocation.'}
$events.Clear()
try { Invoke-ServiceInstallTransaction -WarningAction SilentlyContinue -Create { $events.Add('create'); return $false } -Configure { $events.Add('configure') } -Rollback { $events.Add('rollback') }; throw 'Unconfirmed ownership was accepted' } catch { if($_.Exception.Message -ne 'Service creation did not confirm ownership.') {throw} }
if(($events -join ',') -ne 'create'){throw 'Unconfirmed service ownership must not trigger rollback.'}
# Dependency files need the same trust checks as the executable, including
# hidden and nested files. This fixture only reads and writes disposable data.
$treeRoot=Join-Path ([IO.Path]::GetTempPath()) ('kr-tree-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $treeRoot 'nested') -Force | Out-Null
try {
 $exe=Join-Path $treeRoot 'Agent.exe'; $dll=Join-Path $treeRoot 'nested/dependency.dll'; $hidden=Join-Path $treeRoot 'nested/hidden.dll'
 [IO.File]::WriteAllText($exe,'executable'); [IO.File]::WriteAllText($dll,'dependency'); [IO.File]::WriteAllText($hidden,'hidden')
 (Get-Item -LiteralPath $hidden -Force).Attributes = [IO.FileAttributes]::Hidden
 $visited=[Collections.Generic.List[string]]::new()
 function Assert-TrustedInstallFile { param($Path) $visited.Add([IO.Path]::GetFileName($Path)) }
 function Assert-TrustedInstallParent { param($Path) }
 Assert-TrustedPublishTree $treeRoot
 if($visited.Count -ne 3 -or 'hidden.dll' -notin $visited -or 'dependency.dll' -notin $visited -or 'Agent.exe' -notin $visited){throw 'Publish tree must inspect every file, including hidden and nested files.'}
 $hardlink=Join-Path $treeRoot 'nested/hardlink.dll'
 New-Item -ItemType HardLink -Path $hardlink -Target $dll | Out-Null
 $rejected=$false
 try { Assert-TrustedPublishTree $treeRoot } catch { $rejected=$true }
 if(-not $rejected){throw 'Publish tree must reject hard-linked dependencies.'}
 Remove-Item -LiteralPath $hardlink -Force
 $junction=Join-Path $treeRoot 'redirect'
 New-Item -ItemType Junction -Path $junction -Target (Join-Path $treeRoot 'nested') | Out-Null
 try {
  $rejected=$false
  try { Assert-TrustedPublishTree $treeRoot } catch { $rejected=$true }
  if(-not $rejected){throw 'Publish tree must reject junctions without following them.'}
 } finally { [IO.Directory]::Delete($junction, $false) }
} finally {
 $resolved=[IO.Path]::GetFullPath($treeRoot);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'kr-tree-test-*'){throw 'Unsafe cleanup path.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output 'Installer trust, dependency traversal and rollback tests passed.'
