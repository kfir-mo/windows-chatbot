# Run ONCE on a PC with internet access, before moving this folder to a closed network.
# Copies every NuGet package that building, testing and publishing Chaty needs into development\packages
# and writes development\nuget.offline.config. On the closed network, publish-local.ps1 -Offline and the test
# commands then restore from that folder only (see development/AI-INSTRUCTIONS.md, "Building without internet").
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet).Source }
$needed = @{}
function Collect {
    foreach ($assets in Get-ChildItem -Path (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -Filter project.assets.json) {
        $json = Get-Content $assets.FullName -Raw | ConvertFrom-Json
        foreach ($library in $json.libraries.PSObject.Properties) {
            if ($library.Value.type -eq 'package') { $needed[$library.Name.ToLowerInvariant()] = $true }
        }
        # Runtime packs for the self-contained publish are "download dependencies", not libraries.
        foreach ($framework in $json.project.frameworks.PSObject.Properties) {
            foreach ($download in @($framework.Value.downloadDependencies)) {
                if ($download) { $needed[("{0}/{1}" -f $download.name, ($download.version -replace '[\[\]\s]', '' -split ',')[0]).ToLowerInvariant()] = $true }
            }
        }
    }
}
# Collect after each restore: restoring Chat.Smoke rewrites Chat.Local's assets file without the runtime packs.
foreach ($p in @(@('tests\Knowledge.Tests\Knowledge.Tests.csproj'), @('tests\Chat.Smoke\Chat.Smoke.csproj'), @('src\Chat.Local\Chat.Local.csproj', '-r', 'win-x64'))) {
    & $dotnet restore (Join-Path $root $p[0]) @($p | Select-Object -Skip 1) | Out-Null
    if ($LASTEXITCODE) { throw "Restore failed: $($p[0])" }
    Collect
}
$cache = ((& $dotnet nuget locals global-packages -l) -replace '^global-packages:\s*', '').Trim()
$target = Join-Path $root 'packages'
New-Item -ItemType Directory -Force $target | Out-Null
foreach ($package in $needed.Keys) {
    $name, $version = $package -split '/'
    $file = Get-ChildItem -LiteralPath (Join-Path $cache "$name\$version") -Filter '*.nupkg' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$file) { throw "Package not in the local cache: $package" }
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}
@'
<?xml version="1.0" encoding="utf-8"?>
<!-- Restore only from the bundled packages folder (closed network). Created by scripts\prepare-offline.ps1. -->
<configuration>
  <packageSources>
    <clear />
    <add key="chaty-offline" value="packages" />
  </packageSources>
</configuration>
'@ | Set-Content -LiteralPath (Join-Path $root 'nuget.offline.config') -Encoding utf8
$size = (Get-ChildItem $target | Measure-Object Length -Sum).Sum / 1MB
Write-Output ("Offline packages: {0} packages, {1:N0} MB in {2}" -f $needed.Count, $size, $target)
