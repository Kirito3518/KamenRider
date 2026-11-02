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
function Assert-TrustedInstallParent {
 param([Parameter(Mandatory)][string]$Path)
 $full=[IO.Path]::GetFullPath($Path)
 if($full.StartsWith('\\')){throw 'Network installation paths are not supported.'}
 $trusted=@([Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),[Security.Principal.WindowsIdentity]::GetCurrent().User)
 $current=$full
 while(-not (Test-Path -LiteralPath $current)) {$parent=[IO.Path]::GetDirectoryName($current);if(-not $parent -or $parent -eq $current){throw 'No existing trusted parent.'};$current=$parent}
 if(-not (Get-Item -LiteralPath $current -Force).PSIsContainer){$current=[IO.Path]::GetDirectoryName($current)}
 while($current){
  $item=Get-Item -LiteralPath $current -Force
  if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Installation path has a reparse point or non-directory ancestor.'}
  if(-not (Test-TrustedInstallAcl (Get-Acl -LiteralPath $current) $trusted -IncludeInheritedChildRules)){throw 'Installation path has an untrusted owner or writable ancestor.'}
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
 param([Parameter(Mandatory)][string]$Path,[switch]$Private)
 Assert-TrustedInstallParent $Path
 $file=Get-Item -LiteralPath $Path -Force
 if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Installation file must be a regular file, not a reparse point.'}
 if((Get-InstallFileLinkCount $Path) -ne 1){throw 'Installation file must not have multiple hard links.'}
 $trusted=@([Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),[Security.Principal.WindowsIdentity]::GetCurrent().User)
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
function Set-PrivateInstallAcl {
 param([string]$Path,[Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$Directory)
 $acl=if($Directory){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()}
 $acl.SetAccessRuleProtection($true,$false)
 $systemSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
 $adminSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
 # The installing owner already controls WRITE_DAC. Retain its access for staging/rollback.
 # The service installer requires this principal to be an elevated administrator.
 $installerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User
 foreach($sid in @($ServiceSid,$systemSid,$adminSid,$installerSid)){
  $rights=if($sid -eq $ServiceSid -and $sid -ne $installerSid){if($Directory){[Security.AccessControl.FileSystemRights]::Modify}else{[Security.AccessControl.FileSystemRights]::Read}}else{[Security.AccessControl.FileSystemRights]::FullControl}
  $rule=if($Directory){[Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)}else{[Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,[Security.AccessControl.AccessControlType]::Allow)}
  $acl.AddAccessRule($rule)
 }
 Set-Acl -LiteralPath $Path -AclObject $acl
}
function Initialize-PrivateDataDirectory {
 param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid)
 Assert-EmptyInstallDirectory $Path
 if(-not (Test-Path -LiteralPath $Path)){New-Item -ItemType Directory -Path $Path | Out-Null}
 Set-PrivateInstallAcl $Path $ServiceSid -Directory
 Assert-EmptyInstallDirectory $Path
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
 New-Item -ItemType Directory -Path $stage | Out-Null
 try {
  # Restrict the EMPTY staging directory before copying any configuration bytes.
  Set-PrivateInstallAcl $stage $ServiceSid -Directory
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
