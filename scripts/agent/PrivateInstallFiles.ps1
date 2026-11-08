# File-only helpers: tests use disposable directories without SCM changes.
function Test-TrustedInstallAcl {
 param([Parameter(Mandatory)][Security.AccessControl.FileSystemSecurity]$Acl,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier[]]$TrustedSids,[switch]$IncludeInheritedChildRules,[switch]$Private,[Security.Principal.SecurityIdentifier]$RequireModifySid)
 $trusted=@($TrustedSids | ForEach-Object {$_.Value})
 if($Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){return $false}
 $rules=@($Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
 foreach($rule in $rules) {
  if(($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and -not $IncludeInheritedChildRules){continue}
  if($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -notin $trusted -and (Test-DisallowedInstallAceRights ([int]$rule.FileSystemRights) -Private:$Private)){return $false}
 }
 if($RequireModifySid){
  $modify=[Security.AccessControl.FileSystemRights]::Modify
  $serviceRules=@($rules | Where-Object {$_.IdentityReference.Value -eq $RequireModifySid.Value})
  $hasModify=@($serviceRules | Where-Object {$_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and ((([int]$_.FileSystemRights -band [int]$modify) -eq [int]$modify) -or (([int]$_.FileSystemRights -band [int]268435456) -ne 0)) }).Count -gt 0
  $deniesModify=@($serviceRules | Where-Object {$_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Deny -and (Test-DisallowedInstallAceRights ([int]$_.FileSystemRights) -Private) }).Count -gt 0
  if(-not $hasModify -or $deniesModify){return $false}
 }
 return $true
}
function Test-DisallowedInstallAceRights {
 param([Parameter(Mandatory)][int]$Rights,[switch]$Private)
 $write=[Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
 if($Private){$write=$write -bor [Security.AccessControl.FileSystemRights]::Read}
 $genericAll=($Rights -band [int]268435456) -ne 0
 $genericWrite=($Rights -band [int]1073741824) -ne 0
 $genericRead=($Rights -band [int]-2147483648) -ne 0
 return (($Rights -band [int]$write) -ne 0 -or $genericAll -or $genericWrite -or ($Private -and $genericRead))
}

function Get-ServiceSid {
 param([Parameter(Mandatory)][string]$ServiceName)
 $scPath=Join-Path $env:SystemRoot 'System32\sc.exe'
 $output=@(& $scPath showsid $ServiceName 2>&1)
 if($LASTEXITCODE -ne 0){throw "Unable to derive the virtual service SID for $ServiceName."}
 $matches=[regex]::Matches(($output -join "`n"),'S-1-5-80(?:-\d+){5}')
 if($matches.Count -ne 1){throw "Service SID output for $ServiceName did not contain exactly one service SID."}
 return [Security.Principal.SecurityIdentifier]::new($matches[0].Value)
}
function Assert-ServiceSidMatchesVirtualAccount {
 param([Parameter(Mandatory)][string]$ServiceName,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ExpectedSid)
 $account=([Security.Principal.NTAccount]::new("NT SERVICE\$ServiceName")).Translate([Security.Principal.SecurityIdentifier])
 if($account.Value -ne $ExpectedSid.Value){throw "The created virtual account SID for $ServiceName does not match its precomputed service SID."}
}
function Get-ServiceBinaryPath {
 param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$ContentRoot)
 $executablePath=[IO.Path]::GetFullPath($Executable)
 $contentRootPath=[IO.Path]::GetFullPath($ContentRoot)
 if($executablePath.Contains('"') -or $contentRootPath.Contains('"')){throw 'Service paths cannot contain quotes.'}
 return ('"{0}" --contentRoot "{1}"' -f $executablePath,$contentRootPath)
}
function Initialize-ServiceManagerNativeType {
 # Add-Type cannot replace a type already loaded in this PowerShell process. Use
 # a versioned name so a package update can rerun in the same elevated session.
 if(-not ('KamenRaider.Install.NativeServiceManagerV2' -as [type])){
  Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace KamenRaider.Install {
 public static class NativeServiceManagerV2 {
  [DllImport("advapi32.dll", EntryPoint="OpenSCManagerW", CharSet=CharSet.Unicode, SetLastError=true)]
  private static extern IntPtr OpenSCManager(string machineName,string databaseName,uint desiredAccess);
  [DllImport("advapi32.dll", EntryPoint="CreateServiceW", CharSet=CharSet.Unicode, SetLastError=true)]
  private static extern IntPtr CreateService(IntPtr manager,string serviceName,string displayName,uint desiredAccess,uint serviceType,uint startType,uint errorControl,string binaryPathName,string loadOrderGroup,IntPtr tagId,IntPtr dependencies,string serviceStartName,string password);
  [DllImport("advapi32.dll", EntryPoint="OpenServiceW", CharSet=CharSet.Unicode, SetLastError=true)]
  private static extern IntPtr OpenService(IntPtr manager,string serviceName,uint desiredAccess);
  [DllImport("advapi32.dll", EntryPoint="QueryServiceConfigW", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool QueryServiceConfig(IntPtr service,IntPtr config,int bufferSize,out int bytesNeeded);
  [DllImport("advapi32.dll", EntryPoint="ChangeServiceConfigW", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ChangeServiceConfig(IntPtr service,uint serviceType,uint startType,uint errorControl,string binaryPath,string loadOrderGroup,IntPtr tagId,IntPtr dependencies,string account,string password,string displayName);
   [DllImport("advapi32.dll", EntryPoint="ChangeServiceConfig2W", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool ChangeServiceConfig2(IntPtr service,uint infoLevel,IntPtr info);
  [DllImport("advapi32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CloseServiceHandle(IntPtr handle);
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  private struct ServiceConfig {
   public uint ServiceType; public uint StartType; public uint ErrorControl; public IntPtr BinaryPathName;
   public IntPtr LoadOrderGroup; public uint TagId; public IntPtr Dependencies; public IntPtr ServiceStartName; public IntPtr DisplayName;
  }
  public sealed class ServiceConfiguration {
   public string BinaryPath { get; set; } public uint StartType { get; set; }
   public string Account { get; set; } public string[] Dependencies { get; set; }
  }
   [StructLayout(LayoutKind.Sequential)]
   private struct ServiceAction { public uint Type; public uint Delay; }
   [StructLayout(LayoutKind.Sequential)]
   private struct ServiceFailureActions {
    public uint ResetPeriod; public IntPtr RebootMessage; public IntPtr Command;
    public uint ActionCount; public IntPtr Actions;
   }
   [StructLayout(LayoutKind.Sequential)]
   private struct ServiceFailureActionsFlag { public int Enabled; }
  private static string[] ReadMultiString(IntPtr value) {
   if(value==IntPtr.Zero) return new string[0];
   var result=new System.Collections.Generic.List<string>(); int offset=0;
   while(offset<65536) {
    var current=Marshal.PtrToStringUni(IntPtr.Add(value,offset*2));
    if(String.IsNullOrEmpty(current)) return result.ToArray();
    result.Add(current); offset+=current.Length+1;
   }
   throw new InvalidOperationException("Windows service dependency list is not terminated.");
  }
  private static IntPtr MakeMultiString(string[] values) {
   if(values==null) values=new string[0];
   var clean=new System.Collections.Generic.List<string>();
   var seen=new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var value in values) {
    if(String.IsNullOrWhiteSpace(value) || value.IndexOf('\0')>=0) throw new ArgumentException("Windows service dependency is invalid.");
    if(seen.Add(value)) clean.Add(value);
   }
   return Marshal.StringToHGlobalUni(String.Join("\0",clean.ToArray())+"\0\0");
  }
  public static ServiceConfiguration QueryConfiguration(string serviceName) {
   IntPtr manager=OpenSCManager(null,null,0x0001);
   if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
   try {
    IntPtr service=OpenService(manager,serviceName,0x0001);
    if(service==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    try {
     int size=0; QueryServiceConfig(service,IntPtr.Zero,0,out size); int error=Marshal.GetLastWin32Error();
     if(error!=122 || size<=0) throw new Win32Exception(error);
     IntPtr buffer=Marshal.AllocHGlobal(size);
     try {
      if(!QueryServiceConfig(service,buffer,size,out size)) throw new Win32Exception(Marshal.GetLastWin32Error());
      var config=(ServiceConfig)Marshal.PtrToStructure(buffer,typeof(ServiceConfig));
      return new ServiceConfiguration {
       BinaryPath=Marshal.PtrToStringUni(config.BinaryPathName), StartType=config.StartType,
       Account=Marshal.PtrToStringUni(config.ServiceStartName), Dependencies=ReadMultiString(config.Dependencies)
      };
     } finally {Marshal.FreeHGlobal(buffer);}
    } finally {CloseServiceHandle(service);}
   } finally {CloseServiceHandle(manager);}
  }
  public static void SetStartAndDependencies(string serviceName,uint startType,string[] dependencies) {
   if(startType<2 || startType>4) throw new ArgumentOutOfRangeException("startType");
   IntPtr manager=OpenSCManager(null,null,0x0001);
   if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
   try {
    IntPtr service=OpenService(manager,serviceName,0x0002);
    if(service==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    try {
     IntPtr dependencyValue=MakeMultiString(dependencies);
     try {if(!ChangeServiceConfig(service,0xFFFFFFFF,startType,0xFFFFFFFF,null,null,IntPtr.Zero,dependencyValue,null,null,null)) throw new Win32Exception(Marshal.GetLastWin32Error());}
     finally {Marshal.FreeHGlobal(dependencyValue);}
    } finally {CloseServiceHandle(service);}
   } finally {CloseServiceHandle(manager);}
  }
   public static void SetFailureActions(string serviceName,uint resetPeriod,uint[] actionTypes,uint[] delays,string command) {
    if(String.IsNullOrWhiteSpace(serviceName)) throw new ArgumentException("Service name is required.","serviceName");
    if(actionTypes==null || delays==null || actionTypes.Length==0 || actionTypes.Length!=delays.Length) throw new ArgumentException("Failure action types and delays must have the same non-zero length.");
    if(String.IsNullOrWhiteSpace(command)) throw new ArgumentException("Failure recovery command is required.","command");
    for(int i=0;i<actionTypes.Length;i++) if(actionTypes[i]>3) throw new ArgumentOutOfRangeException("actionTypes");
    IntPtr manager=OpenSCManager(null,null,0x0001);
    if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    try {
     IntPtr service=OpenService(manager,serviceName,0x0012);
     if(service==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
     try {
      int actionSize=Marshal.SizeOf(typeof(ServiceAction));
      IntPtr actionBuffer=Marshal.AllocHGlobal(actionSize*actionTypes.Length);
      IntPtr commandBuffer=IntPtr.Zero;
      IntPtr configBuffer=IntPtr.Zero;
      try {
       commandBuffer=Marshal.StringToHGlobalUni(command);
       for(int i=0;i<actionTypes.Length;i++) {
        IntPtr current=IntPtr.Add(actionBuffer,i*actionSize);
        Marshal.StructureToPtr(new ServiceAction { Type=actionTypes[i], Delay=delays[i] },current,false);
       }
       var config=new ServiceFailureActions {
        ResetPeriod=resetPeriod, RebootMessage=IntPtr.Zero, Command=commandBuffer,
        ActionCount=(uint)actionTypes.Length, Actions=actionBuffer
       };
       configBuffer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ServiceFailureActions)));
       Marshal.StructureToPtr(config,configBuffer,false);
       if(!ChangeServiceConfig2(service,2,configBuffer)) throw new Win32Exception(Marshal.GetLastWin32Error());
      } finally {
       if(configBuffer!=IntPtr.Zero) Marshal.FreeHGlobal(configBuffer);
       if(commandBuffer!=IntPtr.Zero) Marshal.FreeHGlobal(commandBuffer);
       Marshal.FreeHGlobal(actionBuffer);
      }
     } finally {CloseServiceHandle(service);}
    } finally {CloseServiceHandle(manager);}
   }
   public static void SetFailureActionsFlag(string serviceName,bool enabled) {
    if(String.IsNullOrWhiteSpace(serviceName)) throw new ArgumentException("Service name is required.","serviceName");
    IntPtr manager=OpenSCManager(null,null,0x0001);
    if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    try {
     IntPtr service=OpenService(manager,serviceName,0x0002);
     if(service==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
     try {
      var config=new ServiceFailureActionsFlag { Enabled=enabled?1:0 };
      IntPtr buffer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ServiceFailureActionsFlag)));
      try {
       Marshal.StructureToPtr(config,buffer,false);
       if(!ChangeServiceConfig2(service,4,buffer)) throw new Win32Exception(Marshal.GetLastWin32Error());
      } finally {Marshal.FreeHGlobal(buffer);}
     } finally {CloseServiceHandle(service);}
    } finally {CloseServiceHandle(manager);}
   }
  public static void CreateDisabledService(string serviceName,string displayName,string binaryPath,string account,string[] serviceDependencies) {
   IntPtr manager=OpenSCManager(null,null,0x0002);
   if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
   try {
    IntPtr dependencies=MakeMultiString(serviceDependencies);
    try {
     IntPtr service=CreateService(manager,serviceName,displayName,0x000F01FF,0x00000010,0x00000004,0x00000001,binaryPath,null,IntPtr.Zero,dependencies,account,null);
     if(service==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
     try {} finally {CloseServiceHandle(service);}
    } finally {Marshal.FreeHGlobal(dependencies);}
   } finally {CloseServiceHandle(manager);}
  }
 }
}
'@
 }
}
function New-DisabledWindowsService {
 param([Parameter(Mandatory)][string]$ServiceName,[Parameter(Mandatory)][string]$BinaryPath,[Parameter(Mandatory)][string]$Account,[string[]]$Dependencies=@())
 Initialize-ServiceManagerNativeType
 [KamenRaider.Install.NativeServiceManagerV2]::CreateDisabledService($ServiceName,$ServiceName,$BinaryPath,$Account,$Dependencies)
}
function Set-WindowsServiceFailureActions {
 param([Parameter(Mandatory)][string]$ServiceName,[Parameter(Mandatory)][uint32]$ResetPeriod,[Parameter(Mandatory)][uint32[]]$ActionTypes,[Parameter(Mandatory)][uint32[]]$Delays,[Parameter(Mandatory)][string]$Command)
 if($ActionTypes.Count -eq 0 -or $ActionTypes.Count -ne $Delays.Count){throw 'Service failure action types and delays must have the same non-zero length.'}
 Initialize-ServiceManagerNativeType
 [KamenRaider.Install.NativeServiceManagerV2]::SetFailureActions($ServiceName,$ResetPeriod,$ActionTypes,$Delays,$Command)
}
function Set-WindowsServiceFailureActionsFlag {
 param([Parameter(Mandatory)][string]$ServiceName,[Parameter(Mandatory)][bool]$Enabled)
 Initialize-ServiceManagerNativeType
 [KamenRaider.Install.NativeServiceManagerV2]::SetFailureActionsFlag($ServiceName,$Enabled)
}
function Ensure-AgentEventLogSource {
 param([Parameter(Mandatory)][string]$SourceName)
 if($env:OS -ne 'Windows_NT'){throw 'Windows is required to register the Agent event source.'}
 if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
  throw 'Run Agent installation from an elevated PowerShell session to register its Event Log source.'
 }
 if(-not ('System.Diagnostics.EventLog' -as [type])){throw 'Windows Event Log APIs are unavailable in this PowerShell session.'}
 if([Diagnostics.EventLog]::SourceExists($SourceName,'.')){
  $existingLog=[Diagnostics.EventLog]::LogNameFromSourceName($SourceName,'.')
  if(-not [string]::Equals($existingLog,'Application',[StringComparison]::OrdinalIgnoreCase)){
   throw "Agent event source '$SourceName' is already registered to '$existingLog'; refusing to remap it."
  }
  return
 }
 [Diagnostics.EventLog]::CreateEventSource($SourceName,'Application')
 $sourceKey="Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\Application\$SourceName"
 if(-not (Test-Path -LiteralPath $sourceKey)){throw "Agent Event Log source '$SourceName' was not published."}
}
function Get-AgentEnrollmentWaitState {
 param([Parameter(Mandatory)][string]$ServiceStatus,[psobject]$ReceiptStatus)
 if($ServiceStatus -eq 'Running'){
  if($ReceiptStatus -and $ReceiptStatus.State -eq 'enrolled'){return 'enrolled'}
  return 'pending'
 }
 if($ServiceStatus -in @('StartPending','ContinuePending','PausePending')){return 'pending'}
 if($ServiceStatus -in @('Stopped','StopPending','Paused')){return 'unavailable'}
 return 'unavailable'
}
function Get-AgentEnrollmentRecoveryInstruction {
 param([Parameter(Mandatory)][string]$ServiceStatus)
 switch($ServiceStatus){
  'Stopped' { return "Agent enrollment is incomplete. The protected installation and local identity were kept. Fix Controller connectivity or certificate trust, then run Start-Service -Name 'KamenRaider Agent' before the enrollment ticket expires. Do not rerun the installer." }
  'Paused' { return "Agent enrollment is incomplete. The protected installation and local identity were kept. Fix Controller connectivity or certificate trust, then run Resume-Service -Name 'KamenRaider Agent' before the enrollment ticket expires. Do not rerun the installer." }
  default { return "Agent enrollment is incomplete. The protected installation and local identity were kept. Check Get-Service -Name 'KamenRaider Agent', resolve the service state, then let enrollment retry before the enrollment ticket expires. Do not rerun the installer." }
 }
}
function Get-AgentInstallMode {
 param([Parameter(Mandatory)][bool]$ServiceExists,[Parameter(Mandatory)][bool]$InstallDirectoryExists,[Parameter(Mandatory)][bool]$DataDirectoryExists,[bool]$GuardServiceExists=$false,[bool]$EmptyIdentityDirectoryResidue=$false)
 if(-not $ServiceExists -and -not $InstallDirectoryExists -and (-not $DataDirectoryExists -or $EmptyIdentityDirectoryResidue) -and -not $GuardServiceExists){return 'install'}
 if($ServiceExists -and $InstallDirectoryExists -and $DataDirectoryExists -and $GuardServiceExists){return 'repair-enrollment'}
 throw 'Agent installation is incomplete or inconsistent; refusing to overwrite service, binaries or identity data.'
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
 foreach($rule in $Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
  if($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow){continue}
  if($rule.IdentityReference.Value -in $trusted){continue}
  $ruleWrites=(Test-DisallowedInstallAceRights ([int]$rule.FileSystemRights))
  if($rule.IdentityReference.Value -eq $usersSid){
   if(-not $ruleWrites){continue}
   if((Remove-InstallSynchronizeRight ([int]$rule.FileSystemRights)) -ne [int][Security.AccessControl.FileSystemRights]::Write -or
      $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::ContainerInherit -or
      $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or $rule.IsInherited){return $false}
   continue
  }
  if($ruleWrites){return $false}
 }
 $usersWrite=@($Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object {
  $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
  $_.IdentityReference.Value -eq $usersSid -and
  (Test-DisallowedInstallAceRights ([int]$_.FileSystemRights)) -and
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
 foreach($rule in $Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
  if($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow){continue}
  if($rule.IdentityReference.Value -in $trusted){continue}
  $ruleWrites=(Test-DisallowedInstallAceRights ([int]$rule.FileSystemRights))
  if($rule.IdentityReference.Value -eq $authenticatedUsersSid){
   if([int]$rule.FileSystemRights -eq -536805376 -and
      $rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' -and
      $rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::InheritOnly -and -not $rule.IsInherited){continue}
   if(-not $ruleWrites){continue}
   if((Remove-InstallSynchronizeRight ([int]$rule.FileSystemRights)) -ne [int][Security.AccessControl.FileSystemRights]::CreateDirectories -or
      $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
      $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or $rule.IsInherited){return $false}
   continue
  }
  if($ruleWrites){return $false}
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
 Assert-TrustedInstallParent $Path -ServiceSid $ServiceSid
 $file=Get-Item -LiteralPath $Path -Force
 if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Installation file must be a regular file, not a reparse point.'}
 if((Get-InstallFileLinkCount $Path) -ne 1){throw 'Installation file must not have multiple hard links.'}
 $trusted=Get-TrustedInstallSids @($ServiceSid)
 if(-not (Test-TrustedInstallAcl (Get-Acl -LiteralPath $Path) $trusted -Private:$Private)){throw 'Installation file has untrusted access or ownership.'}
}
function Assert-TrustedPublishTree {
 param([Parameter(Mandatory)][string]$Path,[Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$Private)
 # Walk one directory at a time so a junction is rejected before traversal.
 $root=Get-Item -LiteralPath $Path -Force -ErrorAction Stop
 if(-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Publish directory must be a real directory.'}
 $pending=[Collections.Generic.Stack[string]]::new()
 $pending.Push($root.FullName)
 while($pending.Count -gt 0){
  $directory=$pending.Pop()
  Assert-TrustedInstallParent $directory -ServiceSid $ServiceSid
  if($Private -and -not (Test-TrustedInstallAcl (Get-Acl -LiteralPath $directory) (Get-TrustedInstallSids @($ServiceSid)) -IncludeInheritedChildRules -Private)){throw 'Private tree directory has untrusted access or ownership.'}
  foreach($item in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)){
   if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Publish tree must not contain reparse points.'}
   if($item.PSIsContainer){$pending.Push($item.FullName);continue}
   if((Get-InstallFileLinkCount $item.FullName) -ne 1){throw 'Publish tree files must not have multiple hard links.'}
   Assert-TrustedInstallFile $item.FullName -ServiceSid $ServiceSid -Private:$Private
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
function Test-EmptyAgentIdentityDirectoryResidue {
 param([Parameter(Mandatory)][string]$DataDirectory,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid)
 if(-not (Test-Path -LiteralPath $DataDirectory -PathType Container)){return $false}
 $dataItem=Get-Item -LiteralPath $DataDirectory -Force
 if($dataItem.Attributes -band [IO.FileAttributes]::ReparsePoint){return $false}
 $identityPath=Join-Path $dataItem.FullName 'identity'
 $children=@(Get-ChildItem -LiteralPath $DataDirectory -Force -ErrorAction Stop)
 if($children.Count -ne 1 -or $children[0].Name -ine 'identity' -or -not $children[0].PSIsContainer -or ($children[0].Attributes -band [IO.FileAttributes]::ReparsePoint)){return $false}
 if(@(Get-ChildItem -LiteralPath $identityPath -Force -ErrorAction Stop).Count -ne 0){return $false}
 $trusted=Get-TrustedInstallSids @($ServiceSid)
 $dataAcl=Get-Acl -LiteralPath $DataDirectory -ErrorAction Stop
 $identityAcl=Get-Acl -LiteralPath $identityPath -ErrorAction Stop
 if(-not $dataAcl.AreAccessRulesProtected -or
    -not (Test-TrustedInstallAcl $dataAcl $trusted -Private -RequireModifySid $ServiceSid) -or
    $identityAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $ServiceSid.Value -or
    -not (Test-TrustedInstallAcl $identityAcl $trusted -IncludeInheritedChildRules -Private -RequireModifySid $ServiceSid)){return $false}
 return $true
}
function Assert-AgentDataDirectoryReady {
 param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$AllowEmptyIdentityDirectory)
 if(-not (Test-Path -LiteralPath $Path)){return}
 $item=Get-Item -LiteralPath $Path -Force
 if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Data directory must be a real directory.'}
 if(@(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop).Count -eq 0){return}
 if($AllowEmptyIdentityDirectory -and (Test-EmptyAgentIdentityDirectoryResidue -DataDirectory $Path -ServiceSid $ServiceSid)){return}
 throw 'Data directory must be empty or contain only a verified empty identity directory from interrupted Agent startup.'
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
function Get-AgentEnrollmentStatusFromDataDirectory {
 param([Parameter(Mandatory)][string]$DataDirectory)
 $receiptPath=Join-Path $DataDirectory 'enrollment-receipt.json'
 if(-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)){return Get-AgentEnrollmentStatus}
 try{$receipt=[IO.File]::ReadAllText($receiptPath)|ConvertFrom-Json -ErrorAction Stop;return Get-AgentEnrollmentStatus -Receipt $receipt}catch{throw 'Agent enrollment receipt is invalid; TightVNC setup was skipped.'}
}
function Get-AgentVncSetupDecision {
 param([psobject]$EnrollmentStatus)
 if($EnrollmentStatus -and $EnrollmentStatus.State -eq 'enrolled'){return 'Configure'}
 return 'SkipPendingEnrollment'
}
function New-PrivateInstallSecurity {
 param([Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$Directory,[switch]$ReadOnly,[switch]$AllowDelete)
 if($Directory -and $AllowDelete){throw 'Delete-only enrollment access applies to files, not directories.'}
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
   # EnrollmentWorker removes its one-time bearer ticket after saving the receipt; it needs file Delete, not write access.
   elseif($AllowDelete){[Security.AccessControl.FileSystemRights]::Read -bor [Security.AccessControl.FileSystemRights]::Delete}
   elseif($Directory){[Security.AccessControl.FileSystemRights]::Modify}
   else{[Security.AccessControl.FileSystemRights]::Read}
  }else{[Security.AccessControl.FileSystemRights]::FullControl}
  $rule=if($Directory){[Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)}else{[Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,[Security.AccessControl.AccessControlType]::Allow)}
  $acl.AddAccessRule($rule)
 }
 return $acl
}
function Set-PrivateInstallAcl {
 param([string]$Path,[Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$Directory,[switch]$ReadOnly,[switch]$AllowDelete)
 $acl=New-PrivateInstallSecurity $ServiceSid -Directory:$Directory -ReadOnly:$ReadOnly -AllowDelete:$AllowDelete
 Set-Acl -LiteralPath $Path -AclObject $acl
}
function Initialize-PrivateFileAclNativeType {
 if(-not ('KamenRaider.Install.NativePrivateFileAclV1' -as [type])){
  Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace KamenRaider.Install {
 public static class NativePrivateFileAclV1 {
  [DllImport("advapi32.dll", EntryPoint="SetNamedSecurityInfoW", CharSet=CharSet.Unicode, ExactSpelling=true)]
  private static extern UInt32 SetNamedSecurityInfo(String name, UInt32 objectType, UInt32 securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
  public static void SetProtectedDacl(String path, byte[] daclBytes) {
   IntPtr dacl=Marshal.AllocHGlobal(daclBytes.Length);
   try {
    Marshal.Copy(daclBytes,0,dacl,daclBytes.Length);
    // SE_FILE_OBJECT, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION
    UInt32 result=SetNamedSecurityInfo(path,1,0x80000004,IntPtr.Zero,IntPtr.Zero,dacl,IntPtr.Zero);
    if(result!=0)throw new Win32Exception((Int32)result);
   } finally { Marshal.FreeHGlobal(dacl); }
  }
 }
}
'@
 }
}
function Get-PrivateInstallAccessRulesForSid {
 param([Parameter(Mandatory)][Security.AccessControl.FileSystemSecurity]$Acl,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid)
 return @($Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object {$_.IdentityReference.Value -eq $ServiceSid.Value})
}
function Set-PrivateEnrollmentTicketAcl {
 param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid)
 $file=Get-Item -LiteralPath $Path -Force -ErrorAction Stop
 if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-InstallFileLinkCount $Path) -ne 1){throw 'Enrollment ticket must be a regular single-link file.'}
 $trusted=Get-TrustedInstallSids @($ServiceSid)
 if(-not (Test-TrustedInstallAcl (Get-Acl -LiteralPath $Path) $trusted -Private)){throw 'Enrollment ticket has untrusted access or ownership.'}
 $security=New-PrivateInstallSecurity $ServiceSid -AllowDelete
 $descriptor=[Security.AccessControl.RawSecurityDescriptor]::new($security.GetSecurityDescriptorBinaryForm(),0)
 $daclBytes=[byte[]]::new($descriptor.DiscretionaryAcl.BinaryLength)
 $descriptor.DiscretionaryAcl.GetBinaryForm($daclBytes,0)
 Initialize-PrivateFileAclNativeType
 [KamenRaider.Install.NativePrivateFileAclV1]::SetProtectedDacl($Path,$daclBytes)
 $acl=Get-Acl -LiteralPath $Path
 $rules=Get-PrivateInstallAccessRulesForSid -Acl $acl -ServiceSid $ServiceSid
 $forbidden=[Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
 if(-not $acl.AreAccessRulesProtected -or -not (Test-TrustedInstallAcl $acl $trusted -Private) -or $rules.Count -ne 1 -or
    ($rules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Read) -ne [Security.AccessControl.FileSystemRights]::Read -or
    ($rules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Delete) -ne [Security.AccessControl.FileSystemRights]::Delete -or
    ($rules[0].FileSystemRights -band $forbidden)){throw 'Enrollment ticket ACL did not grant only the required Agent access.'}
}
function Publish-PrivateEnrollmentTicket {
 param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Ticket,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$Replace)
 if($Ticket -notmatch '^[A-Za-z0-9_-]{43}$'){throw 'Enrollment ticket is invalid.'}
 $directoryItem=Get-Item -LiteralPath $Directory -Force
 if(-not $directoryItem.PSIsContainer -or ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Enrollment ticket directory must be a real directory.'}
 if(-not (Test-TrustedInstallAcl (Get-Acl -LiteralPath $Directory) (Get-TrustedInstallSids @($ServiceSid)) -RequireModifySid $ServiceSid)){throw 'Enrollment ticket directory does not have the required private Agent ACL.'}
 $path=Join-Path $Directory 'enrollment-ticket.txt'
 $existing=Test-Path -LiteralPath $path
 if($existing -and -not $Replace){throw 'Enrollment ticket file already exists.'}
 if($existing){
  $old=Get-Item -LiteralPath $path -Force
  if($old.PSIsContainer -or ($old.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-InstallFileLinkCount $path) -ne 1){throw 'Existing enrollment ticket is not a regular single-link file.'}
 }
 $temporary=Join-Path $Directory ('.enrollment-ticket-'+[Guid]::NewGuid().ToString('N')+'.tmp')
 $backup=$temporary+'.backup'
 $file=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 $file.Dispose()
 try {
  [IO.File]::WriteAllText($temporary,$Ticket,[Text.UTF8Encoding]::new($false))
  $ticketFile=Get-Item -LiteralPath $temporary -Force
  if($ticketFile.PSIsContainer -or ($ticketFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-InstallFileLinkCount $temporary) -ne 1){throw 'Enrollment ticket must be a regular single-link file.'}
  Set-PrivateEnrollmentTicketAcl -Path $temporary -ServiceSid $ServiceSid
  if($existing){[IO.File]::Replace($temporary,$path,$backup)}else{[IO.File]::Move($temporary,$path)}
 } catch {
  throw
 } finally {
  if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}
  if(Test-Path -LiteralPath $backup){Remove-Item -LiteralPath $backup -Force}
 }
 return $path
}
function Invoke-AgentEnrollmentRepair {
 param(
  [Parameter(Mandatory)][string]$ServiceName,
  [Parameter(Mandatory)][string]$InstallDirectory,
  [Parameter(Mandatory)][string]$DataDirectory,
  [Parameter(Mandatory)][string]$ControllerUrl,
  [Parameter(Mandatory)][string]$EnrollmentTicket,
  [Security.Principal.SecurityIdentifier]$ServiceSid,
  [string]$InstallReturnUrl
 )
 $serviceInfo=@(Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $ServiceName) -ErrorAction Stop)
 if($serviceInfo.Count -ne 1){throw 'Existing Agent service record is missing or ambiguous.'}
 $expectedAccount="NT SERVICE\$ServiceName"
 $expectedBinaryPath=Get-ServiceBinaryPath -Executable (Join-Path $InstallDirectory 'Agent.exe') -ContentRoot $InstallDirectory
 if(-not [string]::Equals([string]$serviceInfo[0].PathName,$expectedBinaryPath,[StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$serviceInfo[0].StartName,$expectedAccount,[StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([string]$serviceInfo[0].StartMode,'Auto',[StringComparison]::OrdinalIgnoreCase)){
  throw 'Existing service identity, binary path or automatic startup does not match the Agent installation.'
 }
 Assert-TrustedInstallParent $InstallDirectory -ServiceSid $ServiceSid
 Assert-TrustedPublishTree $InstallDirectory
 $configurationFile=Join-Path $InstallDirectory 'appsettings.json'
 Assert-TrustedInstallFile $configurationFile -ServiceSid $ServiceSid -Private
 try{$configuration=[IO.File]::ReadAllText($configurationFile)|ConvertFrom-Json -ErrorAction Stop}catch{throw 'Existing Agent configuration is invalid.'}
 if(-not [string]::Equals([string]$configuration.ControllerUrl,$ControllerUrl,[StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals([IO.Path]::GetFullPath([string]$configuration.DataDirectory),[IO.Path]::GetFullPath($DataDirectory),[StringComparison]::OrdinalIgnoreCase)){
  throw 'Existing Agent configuration points to a different Controller or data directory; refusing to overwrite it.'
 }
 $dataItem=Get-Item -LiteralPath $DataDirectory -Force -ErrorAction Stop
 if(-not $dataItem.PSIsContainer -or ($dataItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Existing Agent data directory is not a real directory.'}
 if(-not (Test-TrustedInstallAcl (Get-Acl -LiteralPath $DataDirectory) (Get-TrustedInstallSids @($ServiceSid)) -IncludeInheritedChildRules -Private -RequireModifySid $ServiceSid)){throw 'Existing Agent data directory ACL is not safe for enrollment repair.'}
 Assert-TrustedPublishTree $DataDirectory -ServiceSid $ServiceSid -Private

 $receiptPath=Join-Path $DataDirectory 'enrollment-receipt.json'
 $receiptStatus=$null
 $ticketRenewed=$false
 if(Test-Path -LiteralPath $receiptPath -PathType Leaf){
  try{$receiptStatus=Get-AgentEnrollmentStatus -Receipt ([IO.File]::ReadAllText($receiptPath)|ConvertFrom-Json -ErrorAction Stop)}catch{throw 'Existing enrollment receipt is invalid; refusing to replace the installation.'}
 }else{
  Publish-PrivateEnrollmentTicket -Directory $DataDirectory -Ticket $EnrollmentTicket -ServiceSid $ServiceSid -Replace | Out-Null
  $ticketRenewed=$true
 }

 $ticketPath=Join-Path $DataDirectory 'enrollment-ticket.txt'
 if(Test-Path -LiteralPath $ticketPath -PathType Leaf){Set-PrivateEnrollmentTicketAcl -Path $ticketPath -ServiceSid $ServiceSid}

 $service=Get-Service -Name $ServiceName -ErrorAction Stop
 if($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Stopped){Start-Service -Name $ServiceName -ErrorAction Stop}
 elseif($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Paused){Resume-Service -Name $ServiceName -ErrorAction Stop}
 $startup= [Diagnostics.Stopwatch]::StartNew();$startAttempted=$service.Status -in @([ServiceProcess.ServiceControllerStatus]::Stopped,[ServiceProcess.ServiceControllerStatus]::Paused)
 do{
  $service=Get-Service -Name $ServiceName -ErrorAction Stop
  if($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running){break}
  if($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Stopped -and -not $startAttempted){Start-Service -Name $ServiceName -ErrorAction Stop;$startAttempted=$true}
  elseif($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Paused){Resume-Service -Name $ServiceName -ErrorAction Stop}
  Start-Sleep -Milliseconds 250
 }while($startup.Elapsed.TotalSeconds -lt 30)
 if($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Running){throw "Existing Agent service did not reach Running state (state=$($service.Status))."}

 $timer=[Diagnostics.Stopwatch]::StartNew()
 while($timer.Elapsed.TotalSeconds -lt 90 -and -not $receiptStatus){
  $candidateStatus=$null
  if(Test-Path -LiteralPath $receiptPath -PathType Leaf){try{$candidateStatus=Get-AgentEnrollmentStatus -Receipt ([IO.File]::ReadAllText($receiptPath)|ConvertFrom-Json -ErrorAction Stop)}catch{}}
  $service=Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
  if(-not $service){throw 'Existing Agent service disappeared during enrollment repair.'}
  $waitState=Get-AgentEnrollmentWaitState -ServiceStatus $service.Status.ToString() -ReceiptStatus $candidateStatus
  if($waitState -eq 'unavailable'){
   Write-Warning (Get-AgentEnrollmentRecoveryInstruction -ServiceStatus $service.Status.ToString())
   throw "Existing Agent service entered '$($service.Status)' during enrollment repair."
  }
  if($waitState -eq 'enrolled'){$receiptStatus=$candidateStatus;break}
  $remaining=90-$timer.Elapsed.TotalSeconds;if($remaining -gt 0){Start-Sleep -Milliseconds ([int][Math]::Min(2000,[Math]::Ceiling($remaining*1000)))}
 }
 if($receiptStatus){Write-Output $receiptStatus.Message;Write-Output 'The machine still requires administrator configuration and approval before it can be rented.'}
 else{Write-Warning (Get-AgentEnrollmentStatus).Message}
 if($InstallReturnUrl){try{Start-Process -FilePath $InstallReturnUrl}catch{Write-Warning 'Enrollment repair completed, but the admin page could not be opened automatically.'}}
 if($ticketRenewed){Write-Output 'Existing Agent installation verified; enrollment ticket renewed and service retrying. Agent binaries and configuration were not changed.'}
 else{Write-Output 'Existing Agent installation verified; enrollment receipt is already present. Agent binaries and configuration were not changed.'}
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
 $secure=if($ReadOnly){Test-TrustedInstallAcl $actual $trusted -IncludeInheritedChildRules}else{Test-TrustedInstallAcl $actual $trusted -IncludeInheritedChildRules -Private -RequireModifySid $ServiceSid}
 if(-not $actual.AreAccessRulesProtected -or -not $secure){throw 'Private directory was already present with unsafe access, ownership or service rights.'}
 return $item
}
function Initialize-PrivateDataDirectory {
 param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$ServiceSid,[switch]$AllowEmptyIdentityDirectory)
 if($AllowEmptyIdentityDirectory){Assert-AgentDataDirectoryReady -Path $Path -ServiceSid $ServiceSid -AllowEmptyIdentityDirectory}else{Assert-EmptyInstallDirectory $Path}
 $created=$false
 if(Test-Path -LiteralPath $Path){
  $existingAcl=Get-Acl -LiteralPath $Path
  $trusted=Get-TrustedInstallSids @($ServiceSid)
   if(-not $existingAcl.AreAccessRulesProtected -or -not (Test-TrustedInstallAcl $existingAcl $trusted -IncludeInheritedChildRules -Private -RequireModifySid $ServiceSid)){throw 'Existing data directory has unsafe access, ownership or missing service write access.'}
 }else{
  New-PrivateInstallDirectory $Path $ServiceSid | Out-Null
  $created=$true
 }
 if($AllowEmptyIdentityDirectory){Assert-AgentDataDirectoryReady -Path $Path -ServiceSid $ServiceSid -AllowEmptyIdentityDirectory}else{Assert-EmptyInstallDirectory $Path}
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
