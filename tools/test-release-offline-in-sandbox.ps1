[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Run this fixture only in Windows Sandbox with Networking disabled.' }
$inputs = $PSScriptRoot
$reports = 'C:\Users\WDAGUtilityAccount\Desktop\Reports'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\PulseReader'
$dataRoot = Join-Path $env:LOCALAPPDATA 'PulseReader'
$application = $null
$modelHash = '0CFD5E79AAB70A3D8C1A57DC639835110DDB32C9F5FF4FDD1F4DB202EA43BB05'

function Stage([string]$Name) {
    @{ Stage = $Name; Utc = [DateTime]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reports 'stage.json') -Encoding UTF8
}
function Check-Payload([string]$Root, [string]$Prefix) {
    $voiceAttributes = @{}
    $files = @('PulseReader.InputSequenceTests.exe', 'PulseReader.InputSequenceTests.dll', 'PulseReader.InputSequenceTests.deps.json', 'PulseReader.InputSequenceTests.runtimeconfig.json')
    foreach ($name in $files) {
        if (Test-Path -LiteralPath (Join-Path $Root $name)) { throw 'A test runner would overwrite a real payload file.' }
        Copy-Item -LiteralPath (Join-Path $inputs ('test-runner\' + $name)) -Destination $Root
    }
    try {
        foreach ($voice in Get-ChildItem -LiteralPath (Join-Path $Root 'voices') -Filter '*.npy' -File) {
            $voiceAttributes[$voice.FullName] = $voice.Attributes
            [IO.File]::SetAttributes($voice.FullName, ($voice.Attributes -bor [IO.FileAttributes]::ReadOnly))
        }
        $check = Start-Process -FilePath (Join-Path $Root $files[0]) -ArgumentList '--release-model-smoke' -WorkingDirectory $Root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $reports ($Prefix + '.stdout.txt')) -RedirectStandardError (Join-Path $reports ($Prefix + '.stderr.txt')) -PassThru
        $null = $check.Handle
        if (-not $check.WaitForExit(120000)) { Stop-Process -Id $check.Id; throw 'Offline synthesis timed out.' }
        $check.Refresh()
        if ($null -eq $check.ExitCode -or $check.ExitCode -ne 0) { throw "Offline synthesis failed: $($check.ExitCode)" }
    }
    finally {
        foreach ($path in $voiceAttributes.Keys) { [IO.File]::SetAttributes($path, $voiceAttributes[$path]) }
        foreach ($name in $files) { Remove-Item -LiteralPath (Join-Path $Root $name) }
    }
}
try {
    Stage 'Verifying fresh network-disabled guest'
    if (Test-Path -LiteralPath $installRoot) { throw 'Offline fixture requires a fresh guest profile.' }
    $nonLoopbackUp = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object {
        $_.NetworkInterfaceType -ne [Net.NetworkInformation.NetworkInterfaceType]::Loopback -and
        $_.OperationalStatus -eq [Net.NetworkInformation.OperationalStatus]::Up
    })
    if ($nonLoopbackUp.Count -ne 0) { throw 'Offline fixture found an active non-loopback network interface.' }
    $sourceModel = Join-Path $inputs 'kokoro.onnx'
    if ((Get-FileHash -LiteralPath $sourceModel -Algorithm SHA256).Hash -ne $modelHash) { throw 'Offline fixture model does not match the official pin.' }
    Stage 'Installing with network disabled and reusing verified cache'
    $installer = Start-Process -FilePath (Join-Path $inputs 'PulseReader-1.0.0-win-x64-Setup.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/ACCEPTDISTRIBUTIONTERMS=yes', ('/LOG=' + (Join-Path $reports 'offline-install.log'))) -WindowStyle Hidden -PassThru
    $null = $installer.Handle
    if (-not $installer.WaitForExit(120000)) { Stop-Process -Id $installer.Id; throw 'Offline installation timed out.' }
    $installer.Refresh()
    if ($null -eq $installer.ExitCode -or $installer.ExitCode -ne 0) { throw "Offline installer failed: $($installer.ExitCode)" }
    New-Item -ItemType Directory -Path (Join-Path $dataRoot 'Models') | Out-Null
    Copy-Item -LiteralPath $sourceModel -Destination (Join-Path $dataRoot 'Models\kokoro.onnx')
    $application = Start-Process -FilePath (Join-Path $installRoot 'PulseReader.exe') -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    $application.Refresh()
    if ($application.HasExited) { throw 'Installed application did not start with verified cache offline.' }
    $config = Join-Path $dataRoot 'config.json'
    $configHash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash
    Stop-Process -Id $application.Id
    $application.WaitForExit()
    $application = $null
    Check-Payload $installRoot 'offline-installed-smoke'
    Stage 'Checking actual portable first-launch terms and offline synthesis'
    $portableRoot = Join-Path $env:LOCALAPPDATA ('PulseReader-Portable-Test-' + [Guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath (Join-Path $inputs 'PulseReader-1.0.0-win-x64-Portable.zip') -DestinationPath $portableRoot
    $receiptKey = 'HKCU:\Software\QuantumDotGames\PulseReader'
    $receiptName = 'AcceptedDistributionTermsSha256'
    $receipt = (Get-ItemProperty -LiteralPath $receiptKey).$receiptName
    $termsContract = Get-Content -LiteralPath (Join-Path $portableRoot 'distribution-terms.json') -Raw | ConvertFrom-Json
    if ($receipt -ne $termsContract.TermsSha256 -or $receipt -ne (Get-FileHash -LiteralPath (Join-Path $portableRoot 'Distribution-Terms.txt') -Algorithm SHA256).Hash) { throw 'Installer and portable agreement receipts differ.' }
    Remove-ItemProperty -LiteralPath $receiptKey -Name $receiptName
    try {
        $startup = Start-Process -FilePath (Join-Path $portableRoot 'PulseReader.exe') -ArgumentList '--startup' -WorkingDirectory $portableRoot -WindowStyle Hidden -PassThru
        $null = $startup.Handle
        if (-not $startup.WaitForExit(15000)) { Stop-Process -Id $startup.Id; throw 'Unaccepted portable startup did not exit.' }
        $startup.Refresh()
        if ($null -eq $startup.ExitCode -or $startup.ExitCode -eq 0) { throw 'Unaccepted portable startup reported success.' }
        # Deliberately visible inside the disposable guest to verify the real prompt.
        $application = Start-Process -FilePath (Join-Path $portableRoot 'PulseReader.exe') -WorkingDirectory $portableRoot -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 200
            $application.Refresh()
            if ($application.HasExited) { throw 'Portable first-launch agreement exited unexpectedly.' }
        } while ($application.MainWindowTitle -ne 'Pulse Reader distribution terms' -and [DateTime]::UtcNow -lt $deadline)
        if ($application.MainWindowTitle -ne 'Pulse Reader distribution terms') { throw 'Portable first launch did not display its agreement.' }
        if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -ne $configHash) { throw 'Unaccepted portable launch altered settings.' }
        Stop-Process -Id $application.Id
        $application.WaitForExit()
        $application = $null
    }
    finally { New-ItemProperty -LiteralPath $receiptKey -Name $receiptName -Value $receipt -PropertyType String -Force | Out-Null }
    Check-Payload $portableRoot 'offline-portable-smoke'
    $application = Start-Process -FilePath (Join-Path $portableRoot 'PulseReader.exe') -WorkingDirectory $portableRoot -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    $application.Refresh()
    if ($application.HasExited -or $application.MainWindowTitle -eq 'Pulse Reader distribution terms') { throw 'Portable did not reuse the accepted agreement and model cache offline.' }
    if (Test-Path -LiteralPath (Join-Path $dataRoot 'ContextualPronunciation')) { throw 'Offline launch attempted optional installation.' }
    Stage 'Completed'
    @{ Passed = $true; FreshProfile = $true; NetworkingDisabled = $true; ActiveNonLoopbackInterfaces = $nonLoopbackUp.Count; VerifiedCacheSeeded = $true; ModelDownloadedInThisRun = $false; OfflineInstalledSynthesisPassed = $true; OfflinePortableSynthesisPassed = $true; PortableFirstLaunchTermsPassed = $true; UnacceptedStartupRejected = $true; SettingsPreserved = $true; OptionalModelDownloaded = $false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reports 'result.json') -Encoding UTF8
} catch {
    @{ Passed = $false; Error = $_.Exception.ToString(); Where = $_.ScriptStackTrace } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reports 'result.json') -Encoding UTF8
    exit 1
} finally {
    if ($null -ne $application -and -not $application.HasExited) { Stop-Process -Id $application.Id }
}
