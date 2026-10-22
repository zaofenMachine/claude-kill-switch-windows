[CmdletBinding()]
param([ValidateSet('Register','Check','Remove')][string]$Mode='Check',[switch]$ValidateOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$name='Claude Safe Firefox'
$progId='ClaudeKillSwitch.SafeBrowserUrl'
$client='Software\Clients\StartMenuInternet\ClaudeSafeFirefox'
$classes='Software\Classes\'+$progId
$capability=$client+'\Capabilities'
$handler=Join-Path $root 'runtime\browser-handler\ClaudeSafeBrowser.exe'
$firefox=Join-Path $root 'runtime\firefox\core\firefox.exe'
$command='"'+$handler+'" --url "%1"'
$stateFile=Join-Path $root 'reports\browser-registration.local.json'
$expected=@(
 @{path=$classes;name='';value=$name},
 @{path=$classes;name='URL Protocol';value=''},
 @{path=$classes+'\DefaultIcon';name='';value=('"'+$firefox+'",0')},
 @{path=$classes+'\shell\open\command';name='';value=$command},
 @{path=$classes+'\Application';name='ApplicationName';value=$name},
 @{path=$classes+'\Application';name='ApplicationDescription';value='Dedicated Firefox with a fixed Claude proxy'},
 @{path=$client;name='';value=$name},
 @{path=$client+'\DefaultIcon';name='';value=('"'+$firefox+'",0')},
 @{path=$client+'\shell\open\command';name='';value=('"'+$handler+'"')},
 @{path=$capability;name='ApplicationName';value=$name},
 @{path=$capability;name='ApplicationDescription';value='Open HTTP and HTTPS only in the protected Firefox installation'},
 @{path=$capability;name='ApplicationIcon';value=('"'+$firefox+'",0')},
 @{path=$capability+'\URLAssociations';name='http';value=$progId},
 @{path=$capability+'\URLAssociations';name='https';value=$progId},
 @{path='Software\RegisteredApplications';name=$name;value=$capability}
)
function Read-Value([string]$path,[string]$name) {
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
 if(!$key){return $null}
 try {$key.GetValue($name,$null)} finally {$key.Dispose()}
}
function Read-Defaults {
 $result=[ordered]@{}
 foreach($scheme in @('http','https')) {
  $result[$scheme]=Read-Value ('Software\Microsoft\Windows\Shell\Associations\UrlAssociations\'+$scheme+'\UserChoice') 'ProgId'
 }
 return $result
}
function Write-Value($entry) {
 $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($entry.path)
 try {$key.SetValue($entry.name,$entry.value,[Microsoft.Win32.RegistryValueKind]::String)} finally {$key.Dispose()}
}
function Read-EffectiveDefaults {
 if(-not ('ClaudeBrowserAssociationQuery' -as [type])){
  Add-Type @'
using System;
using System.Runtime.InteropServices;
[ComImport,Guid("4e530b0a-e611-4c77-a3ac-9031d022281b"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IClaudeReadAssociation {
 [PreserveSig] int QueryCurrentDefault([MarshalAs(UnmanagedType.LPWStr)] string protocol,int associationType,int associationLevel,[MarshalAs(UnmanagedType.LPWStr)] out string progId);
}
public static class ClaudeBrowserAssociationQuery {
 public static string Query(string protocol) {
  object instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("591209c7-767b-42b2-9fba-44ee4615f2c7")));
  try {string value;int hr=((IClaudeReadAssociation)instance).QueryCurrentDefault(protocol,1,1,out value);Marshal.ThrowExceptionForHR(hr);return value;}
  finally {Marshal.ReleaseComObject(instance);}
 }
}
'@
 }
 $result=[ordered]@{}
 foreach($scheme in @('http','https','claude')){
  try{$result[$scheme]=[ClaudeBrowserAssociationQuery]::Query($scheme)}catch{$result[$scheme]=$null}
 }
 return $result
}
function Check-Registration {
 $mismatches=@($expected|Where-Object {(Read-Value $_.path $_.name) -cne $_.value})
 $defaults=Read-Defaults
 $effective=Read-EffectiveDefaults
 $callbackApp=$null
 if($effective.claude){
  $callbackKey=[Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey($effective.claude+'\Application')
  if($callbackKey){try{$callbackApp=$callbackKey.GetValue('AppUserModelId')}finally{$callbackKey.Dispose()}}
 }
 $policy=Join-Path $root 'runtime\firefox\core\distribution\policies.json'
 $hash=if(Test-Path -LiteralPath $policy){(Get-FileHash -LiteralPath $policy -Algorithm SHA256).Hash}else{$null}
 $stored=if(Test-Path -LiteralPath $stateFile){Get-Content -LiteralPath $stateFile -Raw|ConvertFrom-Json}else{$null}
 $handlerMatches=$false
 if($stored -and (Test-Path -LiteralPath $handler)){$handlerMatches=(Get-FileHash -LiteralPath $handler).Hash -eq $stored.handlerSha256}
 [pscustomobject]@{utc=[DateTime]::UtcNow.ToString('o');registered=($mismatches.Count -eq 0);registrationMismatchCount=$mismatches.Count;
  handlerExists=(Test-Path -LiteralPath $handler);handlerMatchesRecordedHash=$handlerMatches;
  protectedPolicyMatches=($hash -eq '29463A5ECB4133A412183D601D507552FD433AC437263D9AD9F9D83717296741');
  defaults=$defaults;effectiveDefaults=$effective;
  bothWebProtocolsSelected=($defaults.http -eq $progId -and $defaults.https -eq $progId -and $effective.http -eq $progId -and $effective.https -eq $progId);
  callbackApplicationUserModelId=$callbackApp;callbackRegistrationMatches=($callbackApp -eq 'Claude_pzs8sxrjxfjjc!Claude');
  browserPath=$firefox;profilePath=(Join-Path $root 'runtime\claude-browser-profile');
  authenticatedLoginTested=$false}
}
if($Mode -eq 'Check'){Check-Registration|ConvertTo-Json -Depth 5;return}
if($Mode -eq 'Remove'){
 $defaults=Read-Defaults
 if($defaults.http -eq $progId -or $defaults.https -eq $progId){throw 'Select another default browser in Windows Settings before removing this registration.'}
 if(!(Test-Path -LiteralPath $stateFile)){throw 'Registration ownership record is missing.'}
 $saved=Get-Content -LiteralPath $stateFile -Raw|ConvertFrom-Json
 if($saved.root -ne $root){throw 'Registration belongs to a different project path.'}
 # Refuse to delete trees if another tool has added or changed registry content.
 $actual=@()
 foreach($base in @($classes,$client)){
  $item=Get-Item -LiteralPath ('HKCU:\'+$base) -ErrorAction SilentlyContinue
  if($item){$actual+=@($item)+@(Get-ChildItem -LiteralPath $item.PSPath -Recurse)}
 }
 foreach($key in $actual){
  $relative=$key.Name.Substring('HKEY_CURRENT_USER\'.Length)
  if(@($expected|Where-Object {$_.path -eq $relative -or $_.path.StartsWith($relative+'\',[StringComparison]::OrdinalIgnoreCase)}).Count -eq 0){throw 'Unknown registration subkey; manual merge required.'}
  foreach($valueName in $key.GetValueNames()){
   $known=@($expected|Where-Object {$_.path -eq $relative -and $_.name -eq $valueName})
   if($known.Count -ne 1 -or $key.GetValue($valueName) -cne $known[0].value){throw 'Registration changed; manual merge required.'}
  }
 }
 $registered=Read-Value 'Software\RegisteredApplications' $name
 if($null -ne $registered -and $registered -ne $capability){throw 'RegisteredApplications changed.'}
 if($ValidateOnly){Write-Host 'Browser registration removal preflight passed. No changes.';return}
 foreach($path in @($classes,$client)){
  # Exact, owned registry paths; no filesystem deletion or generic association changes.
  [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($path,$false)
 }
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\RegisteredApplications',$true)
 if($key){try{$key.DeleteValue($name,$false)}finally{$key.Dispose()}}
 Write-Host 'Owned browser registration removed. Browser, profile and backup files retained.'
 return
}
if($ValidateOnly){throw '-ValidateOnly is supported with -Mode Remove only.'}
if(!(Test-Path -LiteralPath $firefox)){throw 'Dedicated Firefox is missing.'}
$defaultsBefore=Read-Defaults
foreach($entry in $expected){
 $current=Read-Value $entry.path $entry.name
 if($null -ne $current -and $current -cne $entry.value){throw 'Conflicting browser registration; refusing overwrite.'}
}
if(!(Test-Path -LiteralPath $stateFile)){
 foreach($path in @($classes,$client)){
  if(Test-Path -LiteralPath ('HKCU:\'+$path)){throw 'Pre-existing browser registration has no ownership record.'}
 }
 $before=[ordered]@{utc=[DateTime]::UtcNow.ToString('o');root=$root;defaultsBefore=$defaultsBefore;handlerSha256=$null}
 $before|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $stateFile -Encoding utf8
}
$policy=Join-Path $root 'runtime\firefox\core\distribution\policies.json'
if((Get-FileHash -LiteralPath $policy).Hash -ne '29463A5ECB4133A412183D601D507552FD433AC437263D9AD9F9D83717296741'){throw 'Protected Firefox policy changed.'}
New-Item -ItemType Directory -Path (Split-Path $handler -Parent) -Force|Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll ('/out:'+$handler) (Join-Path $root 'src\ClaudeSafeBrowser.cs')
if($LASTEXITCODE -ne 0){throw 'Browser launcher compilation failed.'}
foreach($entry in $expected){Write-Value $entry}
$saved=Get-Content -LiteralPath $stateFile -Raw|ConvertFrom-Json
$saved.handlerSha256=(Get-FileHash -LiteralPath $handler).Hash
$saved|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $stateFile -Encoding utf8
$after=Read-Defaults
if($defaultsBefore.http -ne $after.http -or $defaultsBefore.https -ne $after.https){throw 'Default browser unexpectedly changed during registration.'}
# Notify Explorer about registration changes, without changing UserChoice or its hash.
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices; public static class BrowserAssociationNotice {[DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e,uint f,IntPtr a,IntPtr b);}'
[BrowserAssociationNotice]::SHChangeNotify(0x08000000,0,[IntPtr]::Zero,[IntPtr]::Zero)
Check-Registration|ConvertTo-Json -Depth 5
