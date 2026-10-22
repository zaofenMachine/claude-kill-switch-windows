#Requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
foreach($name in @('private','runtime','reports','backups')) {
 $directory=Join-Path $root $name
 New-Item -ItemType Directory -Path $directory -Force|Out-Null
 $acl=Get-Acl -LiteralPath $directory
 $acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18','S-1-5-32-544')) {
  $identity=New-Object Security.Principal.SecurityIdentifier($sid)
  $rule=New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
  $acl.AddAccessRule($rule)
 }
 Set-Acl -LiteralPath $directory -AclObject $acl
}
foreach($name in @('approved-nodes','targets')) {
 $target=Join-Path $root ('private\'+$name+'.json')
 if(!(Test-Path -LiteralPath $target)){Copy-Item -LiteralPath (Join-Path $root ('templates\'+$name+'.example.json')) -Destination $target}
}
Write-Host 'Local directories initialized. No network, firewall, registry or proxy settings were changed.'
