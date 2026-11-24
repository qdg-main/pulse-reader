#requires -Version 7.4
param([string]$RepositoryRoot = (Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
foreach ($relative in @('tools\build-release.ps1', 'tools\build-dependency-sources.ps1', 'tools\release-verify-payload.ps1')) {
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root $relative), [ref]$null, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "Release script has parser errors: $relative" }
    if ($null -eq $ast.ScriptRequirements.RequiredPSVersion -or $ast.ScriptRequirements.RequiredPSVersion -lt [Version]'7.4') {
        throw "Release script permits a runtime without the required APIs: $relative"
    }
}
$guide = [IO.File]::ReadAllText((Join-Path $root 'docs\releasing.md'))
if (-not $guide.Contains('PowerShell 7.4 or later')) { throw 'Release guide does not match the required PowerShell runtime.' }
$sourceGuide = [IO.File]::ReadAllText((Join-Path $root 'docs\dependency-source-build.md'))
if (-not $sourceGuide.Contains('PowerShell 7.4 or later')) { throw 'Source build guide omits the release-tool runtime prerequisite.' }
$digest = [Security.Cryptography.SHA256]::HashData([byte[]](1, 2, 3))
if ([Convert]::ToHexString($digest).Length -ne 64) { throw 'The current PowerShell host lacks the release hash APIs.' }
Write-Output 'PASS: release script guards, documentation and host APIs agree on PowerShell 7.4 or later.'
