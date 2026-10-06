$ErrorActionPreference='Stop'
$env:WINDOWSCHAT_SERVER_HOME=$PSScriptRoot
$app=Join-Path $PSScriptRoot 'app\WindowsChat.Server.exe'
if(!(Test-Path -LiteralPath $app)){throw 'Run scripts\publish-all.ps1 first.'}
& $app
