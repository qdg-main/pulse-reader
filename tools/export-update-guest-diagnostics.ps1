#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or $env:USERPROFILE -ne 'C:\Users\WDAGUtilityAccount' -or
    -not (Test-Path -LiteralPath 'C:\PulseReaderUpdateQualification\guest-only.marker' -PathType Leaf)) {
    throw 'This diagnostic export is restricted to the disposable update guest.'
}
$reports = 'C:\Users\WDAGUtilityAccount\Desktop\Reports'
if (-not (Test-Path -LiteralPath $reports -PathType Container)) { throw 'The guest Reports mapping is missing.' }
$policy = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$events = @()
$credentialEvents = @()
try {
    $applicationErrors = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = @(1000, 1026); StartTime = (Get-Date).AddHours(-2) } -ErrorAction Stop)
    $events = @($applicationErrors | Where-Object Message -Match 'PulseReader' | Select-Object Id, ProviderName, TimeCreated, Message)
    $credentialEvents = @($applicationErrors | Where-Object Message -Match 'CredentialUI|CredDialogHost|consent\.exe|0xc000027b' | Select-Object Id, ProviderName, TimeCreated, Message)
}
catch {
    if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { throw }
}
$desktopRoot = 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App'
$runtimes = @()
if (Test-Path -LiteralPath $desktopRoot -PathType Container) { $runtimes = @(Get-ChildItem -LiteralPath $desktopRoot -Directory | Select-Object -ExpandProperty Name) }
$record = [ordered]@{
    GuestOnly = $true; Utc = [DateTime]::UtcNow.ToString('O'); DesktopRuntimes = @($runtimes)
    EnableLUA = $policy.EnableLUA; ConsentPromptBehaviorUser = $policy.ConsentPromptBehaviorUser
    ConsentPromptBehaviorAdmin = $policy.ConsentPromptBehaviorAdmin; PromptOnSecureDesktop = $policy.PromptOnSecureDesktop
    PulseReaderCrashEvents = @($events); PulseReaderCrashCount = $events.Count
    CredentialUiEvents = @($credentialEvents)
}
[IO.File]::WriteAllText((Join-Path $reports 'guest-diagnostics.json'), ($record | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
if ($events.Count -ne 0) { throw 'A Pulse Reader application crash event was recorded; inspect guest-diagnostics.json.' }
