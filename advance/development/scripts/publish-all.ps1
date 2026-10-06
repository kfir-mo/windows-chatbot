param([switch]$FrameworkDependentClient)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$dotnet=Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if(!(Test-Path -LiteralPath $dotnet)){$dotnet=(Get-Command dotnet).Source}
$client=Join-Path (Split-Path -Parent $root) 'client'
$serverHome=Join-Path (Split-Path -Parent $root) 'server'
$serverApp=Join-Path $serverHome 'app'
# The server source excludes its app output from subsequent builds.
& $dotnet publish (Join-Path $root 'server\Server.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $serverApp
if($LASTEXITCODE){throw 'Server publish failed'}
$selfContained=if($FrameworkDependentClient){'false'}else{'true'}
& $dotnet publish (Join-Path $root 'src\Chat\Chat.csproj') -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ExcludeDevelopmentSettings=true -p:DebugType=None -p:DebugSymbols=false -o $client
if($LASTEXITCODE){throw 'Client publish failed'}
if(!(Test-Path -LiteralPath (Join-Path $client '.env'))){Copy-Item -LiteralPath (Join-Path $root '.env') -Destination (Join-Path $client '.env')}
if(!(Test-Path -LiteralPath (Join-Path $client 'config.json'))){Copy-Item -LiteralPath (Join-Path $root 'config.json') -Destination (Join-Path $client 'config.json')}
foreach($name in @('README.md','INTERNAL-DEPLOYMENT.md','.env.example','settings.production.example.json')){Copy-Item -LiteralPath (Join-Path $root ('server\'+$name)) -Destination (Join-Path $serverHome $name) -Force}
if(!(Test-Path -LiteralPath (Join-Path $serverHome 'config.json'))){Set-Content -LiteralPath (Join-Path $serverHome 'config.json') -Value '{}' -Encoding utf8}
if(!(Test-Path -LiteralPath (Join-Path $serverHome '.env'))){Copy-Item -LiteralPath (Join-Path $root 'server\.env.example') -Destination (Join-Path $serverHome '.env')}
if(!(Test-Path -LiteralPath (Join-Path $serverHome 'settings.json'))){Copy-Item -LiteralPath (Join-Path $root 'server\settings.production.example.json') -Destination (Join-Path $serverHome 'settings.json')}
Copy-Item -LiteralPath (Join-Path $root 'scripts\start-managed-server.ps1') -Destination (Join-Path $serverHome 'start-server.ps1') -Force
Write-Output "Client: $client"
Write-Output "Server: $serverApp (start with server\start-server.ps1)"
