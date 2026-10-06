$ErrorActionPreference='Stop'
& (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'server\start-server.ps1')
