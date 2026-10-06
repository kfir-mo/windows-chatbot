param([switch]$FrameworkDependentClient)
& (Join-Path $PSScriptRoot 'publish-all.ps1') -FrameworkDependentClient:$FrameworkDependentClient
