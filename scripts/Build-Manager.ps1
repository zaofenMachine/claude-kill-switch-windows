#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'runtime\manager-app'}
$project=Join-Path $root 'app\ClaudeSafe.Manager\ClaudeSafe.Manager.csproj'
& dotnet restore $project --locked-mode
if($LASTEXITCODE -ne 0){throw 'Locked dependency restore failed.'}
& dotnet publish $project -c Release --no-self-contained --no-restore -o $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Manager build failed.'}
Write-Host 'Manager built. Requires the .NET 10 Desktop Runtime. The proxy core and private configuration are provisioned separately.'
