#Requires -Version 5.1
param([switch]$TestPage)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$exe=Join-Path $root 'runtime\browser-handler\ClaudeSafeBrowser.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Build the browser launcher first. See docs/deployment.md.'}
$arguments=if($TestPage){@('--url','https://www.cloudflare.com/cdn-cgi/trace')}else{@('--url','https://claude.ai/')}
# Interactive launch explicitly requested by the user. Never dispatch via a default browser.
$child=Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
$null=$child.Handle
$child.WaitForExit()
if($null -eq $child.ExitCode -or $child.ExitCode -ne 0){throw 'Protected browser launcher failed.'}
