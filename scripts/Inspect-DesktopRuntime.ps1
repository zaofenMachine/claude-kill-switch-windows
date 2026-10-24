#Requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!(Test-Path -LiteralPath (Join-Path $root 'private'))){throw 'Initialize the local workspace first.'}
if(-not ('ClaudeRuntimeIdentity' -as [type])) {
 Add-Type @'
using System;using System.Text;using System.Runtime.InteropServices;using System.ComponentModel;
public static class ClaudeRuntimeIdentity {
 [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(IntPtr p,uint flags,StringBuilder b,ref uint len);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern int GetPackageFamilyName(IntPtr p,ref uint len,StringBuilder b);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr p);
 public static string Path(int pid,uint flags){var h=OpenProcess(0x1000,false,pid);if(h==IntPtr.Zero)throw new Win32Exception();try{uint n=32768;var b=new StringBuilder((int)n);if(!QueryFullProcessImageName(h,flags,b,ref n))throw new Win32Exception();return b.ToString();}finally{CloseHandle(h);}}
 public static string Family(int pid){var h=OpenProcess(0x1000,false,pid);if(h==IntPtr.Zero)throw new Win32Exception();try{uint n=512;var b=new StringBuilder((int)n);int code=GetPackageFamilyName(h,ref n,b);return code==0?b.ToString():"error:"+code;}finally{CloseHandle(h);}}
}
'@
}
$fileInfo=@{}
$rows=@(Get-CimInstance Win32_Process -Filter "name='claude.exe'"|ForEach-Object {
 try{
  $actual=[ClaudeRuntimeIdentity]::Path($_.ProcessId,0)
  if(!$fileInfo.ContainsKey($actual)){$signature=Get-AuthenticodeSignature -LiteralPath $actual;$fileInfo[$actual]=@{status=$signature.Status.ToString();publisher=$signature.SignerCertificate.Subject;sha256=(Get-FileHash -LiteralPath $actual).Hash}}
  $file=$fileInfo[$actual]
  [pscustomobject]@{pid=$_.ProcessId;parentId=$_.ParentProcessId;reportedPath=$_.ExecutablePath;actualPath=$actual;nativePath=[ClaudeRuntimeIdentity]::Path($_.ProcessId,1);packageIdentity=[ClaudeRuntimeIdentity]::Family($_.ProcessId);signature=$file.status;publisher=$file.publisher;sha256=$file.sha256}
 }catch{[pscustomobject]@{pid=$_.ProcessId;inspectionError=$true}}
})
ConvertTo-Json -InputObject $rows -Depth 5|Set-Content -LiteralPath (Join-Path $root 'private\desktop-runtime.json') -Encoding utf8
Write-Host ('Saved '+$rows.Count+' runtime records locally. No executable launched and no policy changed.')
