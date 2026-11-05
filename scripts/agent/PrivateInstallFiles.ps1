# File-only helpers: tests use disposable directories without SCM changes.
function Test-TrustedInstallAcl {
 param([Parameter(Mandatory)][Security.AccessControl.FileSystemSecurity]$Acl,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier[]]$TrustedSids,[switch]$IncludeInheritedChildRules,[switch]$Private)
 $trusted=@($TrustedSids | ForEach-Object {$_.Value})
 if($Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){return $false}
 $write=[Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
 if($Private){$write=$write -bor [Security.AccessControl.FileSystemRights]::ReadData}
 foreach($rule in $Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
  if(($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and -not $IncludeInheritedChildRules){continue}
  if($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -notin $trusted -and ($rule.FileSystemRights -band $write)){return $false}
 }
 return $true
}
function Get-TrustedInstallSids {
 param([Security.Principal.SecurityIdentifier[]]$AdditionalSid=@())
 $known=@(
  [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
  [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),
  [Security.Principal.SecurityIdentifier]::new('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'),
  [Security.Principal.SecurityIdentifier]::new('S-1-3-0'),
  [Security.Principal.WindowsIdentity]::GetCurrent().User
 )
 return @($known + $AdditionalSid | Sort-Object -Property Value -Unique)
}
function Remove-InstallSynchronizeRight {
 param([Parameter(Mandatory)][int]$Rights)
 return ($Rights -band (-bnot [int][Security.AccessControl.FileSystemRights]::Synchronize))
}
function Test-TrustedProgramDataRootAcl {
 param([Parameter(Mandatory)][Security.AccessControl.DirectorySecurity]$Acl)
 if($Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-18'){return $false}
 $usersSid='S-1-5-32-545'
 $trusted=@(Get-TrustedInstallSids | ForEach-Object {$_.Value})
 $write=[Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
 foreach($rule in $Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
  if($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow){continue}
  if($rule.IdentityReference.Value -in $trusted){continue}
  if($rule.IdentityReference.Value -eq $usersSid){
   if(-not ($rule.FileSystemRights -band $write)){continue}
   if((Remove-InstallSynchronizeRight ([int]$rule.FileSystemRights)) -ne [int][Security.AccessControl.FileSystemRights]::Write -or
      $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::ContainerInherit -or
      $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or $rule.IsInherited){return $false}
   continue
  }
  if($rule.FileSystemRights -band $write){return $false}
 }
 $usersWrite=@($Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object {
  $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
  $_.IdentityReference.Value -eq $usersSid -and
  (Remove-InstallSynchronizeRight ([int]$_.FileSystemRights)) -eq [int][Security.AccessControl.FileSystemRights]::Write -and
  $_.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::ContainerInherit -and
  $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None -and
  -not $_.IsInherited
 })
 return $usersWrite.Count -eq 1
}
function Test-TrustedSystemRootAcl {
 param([Parameter(Mandatory)][Security.AccessControl.DirectorySecurity]$Acl)
 $trustedInstallerSid='S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
 if($Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $trustedInstallerSid){return $false}
 $authenticatedUsersSid='S-1-5-11'
 $trusted=@(Get-TrustedInstallSids | ForEach-Object {$_.Value})
 $write=[Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
 foreach($rule in $Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
  if($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow){continue}
  if($rule.IdentityReference.Value -in $trusted){continue}
  if($rule.IdentityReference.Value -eq $authenticatedUsersSid){
   if([int]$rule.FileSystemRights -eq -536805376 -and
      $rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' -and
      $rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::InheritOnly -and -not $rule.IsInherited){continue}
   if(-not ($rule.FileSystemRights -band $write)){continue}
   if((Remove-InstallSynchronizeRight ([int]$rule.FileSystemRights)) -ne [int][Security.AccessControl.FileSystemRights]::CreateDirectories -or
      $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
      $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or $rule.IsInherited){return $false}
   continue
  }
  if($rule.FileSystemRights -band $write){return $false}
 }
 $createDirectory=@($Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object {
  $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
  $_.IdentityReference.Value -eq $authenticatedUsersSid -and
  (Remove-InstallSynchronizeRight ([int]$_.FileSystemRights)) -eq [int][Security.AccessControl.FileSystemRights]::CreateDirectories -and
  $_.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::None -and
  $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None -and
  -not $_.IsInherited
 })
 $childInheritance=@($Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object {
  $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
  $_.IdentityReference.Value -eq $authenticatedUsersSid -and
  [int]$_.FileSystemRights -eq -536805376 -and
  $_.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' -and
  $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::InheritOnly -and
  -not $_.IsInherited
 })
 return $createDirectory.Count -eq 1 -and $childInheritance.Count -eq 1
}
function Assert-TrustedInstallParent {
 param([Parameter(Mandatory)][string]$Path,[Security.Principal.SecurityIdentifier]$ServiceSid)
 $full=[IO.Path]::GetFullPath($Path)
 if($full.StartsWith('\\')){throw 'Network installation paths are not supported.'}
 $trusted=Get-TrustedInstallSids @($ServiceSid)
 $programData=[IO.Path]::GetFullPath($env:ProgramData).TrimEnd([IO.Path]::DirectorySeparatorChar)
 $systemRoot=[IO.Path]::GetPathRoot($full).TrimEnd([IO.Path]::DirectorySeparatorChar)
 $current=$full
 while(-not (Test-Path -LiteralPath $current)) {$parent=[IO.Path]::GetDirectoryName($current);if(-not $parent -or $parent -eq $current){throw 'No existing trusted parent.'};$current=$parent}
 if(-not (Get-Item -LiteralPath $current -Force).PSIsContainer){$current=[IO.Path]::GetDirectoryName($current)}
 while($current){
  $item=Get-Item -LiteralPath $current -Force
  if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Installation path has a reparse point or non-directory ancestor.'}
  $acl=Get-Acl -LiteralPath $current
  $currentFull=[IO.Path]::GetFullPath($current).TrimEnd([IO.Path]::DirectorySeparatorChar)
  if([string]::Equals($currentFull,$systemRoot,[StringComparison]::OrdinalIgnoreCase)){
   if(-not (Test-TrustedSystemRootAcl $acl)){throw 'System drive root has unexpected ownership or writable access.'}
  }elseif([string]::Equals($currentFull,$programData,[StringComparison]::OrdinalIgnoreCase)){
   if(-not (Test-TrustedProgramDataRootAcl $acl)){throw 'ProgramData root has unexpected ownership or writable access.'}
  }elseif(-not (Test-TrustedInstallAcl $acl $trusted -IncludeInheritedChildRules)){throw 'Installation path has an untrusted owner or writable ancestor.'}
  $parent=[IO.Path]::GetDirectoryName($current);if(-not $parent -or $parent -eq $current){break};$current=$parent
 }
}
function Get-InstallFileLinkCount {
 param([Parameter(Mandatory)][string]$Path)
 if(-not ('KamenRaider.Install.LinkInfo' -as [type])){
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace KamenRaider.Install {
 [StructLayout(LayoutKind.Sequential)] public struct FileInfo {
  public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
  public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
 }
 public static class LinkInfo {
  [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo information);
 }
}
'@
 }
 $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try {
  $info=[KamenRaider.Install.FileInfo]::new()
  if(-not [KamenRaider.Install.LinkInfo]::GetFileInformationByHandle($stream.SafeFileHandle,[ref]$info)){throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())}
  return $info.Links
 } finally {$stream.Dispose()}
}
function Assert-TrustedInstallFile {
 param([Parameter(Mandatory)][string]$Path,[Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$Private)
 Assert-TrustedInstallParent $Path
 $file=Get-Item -LiteralPath $Path -Force
 if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Installation file must be a regular file, not a reparse point.'}
 if((Get-InstallFileLinkCount $Path) -ne 1){throw 'Installation file must not have multiple hard links.'}
 $trusted=Get-TrustedInstallSids @($ServiceSid)
 if(-not (Test-TrustedInstallAcl (Get-Acl -LiteralPath $Path) $trusted -Private:$Private)){throw 'Installation file has untrusted access or ownership.'}
}
function Assert-TrustedPublishTree {
 param([Parameter(Mandatory)][string]$Path)
 # Walk one directory at a time so a junction is rejected before traversal.
 $root=Get-Item -LiteralPath $Path -Force -ErrorAction Stop
 if(-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Publish directory must be a real directory.'}
 $pending=[Collections.Generic.Stack[string]]::new()
 $pending.Push($root.FullName)
 while($pending.Count -gt 0){
  $directory=$pending.Pop()
  Assert-TrustedInstallParent $directory
  foreach($item in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)){
   if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Publish tree must not contain reparse points.'}
   if($item.PSIsContainer){$pending.Push($item.FullName);continue}
   if((Get-InstallFileLinkCount $item.FullName) -ne 1){throw 'Publish tree files must not have multiple hard links.'}
   Assert-TrustedInstallFile $item.FullName
  }
 }
}
function Assert-EmptyInstallDirectory {
 param([Parameter(Mandatory)][string]$Path)
 if(Test-Path -LiteralPath $Path){
  $item=Get-Item -LiteralPath $Path -Force
  if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Data directory must be a real directory.'}
  if(@(Get-ChildItem -LiteralPath $Path -Force).Count -ne 0){throw 'Data directory must be empty.'}
 }
}
function Assert-ServiceIsAbsent {
 param([Parameter(Mandatory)][string]$ServiceName)
 if(Get-Service -Name $ServiceName -ErrorAction SilentlyContinue){throw 'Service already exists; use a reviewed upgrade procedure.'}
}
function Resolve-AgentNetworkAdapter {
 param([Parameter(Mandatory)][object[]]$Candidates,[string]$RequestedId,[scriptblock]$ReadSelection={param($Prompt) Read-Host -Prompt $Prompt})
 if($Candidates.Count -eq 0){throw 'No active IPv4 network adapter with a default gateway was found.'}
 $normalizedCandidates=@($Candidates | ForEach-Object {
  $candidate=$_
  try{$guid=[Guid]::Parse([string]$candidate.Id)}catch{throw 'Network adapter inventory returned an invalid interface GUID.'}
  [pscustomobject]@{Id=[string]$candidate.Id;GuidKey=$guid.ToString('D');Name=[string]$candidate.Name;Address=[string]$candidate.Address}
 } | Sort-Object -Property Id -Unique)
 if(-not [string]::IsNullOrWhiteSpace($RequestedId)){
  try{$requested=([Guid]::Parse($RequestedId)).ToString('D')}catch{throw 'NetworkAdapterId must be a network interface GUID.'}
  $selected=@($normalizedCandidates | Where-Object {$_.GuidKey -eq $requested})
  if($selected.Count -ne 1){throw 'NetworkAdapterId does not identify an active IPv4 network adapter with a default gateway.'}
  return $selected[0].Id
 }
 if($normalizedCandidates.Count -eq 1){return $normalizedCandidates[0].Id}
 Write-Host 'Select the active network adapter for Controller enrollment:'
 for($index=0;$index -lt $normalizedCandidates.Count;$index++){
  Write-Host ('[{0}] {1} ({2}) {3}' -f ($index+1),$normalizedCandidates[$index].Name,$normalizedCandidates[$index].Id,$normalizedCandidates[$index].Address)
 }
 $selection=0
 while($selection -lt 1 -or $selection -gt $normalizedCandidates.Count){
  $answer=& $ReadSelection ('Enter a number from 1 to {0}' -f $normalizedCandidates.Count)
  $parsed=0
  if([int]::TryParse([string]$answer,[ref]$parsed)){$selection=$parsed}else{$selection=0}
 }
 return $normalizedCandidates[$selection-1].Id
}
function New-AgentInstallConfiguration {
 param(
  [Parameter(Mandatory)][string]$ControllerUrl,
  [Parameter(Mandatory)][string]$DataDirectory,
  [Parameter(Mandatory)][ValidateRange(1,65535)][int]$MoonlightPort,
  [Parameter(Mandatory)][ValidateRange(1,65535)][int]$SunshinePort,
  [Parameter(Mandatory)][string]$NetworkAdapterId
 )
 $configuration=[ordered]@{
  ControllerUrl=$ControllerUrl
  DataDirectory=$DataDirectory
  MoonlightPort=$MoonlightPort
  SunshinePort=$SunshinePort
  NetworkAdapterId=$NetworkAdapterId
 }
 return ConvertTo-Json -InputObject $configuration -Depth 3
}
function Get-AgentEnrollmentStatus {
 param([psobject]$Receipt)
 if($null -eq $Receipt){
  return [pscustomobject]@{
   State='pending'
   MachineId=$null
   ApprovalStatus=$null
   Fingerprint=$null
   Message='Enrollment is pending: the service remains installed and will retry while offline. Return to /admin/machines after the Controller is reachable.'
  }
 }
 if([string]::IsNullOrWhiteSpace([string]$Receipt.MachineId) -or $Receipt.ApprovalStatus -notin @('pending','approved') -or [string]$Receipt.PublicKeyFingerprint -notmatch '^[A-Fa-f0-9]{64}$'){
  throw 'Enrollment receipt is invalid.'
 }
 $fingerprint=[string]$Receipt.PublicKeyFingerprint
 return [pscustomobject]@{
  State='enrolled'
  MachineId=[string]$Receipt.MachineId
  ApprovalStatus=[string]$Receipt.ApprovalStatus
  Fingerprint=$fingerprint
  Message=('Enrollment received: MachineId={0}; Status={1}; Fingerprint={2}' -f $Receipt.MachineId,$Receipt.ApprovalStatus,$fingerprint)
 }
}
function New-PrivateInstallSecurity {
 param([Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$Directory,[switch]$ReadOnly)
 $acl=if($Directory){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()}
 $acl.SetAccessRuleProtection($true,$false)
 $systemSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
 $adminSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
 $installerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User
 $acl.SetOwner($installerSid)
 # The installing owner already controls WRITE_DAC. Retain its access for staging/rollback.
 # The service installer requires this principal to be an elevated administrator.
 foreach($sid in @($ServiceSid,$systemSid,$adminSid,$installerSid)){
  $rights=if($sid -eq $ServiceSid -and $sid -ne $installerSid){
   if($ReadOnly){[Security.AccessControl.FileSystemRights]::ReadAndExecute}
   elseif($Directory){[Security.AccessControl.FileSystemRights]::Modify}
   else{[Security.AccessControl.FileSystemRights]::Read}
  }else{[Security.AccessControl.FileSystemRights]::FullControl}
  $rule=if($Directory){[Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)}else{[Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,[Security.AccessControl.AccessControlType]::Allow)}
  $acl.AddAccessRule($rule)
 }
 return $acl
}
function Set-PrivateInstallAcl {
 param([string]$Path,[Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$Directory,[switch]$ReadOnly)
 $acl=New-PrivateInstallSecurity $ServiceSid -Directory:$Directory -ReadOnly:$ReadOnly
 Set-Acl -LiteralPath $Path -AclObject $acl
}
function Initialize-PrivateDirectoryNativeType {
 if(-not ('KamenRaider.Install.NativeDirectories' -as [type])){
  Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace KamenRaider.Install {
 public static class NativeDirectories {
  [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes {
   public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
  }
  [DllImport("kernel32.dll", EntryPoint="CreateDirectoryW", CharSet=CharSet.Unicode, SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryNative(string path, ref SecurityAttributes attributes);
  public static void CreateDirectoryWithSecurity(string path, byte[] descriptor) {
   IntPtr securityDescriptor=Marshal.AllocHGlobal(descriptor.Length);
   try {
    Marshal.Copy(descriptor,0,securityDescriptor,descriptor.Length);
    SecurityAttributes attributes=new SecurityAttributes { Length=Marshal.SizeOf(typeof(SecurityAttributes)), SecurityDescriptor=securityDescriptor, InheritHandle=false };
    if(!CreateDirectoryNative(path,ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error());
   } finally { Marshal.FreeHGlobal(securityDescriptor); }
  }
 }
}
'@
 }
}
function New-PrivateInstallDirectory {
 param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$ReadOnly)
 if(-not [IO.Path]::IsPathRooted($Path)){throw 'Private directory path must be absolute.'}
 $parent=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
 if(-not (Test-Path -LiteralPath $parent -PathType Container)){throw 'Private directory parent must already exist.'}
 $acl=New-PrivateInstallSecurity $ServiceSid -Directory -ReadOnly:$ReadOnly
 Initialize-PrivateDirectoryNativeType
 [KamenRaider.Install.NativeDirectories]::CreateDirectoryWithSecurity([IO.Path]::GetFullPath($Path),$acl.GetSecurityDescriptorBinaryForm())
 $item=Get-Item -LiteralPath $Path -Force
 if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Private directory must be a real directory.'}
 $actual=Get-Acl -LiteralPath $Path
 $trusted=Get-TrustedInstallSids @($ServiceSid)
 if(-not $actual.AreAccessRulesProtected -or -not (Test-TrustedInstallAcl $actual $trusted -IncludeInheritedChildRules)){throw 'Private directory was already present with unsafe access or ownership.'}
 return $item
}
function Initialize-PrivateDataDirectory {
 param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid)
 Assert-EmptyInstallDirectory $Path
 $created=$false
 if(Test-Path -LiteralPath $Path){
  $existingAcl=Get-Acl -LiteralPath $Path
  $trusted=Get-TrustedInstallSids @($ServiceSid)
  if(-not $existingAcl.AreAccessRulesProtected -or -not (Test-TrustedInstallAcl $existingAcl $trusted -IncludeInheritedChildRules)){throw 'Existing data directory has unsafe access or ownership.'}
 }else{
  New-PrivateInstallDirectory $Path $ServiceSid | Out-Null
  $created=$true
 }
 Assert-EmptyInstallDirectory $Path
 return $created
}
function Publish-PrivateConfiguration {
 param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid)
 if(-not ('KamenRaider.Install.NativeFiles' -as [type])){
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace KamenRaider.Install {
 public static class NativeFiles {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool MoveFileEx(string existingFile, string newFile, int flags);
 }
}
'@
 }
 $publishRoot=[IO.Path]::GetFullPath($PublishDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
 $stage=Join-Path $publishRoot ('.install-'+[Guid]::NewGuid().ToString('N'))
 $destination=Join-Path $publishRoot 'appsettings.json'
 if((Get-Item -LiteralPath $publishRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Publish directory must not be a reparse point.'}
 New-PrivateInstallDirectory $stage $ServiceSid -ReadOnly | Out-Null
 try {
  $temporary=Join-Path $stage 'appsettings.json'
  $empty=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$empty.Dispose()
  Set-PrivateInstallAcl $temporary $ServiceSid
  $inputFile=[IO.File]::OpenRead($Source)
  try {
   $outputFile=[IO.FileStream]::new($temporary,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
   try {$inputFile.CopyTo($outputFile);$outputFile.Flush($true)}finally{$outputFile.Dispose()}
  }finally{$inputFile.Dispose()}
  # Same-volume rename retains source explicit DACL; File.Replace can retain the old target ACL.
  if(-not [KamenRaider.Install.NativeFiles]::MoveFileEx($temporary,$destination,9)){
   throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
  }
 }finally{
  if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -ne $publishRoot){throw 'Unsafe staging cleanup path.'}
  Remove-Item -LiteralPath $stage -Recurse -Force
 }
}
