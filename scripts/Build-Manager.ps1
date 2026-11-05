#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'runtime\manager-app'}
$project=Join-Path $root 'app\ClaudeSafe.Manager\ClaudeSafe.Manager.csproj'
& dotnet restore $project --locked-mode
if($LASTEXITCODE -ne 0){throw 'Locked dependency restore failed.'}
& dotnet publish $project -c Release --self-contained true --no-restore -o $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Manager build failed.'}
if(!(Test-Path -LiteralPath (Join-Path $OutputDirectory 'ClaudeSafe.Manager.pri'))){throw 'WinUI resources are missing from the published application.'}
Write-Host 'WinUI 3 manager built for Windows 10 22H2 / Windows 11 (x64). .NET and Windows App SDK runtimes are included. Copy the complete output directory. The proxy core and private configuration are provisioned separately.'
