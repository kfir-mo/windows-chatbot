param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$edition=Split-Path -Parent $root
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('packages\WindowsChat-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new output directory.'}
if(!(Test-Path -LiteralPath (Join-Path $edition 'client\Chat.exe')) -or !(Test-Path -LiteralPath (Join-Path $edition 'server\app\WindowsChat.Server.exe'))){throw 'Run publish-all.ps1 first.'}
$client=New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'client')
$server=New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'server')
Copy-Item -LiteralPath (Join-Path $edition 'client\Chat.exe') -Destination $client.FullName
Copy-Item -LiteralPath (Join-Path $root 'config.internal.example.json') -Destination (Join-Path $client.FullName 'config.json')
Copy-Item -LiteralPath (Join-Path $root '.env.internal.example') -Destination (Join-Path $client.FullName '.env')
Copy-Item -LiteralPath (Join-Path $edition 'server\app') -Destination (Join-Path $server.FullName 'app') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'server\settings.production.example.json') -Destination (Join-Path $server.FullName 'settings.json')
Copy-Item -LiteralPath (Join-Path $root 'server\.env.example') -Destination (Join-Path $server.FullName '.env')
foreach($name in @('README.md','INTERNAL-DEPLOYMENT.md')){Copy-Item -LiteralPath (Join-Path $root ('server\'+$name)) -Destination (Join-Path $server.FullName $name)}
Set-Content -LiteralPath (Join-Path $server.FullName 'config.json') -Value '{}' -Encoding utf8
Copy-Item -LiteralPath (Join-Path $root 'scripts\start-managed-server.ps1') -Destination (Join-Path $server.FullName 'start-server.ps1')
Write-Output "Prepared credential-free transfer folders: $OutputDirectory"
