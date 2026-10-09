param([switch]$FrameworkDependent, [switch]$Offline)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$dotnet=Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if(!(Test-Path -LiteralPath $dotnet)){$dotnet=(Get-Command dotnet).Source}
$output=Split-Path -Parent $root
$selfContained=if($FrameworkDependent){'false'}else{'true'}
# A running copy locks the exe; ask it to close first.
Get-Process Chaty, Chat.Local -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$project=Join-Path $root 'src\Chat.Local\Chat.Local.csproj'
$restore=@()
if($Offline){
  # Closed network: restore only from development\packages (created by scripts\prepare-offline.ps1).
  $config=Join-Path $root 'nuget.offline.config'
  if(!(Test-Path -LiteralPath $config)){throw 'Run scripts\prepare-offline.ps1 on a PC with internet first.'}
  & $dotnet restore $project -r win-x64 --configfile $config
  if($LASTEXITCODE){throw 'Offline restore failed'}
  $restore=@('--no-restore')
}
& $dotnet publish $project @restore -c Release -r win-x64 --self-contained $selfContained -p:ExcludeDevelopmentSettings=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $output
if($LASTEXITCODE){throw 'Local prototype publish failed'}
# The app was renamed from Chat.Local to Chaty; remove the old executable so only one version exists.
$legacy=Join-Path $output 'Chat.Local.exe'
if(Test-Path -LiteralPath $legacy){Remove-Item -LiteralPath $legacy -Force}
Write-Output "Chaty: $output\Chaty.exe"
if($FrameworkDependent){Write-Output 'Requires .NET 8 Windows Desktop Runtime x64 on the target PC.'}

# The AI configuration is %APPDATA%\config.json on each PC; nothing else is placed beside the exe.
