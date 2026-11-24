[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$installRoot = Join-Path $env:LOCALAPPDATA 'PulseReader\ContextualPronunciation'
& (Join-Path $PSScriptRoot 'setup-local-text-cleanup.ps1') `
    -Model qwen3-4b-instruct-2507 `
    -InstallRoot $installRoot

Write-Host ''
Write-Host 'Pulse Reader contextual pronunciation is installed for the current user.'
