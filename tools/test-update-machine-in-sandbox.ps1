#requires -Version 5.1
[CmdletBinding()]
param([switch]$Prepare, [switch]$Run, [switch]$Worker, [switch]$VerifyOnly, [switch]$FailureProbe, [string]$TemporaryPassword)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$staging = 'C:\Users\WDAGUtilityAccount\AppData\Local\Temp\PulseReaderMachineInputs'
$guestRoot = 'C:\PulseReaderUpdateQualification'
$reports = Join-Path $guestRoot 'Reports'
$mappedReports = 'C:\Users\WDAGUtilityAccount\Desktop\Reports'
$expectedScriptRoot = if ($Worker) { Join-Path $guestRoot 'Inputs' } else { $staging }
if ($PSScriptRoot -ne $expectedScriptRoot -or -not (Test-Path -LiteralPath 'C:\PulseReaderUacQualification\restart.marker' -PathType Leaf) -or
    (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System').EnableLUA -ne 1 -or
    (([int][bool]$Prepare + [int][bool]$Run + [int][bool]$Worker) -ne 1) -or
    (-not $Worker -and $TemporaryPassword.Length -lt 16) -or ($VerifyOnly -and $FailureProbe) -or
    ($Prepare -and ($VerifyOnly -or $FailureProbe))) { throw 'Invalid disposable UAC-enabled machine fixture invocation.' }
$runner = Join-Path $guestRoot 'Inputs\Controller\PulseReader.InputSequenceTests.exe'
$runnerArgument = if ($VerifyOnly) { '--sandbox-machine-verify-approved' } elseif ($FailureProbe) { '--failure-reporting-probe' } else { '--sandbox-machine-controller' }
$workerName = if ($VerifyOnly) { 'verification' } elseif ($FailureProbe) { 'failure-probe' } else { 'controller' }
$workerResult = Join-Path $reports ('machine-worker-' + $workerName + '.json')
if ($Worker) {
    if ($env:USERNAME -ne 'PulseUpdateFixture' -or $env:USERPROFILE -ne 'C:\Users\PulseUpdateFixture') { throw 'Worker must be the disposable ordinary user.' }
    # Observe the native runner exit code inside its own user context. The
    # cross-user Start-Process object can expose a null ExitCode in Windows PS.
    $nativeExit = 1
    try { & $runner $runnerArgument; $nativeExit = $LASTEXITCODE }
    finally { [IO.File]::WriteAllText($workerResult, (@{ NativeExitCode=$nativeExit; Mode=$runnerArgument } | ConvertTo-Json)) }
    exit $nativeExit
}
$securePassword = ConvertTo-SecureString $TemporaryPassword -AsPlainText -Force
function Export-MachineReports {
    foreach ($entry in Get-ChildItem -LiteralPath $reports -File | Where-Object Name -NotMatch '\.tmp-[0-9a-fA-F]{32}$') {
        Copy-Item -LiteralPath $entry.FullName -Destination $mappedReports -Force
    }
}
if ($Prepare) {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18') { throw 'Initial guest machine setup must run as guest SYSTEM.' }
    if (Test-Path -LiteralPath $guestRoot) { throw 'Machine setup requires a fresh local fixture root.' }
    if (Test-Path -LiteralPath 'C:\Program Files\PulseReader') { throw 'A guest machine application already exists.' }
    foreach ($entry in Get-ChildItem -LiteralPath $staging -Recurse -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked machine fixture input.' }
    }
    New-LocalUser -Name PulseUpdateFixture -Password $securePassword -PasswordNeverExpires -AccountNeverExpires | Out-Null
    New-LocalUser -Name PulseUpdateAdmin -Password $securePassword -PasswordNeverExpires -AccountNeverExpires | Out-Null
    $usersGroup = ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
    $adminGroup = ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
    Add-LocalGroupMember -Group $usersGroup -Member PulseUpdateFixture
    Add-LocalGroupMember -Group $adminGroup -Member PulseUpdateAdmin
    [IO.Directory]::CreateDirectory($guestRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $guestRoot 'guest-only.marker'), 'Disposable machine updater fixture only.')
    Copy-Item -LiteralPath $staging -Destination (Join-Path $guestRoot 'Inputs') -Recurse
    [IO.Directory]::CreateDirectory($reports) | Out-Null
    $rootAcl = Get-Acl -LiteralPath $guestRoot
    $rootAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('PulseUpdateFixture','ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $guestRoot -AclObject $rootAcl
    $reportAcl = Get-Acl -LiteralPath $reports
    foreach ($account in @('PulseUpdateFixture','WDAGUtilityAccount')) {
        $reportAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($account,'Modify','ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $reports -AclObject $reportAcl
    $initial = Start-Process -FilePath (Join-Path $guestRoot 'Inputs\PulseReader-1.0.0-win-x64-Setup.exe') -ArgumentList @('/ALLUSERS','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/ACCEPTDISTRIBUTIONTERMS=yes',('/LOG=' + (Join-Path $reports 'machine-initial-install.log'))) -WindowStyle Hidden -PassThru
    $null = $initial.Handle
    if (-not $initial.WaitForExit(300000)) { throw 'Guest initial machine installation timed out.' }
    $initial.Refresh()
    if ($initial.ExitCode -ne 0) { throw ('Guest initial machine installation failed: ' + $initial.ExitCode) }
    $sharedSentinel = 'C:\ProgramData\PulseReader\Models\machine-shared-sentinel.bin'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($sharedSentinel)) | Out-Null
    [IO.File]::WriteAllText($sharedSentinel, 'Disposable shared machine-model preservation sentinel.')
    Start-Service -Name seclogon
    [IO.File]::WriteAllText((Join-Path $reports 'machine-setup.json'), '{"Prepared":true,"GuestOnly":true}')
    exit 0
}
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or $env:USERPROFILE -ne 'C:\Users\WDAGUtilityAccount') { throw 'Run phase must use the guest interactive account.' }
try {
    $credential = [Management.Automation.PSCredential]::new('PulseUpdateFixture', $securePassword)
    if (Test-Path -LiteralPath $workerResult) { Remove-Item -LiteralPath $workerResult }
    $workerArguments = @('-NoProfile','-ExecutionPolicy','RemoteSigned','-File',(Join-Path $guestRoot 'Inputs\test-update-machine-in-sandbox.ps1'),'-Worker')
    if ($VerifyOnly) { $workerArguments += '-VerifyOnly' }
    if ($FailureProbe) { $workerArguments += '-FailureProbe' }
    $process = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList $workerArguments -Credential $credential -LoadUserProfile -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $reports ('machine-' + $workerName + '.stdout.txt')) -RedirectStandardError (Join-Path $reports ('machine-' + $workerName + '.stderr.txt'))
    $null = $process.Handle
    $deadline = [DateTime]::UtcNow.AddMinutes(45)
    while (-not $process.WaitForExit(1500)) {
        Export-MachineReports
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Human UAC fixture timed out; no installer was killed.' }
    }
    Export-MachineReports
    $process.Refresh()
    if (-not (Test-Path -LiteralPath $workerResult)) { throw 'Machine worker did not report its native runner exit code.' }
    $workerOutcome = Get-Content -LiteralPath $workerResult -Raw | ConvertFrom-Json
    if ($workerOutcome.NativeExitCode -ne 0) { throw ('Machine native test runner failed: ' + $workerOutcome.NativeExitCode) }
    $resultFile = if ($VerifyOnly) { 'machine-post-retry-validation.json' } else { 'machine-result.json' }
    if (-not (Get-Content -LiteralPath (Join-Path $reports $resultFile) -Raw | ConvertFrom-Json).Passed) { throw 'Machine assertions reported failure.' }
}
catch {
    [IO.File]::WriteAllText((Join-Path $mappedReports ('machine-' + $workerName + '-wrapper-error.json')), (@{ Error=$_.Exception.ToString() } | ConvertTo-Json))
    throw
}
