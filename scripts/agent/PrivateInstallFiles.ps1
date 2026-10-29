# File-only helpers: tests use disposable directories without SCM changes.
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
