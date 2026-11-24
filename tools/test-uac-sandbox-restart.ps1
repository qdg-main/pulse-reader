#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$guestInputs = 'C:\Users\WDAGUtilityAccount\Desktop\Inputs'
$guestReports = 'C:\Users\WDAGUtilityAccount\Desktop\Reports'
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or $env:USERPROFILE -ne 'C:\Users\WDAGUtilityAccount' -or
    $PSScriptRoot -ne $guestInputs -or -not (Test-Path -LiteralPath $guestReports -PathType Container)) {
    throw 'UAC restart probe is restricted to its disposable Sandbox mappings.'
}
$probeRoot = 'C:\PulseReaderUacQualification'
$probeMarker = Join-Path $probeRoot 'restart.marker'
$policyPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
function Write-ProbeResult($Value) {
    [IO.File]::WriteAllText((Join-Path $guestReports 'uac-restart-result.json'), ($Value | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
}
try {
    if (-not (Test-Path -LiteralPath $probeRoot)) {
        [IO.Directory]::CreateDirectory($probeRoot) | Out-Null
        $beforeBoot = (Get-CimInstance -ClassName Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('O')
        [IO.File]::WriteAllText($probeMarker, $beforeBoot)
        $before = Get-ItemProperty -LiteralPath $policyPath
        if ($before.EnableLUA -ne 0) { throw 'Fresh probe guest did not have the expected disabled UAC baseline.' }
        # This raises protection only in the disposable guest. Never change
        # secure-desktop behaviour, signature checks or any host policy.
        Set-ItemProperty -LiteralPath $policyPath -Name EnableLUA -Value 1
        $resume = 'powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -WindowStyle Hidden -File "' + $PSCommandPath + '"'
        $runOncePath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
        if (-not (Test-Path -LiteralPath $runOncePath)) { New-Item -Path $runOncePath -Force | Out-Null }
        New-ItemProperty -Path $runOncePath -Name PulseReaderUacProbe -Value $resume -PropertyType String -Force | Out-Null
        Write-ProbeResult @{ Stage='Restart requested inside disposable guest'; BeforeEnableLUA=$before.EnableLUA; Utc=[DateTime]::UtcNow.ToString('O') }
        & shutdown.exe /r /t 3
        if ($LASTEXITCODE -ne 0) { throw 'Guest restart request failed.' }
        exit 0
    }
    if (-not (Test-Path -LiteralPath $probeMarker -PathType Leaf) -or
        ((Get-Item -LiteralPath $probeRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ((Get-Item -LiteralPath $probeMarker).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Invalid probe marker or linked root.' }
    $after = Get-ItemProperty -LiteralPath $policyPath
    $afterBoot = (Get-CimInstance -ClassName Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('O')
    $restarted = [IO.File]::ReadAllText($probeMarker) -ne $afterBoot
    $administrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-ProbeResult @{ Stage='Observed after guest restart'; Passed=($after.EnableLUA -eq 1 -and $restarted); EnableLUA=$after.EnableLUA
        BootChanged=$restarted; AfterBootUtc=$afterBoot
        PromptOnSecureDesktop=$after.PromptOnSecureDesktop; ConsentPromptBehaviorAdmin=$after.ConsentPromptBehaviorAdmin
        CurrentTokenAdministrator=$administrator; MarkerPreserved=$true; Utc=[DateTime]::UtcNow.ToString('O') }
    if ($after.EnableLUA -ne 1 -or -not $restarted) { exit 1 }
}
catch {
    Write-ProbeResult @{ Passed=$false; Error=$_.Exception.ToString(); Utc=[DateTime]::UtcNow.ToString('O') }
    exit 1
}
