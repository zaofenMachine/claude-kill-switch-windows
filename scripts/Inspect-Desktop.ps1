#Requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!(Test-Path -LiteralPath (Join-Path $root 'private'))){throw 'Run Initialize-Local.ps1 first.'}
$packages=@(Get-AppxPackage -Name Claude)
if($packages.Count -ne 1){throw 'Expected one installed Claude package. Inspect installation manually.'}
$package=$packages[0]
$files=@(Get-ChildItem -LiteralPath $package.InstallLocation -Recurse -Filter '*.exe' -File)
if(!$files.Count){throw 'No executable files found.'}
$inventory=[ordered]@{utc=[DateTime]::UtcNow.ToString('o');packageFamily=$package.PackageFamilyName;version=$package.Version.ToString();executables=@($files|ForEach-Object{[ordered]@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})}
$inventory|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $root 'private\desktop-inventory.json') -Encoding utf8
Write-Host ('Inventory saved locally: '+$files.Count+' executable paths. Review before adding to private/targets.json; no policy changed.')
