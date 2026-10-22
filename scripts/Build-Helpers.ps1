#Requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!(Test-Path -LiteralPath (Join-Path $root 'private'))){throw 'Run Initialize-Local.ps1 first.'}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(!(Test-Path -LiteralPath $compiler)){throw 'The .NET Framework C# compiler is not available.'}
foreach($name in @('browser-handler','probe')){New-Item -ItemType Directory -Path (Join-Path $root ('runtime\'+$name)) -Force|Out-Null}
& $compiler /nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll ('/out:'+(Join-Path $root 'runtime\browser-handler\ClaudeSafeBrowser.exe')) (Join-Path $root 'src\ClaudeSafeBrowser.cs')
if($LASTEXITCODE -ne 0){throw 'Browser launcher compilation failed.'}
& $compiler /nologo /target:exe /optimize+ ('/out:'+(Join-Path $root 'runtime\probe\ProtectedProbe.exe')) (Join-Path $root 'src\NetworkProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Network probe compilation failed.'}
Copy-Item -LiteralPath (Join-Path $root 'runtime\probe\ProtectedProbe.exe') -Destination (Join-Path $root 'runtime\probe\ControlProbe.exe') -Force
Write-Host 'Helpers compiled locally. No browser launched and no rules installed.'
