[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Destructive fixtures are confined to the disposable guest. Never run on the host.
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or $env:USERPROFILE -ne 'C:\Users\WDAGUtilityAccount') {
    throw 'This fixture runs only in the disposable Windows Sandbox account.'
}
$inputs = 'C:\Users\WDAGUtilityAccount\Desktop\Inputs'
$reports = 'C:\Users\WDAGUtilityAccount\Desktop\Reports'
if ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ne $inputs) { throw 'The script must run from the fixed guest Desktop Inputs mapping.' }
if (-not (Test-Path -LiteralPath $reports -PathType Container)) { throw 'The fixed Reports mapping is missing.' }
$started = [DateTime]::UtcNow
$cases = New-Object 'System.Collections.Generic.List[string]'
$stage = 'Prerequisites'
$qualificationNeeded = $false
$application = $null
$ownedReaders = $false
$userInstall = Join-Path $env:LOCALAPPDATA 'Programs\PulseReader'
$machineInstall = Join-Path $env:ProgramFiles 'PulseReader'
$userData = Join-Path $env:LOCALAPPDATA 'PulseReader'
$sharedData = Join-Path $env:ProgramData 'PulseReader'
$otherProfile = 'C:\Users\PulseReaderScopeOther'
$otherData = Join-Path $otherProfile 'AppData\Local\PulseReader'
$syntheticSid = 'S-1-5-21-2468101214-1357911131-1928374650-9876'
$profileKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
$syntheticProfileKey = Join-Path $profileKey $syntheticSid
$runSubkey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$receiptSubkey = 'Software\QuantumDotGames\PulseReader'
$receiptName = 'AcceptedDistributionTermsSha256'
$modelHash = '0CFD5E79AAB70A3D8C1A57DC639835110DDB32C9F5FF4FDD1F4DB202EA43BB05'
$manifestHash = '0F6CE5FC5599E5E5C2F1FD1A101D034284CEF896C03A7C86D5D538584A84882F'
$runnerFiles = @('PulseReader.InputSequenceTests.exe', 'PulseReader.InputSequenceTests.dll',
    'PulseReader.InputSequenceTests.deps.json', 'PulseReader.InputSequenceTests.runtimeconfig.json')
$foreignStartupCommand = '"C:\AnotherApplication\PulseReader.exe" --startup'
$cacheRoot = Join-Path $env:TEMP ('PulseReaderScopeCache-' + [Guid]::NewGuid().ToString('N'))
$cachedModel = Join-Path $cacheRoot 'kokoro.onnx'
$foreignFile = Join-Path $cacheRoot 'foreign.txt'
$termsHash = $null

function Assert-Condition([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Write-Stage([string]$Name) {
    $script:stage = $Name
    @{ Stage = $Name; Utc = [DateTime]::UtcNow.ToString('O'); ElapsedSeconds = ([DateTime]::UtcNow - $started).TotalSeconds; PassedCases = @($cases.ToArray()) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reports 'stage.json') -Encoding UTF8
}
function Pass([string]$Name) { $cases.Add($Name); Write-Stage $Name }
function Assert-GuestTarget([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $allowed = @($userInstall, $machineInstall, $userData, $sharedData, $otherProfile, $cacheRoot)
    foreach ($root in $allowed) {
        $resolved = [IO.Path]::GetFullPath($root).TrimEnd('\')
        if ($full -eq $resolved -or $full.StartsWith($resolved + '\', [StringComparison]::OrdinalIgnoreCase)) { return }
    }
    throw "Refusing a fixture mutation outside the exact guest targets: $full"
}
function Fixture-File([string]$Path, [string]$Value = 'scope fixture') {
    Assert-GuestTarget $Path
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Value)
}
function Read-Registry([Microsoft.Win32.RegistryKey]$Hive, [string]$Subkey, [string]$Name) {
    $key = $Hive.OpenSubKey($Subkey)
    try { if ($null -eq $key) { return $null }; return $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { if ($null -ne $key) { $key.Dispose() } }
}
function Seed-Registry([Microsoft.Win32.RegistryKey]$Hive, [string]$Startup) {
    $run = $Hive.CreateSubKey($runSubkey)
    try { $run.SetValue('Pulse Reader', $Startup); $run.SetValue('Pulse Reader scope foreign value', 'keep foreign value') }
    finally { $run.Dispose() }
    $receipt = $Hive.CreateSubKey($receiptSubkey)
    try { $receipt.SetValue($receiptName, $termsHash); $receipt.SetValue('ScopeFixtureForeign', 'keep foreign receipt value') }
    finally { $receipt.Dispose() }
}
function Assert-Registry([Microsoft.Win32.RegistryKey]$Hive, [bool]$RemoveReceipt, [bool]$ExpectForeignStartup) {
    $value = Read-Registry $Hive $runSubkey 'Pulse Reader'
    if ($ExpectForeignStartup) { Assert-Condition ($value -eq $script:foreignStartupCommand) 'Uninstall removed a foreign startup target.' }
    else { Assert-Condition ($null -eq $value) 'Uninstall retained its own startup target.' }
    $receipt = Read-Registry $Hive $receiptSubkey $receiptName
    if ($RemoveReceipt) { Assert-Condition ($null -eq $receipt) 'Remove everything retained an acceptance receipt.' }
    else { Assert-Condition ($receipt -eq $termsHash) 'Preservation/dependency cleanup changed an acceptance receipt.' }
    Assert-Condition ((Read-Registry $Hive $runSubkey 'Pulse Reader scope foreign value') -eq 'keep foreign value') 'Uninstall altered an unrelated startup value.'
    Assert-Condition ((Read-Registry $Hive $receiptSubkey 'ScopeFixtureForeign') -eq 'keep foreign receipt value') 'Uninstall altered an unrelated receipt-key value.'
}
function Run-Process([string]$Exe, [string[]]$Arguments, [string]$LogName, [bool]$Success = $true) {
    $log = Join-Path $reports $LogName
    $process = Start-Process -FilePath $Exe -ArgumentList ($Arguments + @('/LOG="' + $log + '"')) -WindowStyle Hidden -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(180000)) { throw "Installer/uninstaller timed out: $LogName" }
    $process.Refresh()
    Assert-Condition ($null -ne $process.ExitCode) "Unavailable process exit code: $LogName"
    Assert-Condition (($process.ExitCode -eq 0) -eq $Success) "Unexpected installer/uninstaller exit $($process.ExitCode): $LogName"
}
function Install([string]$Scope, [string]$Label) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/ACCEPTDISTRIBUTIONTERMS=yes')
    if ($Scope -ne 'default') { $arguments += ('/' + $Scope) }
    Run-Process $script:installer $arguments ($Label + '.log')
}
function Uninstall([string]$Root, [string]$Choice, [string]$Label, [bool]$Success = $true) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if ($Choice -ne 'default') { $arguments += ('/UNINSTALLDATA=' + $Choice) }
    Run-Process (Join-Path $Root 'unins000.exe') $arguments ($Label + '.log') $Success
    if ($Success) {
        # Inno's original process can exit before the temporary uninstaller's
        # final delayed directory-removal pass. Assert its completion, not timing.
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        $completed = $false
        do {
            $content = Get-Content -LiteralPath (Join-Path $reports ($Label + '.log')) -Raw
            $completed = $content.Contains('Uninstallation process succeeded.') -and $content.Contains('Log closed.')
            if ($completed) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert-Condition $completed ('Uninstaller did not finish its final cleanup pass: ' + $Label)
    }
}
function Assert-Marker([string]$Root, [string]$Scope) {
    $marker = Get-Content -LiteralPath (Join-Path $Root 'installation-scope.json') -Raw | ConvertFrom-Json
    Assert-Condition ($marker.SchemaVersion -eq 1 -and $marker.Scope -eq $Scope) "Wrong installation marker: $Root"
    Assert-Condition (Test-Path -LiteralPath (Join-Path $Root 'PulseReader.exe')) 'Installer omitted the executable.'
}
function Assert-Protected([string]$Path) {
    $acl = Get-Acl -LiteralPath $Path
    $trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    Assert-Condition ($trusted -contains $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) "Shared directory has an untrusted owner: $Path"
    $write = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    $userRead = $false; $adminWrite = $false
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
        $sid = $rule.IdentityReference.Value
        Assert-Condition (($rule.FileSystemRights -band $write) -eq 0 -or $trusted -contains $sid) "Ordinary users can write shared storage: $Path ($sid)"
        if ($sid -in @('S-1-5-32-545', 'S-1-5-11', 'S-1-1-0') -and ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq [Security.AccessControl.FileSystemRights]::ReadAndExecute) { $userRead = $true }
        if ($sid -eq 'S-1-5-32-544' -and ($rule.FileSystemRights -band $write) -ne 0) { $adminWrite = $true }
    }
    Assert-Condition ($userRead -and $adminWrite) "Shared storage lacks Users read/Admin write access: $Path"
}
function Stop-GuestReaders {
    if (-not $script:ownedReaders) { return }
    foreach ($process in @(Get-Process -Name PulseReader -ErrorAction SilentlyContinue)) {
        $path = $process.MainModule.FileName
        if ($path -eq (Join-Path $userInstall 'PulseReader.exe') -or $path -eq (Join-Path $machineInstall 'PulseReader.exe')) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit(15000) | Out-Null
        }
    }
    $script:application = $null
    $script:ownedReaders = $false
}
function Launch-Reader([string]$Root) {
    $script:ownedReaders = $true
    $script:application = Start-Process -FilePath (Join-Path $Root 'PulseReader.exe') -WorkingDirectory $Root -WindowStyle Hidden -PassThru
    $null = $script:application.Handle
}
function Invoke-GuestReg([string[]]$Arguments, [string]$Label) {
    # Callers supply only the private HKCU seed, one script-owned synthetic SID,
    # and that synthetic guest profile's exact NTUSER.DAT.
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\reg.exe') -ArgumentList $Arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $reports ($Label + '.stdout.txt')) -RedirectStandardError (Join-Path $reports ($Label + '.stderr.txt'))
    $null = $process.Handle
    if (-not $process.WaitForExit(30000)) { Stop-Process -Id $process.Id; throw "Guest registry fixture timed out: $Label" }
    $process.Refresh()
    Assert-Condition ($null -ne $process.ExitCode -and $process.ExitCode -eq 0) "Guest registry fixture failed: $Label"
}
function Check-Payload([string]$Root) {
    $copied = New-Object 'System.Collections.Generic.List[string]'
    $voiceAttributes = @{}
    try {
        foreach ($name in $runnerFiles) {
            $target = Join-Path $Root $name
            Assert-GuestTarget $target
            Assert-Condition (-not (Test-Path -LiteralPath $target)) "Test helper would overwrite a payload file: $target"
            Copy-Item -LiteralPath (Join-Path $inputs ('test-runner\' + $name)) -Destination $target
            $copied.Add($target)
        }
        # Even an administrator must exercise the immutable installed-asset path.
        # This catches path loaders that mistakenly request write access.
        foreach ($voice in Get-ChildItem -LiteralPath (Join-Path $Root 'voices') -Filter '*.npy' -File) {
            Assert-GuestTarget $voice.FullName
            $voiceAttributes[$voice.FullName] = $voice.Attributes
            [IO.File]::SetAttributes($voice.FullName, ($voice.Attributes -bor [IO.FileAttributes]::ReadOnly))
        }
        $checkArguments = @('--release-model-smoke')
        $checkLabel = 'machine-model-smoke'
        $process = Start-Process -FilePath (Join-Path $Root $runnerFiles[0]) -ArgumentList $checkArguments -WorkingDirectory $Root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $reports ($checkLabel + '.stdout.txt')) -RedirectStandardError (Join-Path $reports ($checkLabel + '.stderr.txt'))
        $null = $process.Handle
        if (-not $process.WaitForExit(180000)) { Stop-Process -Id $process.Id; throw "Installed payload check timed out: $checkLabel" }
        $process.Refresh()
        Assert-Condition ($null -ne $process.ExitCode -and $process.ExitCode -eq 0) "Installed payload check failed: $checkLabel"
    }
    finally {
        foreach ($path in $voiceAttributes.Keys) { Assert-GuestTarget $path; [IO.File]::SetAttributes($path, $voiceAttributes[$path]) }
        foreach ($path in $copied) { Assert-GuestTarget $path; Remove-Item -LiteralPath $path }
    }
}
function Seed-Data([string]$Root) {
    Fixture-File (Join-Path $Root 'config.json') '{"scopeFixture":"keep settings"}'
    Fixture-File (Join-Path $Root 'logs\fixture.log') 'keep logs'
    Fixture-File (Join-Path $Root 'Models\fixture-cache.bin') 'dependency cache'
    Fixture-File (Join-Path $Root 'ContextualPronunciation\models\fixture.gguf') 'optional fixture, not a verified model'
    Fixture-File (Join-Path $Root 'ContextualPronunciation\runtime\fixture.dll')
    Fixture-File (Join-Path $Root 'ContextualPronunciation\installation-manifest.json') 'invalid fixture manifest'
    Fixture-File (Join-Path $Root 'ContextualPronunciation\logs\fixture.log') 'keep optional logs'
    Fixture-File (Join-Path $Root 'ContextualPronunciation\settings.json') 'keep optional settings'
}
function Assert-NoOptionalDownload {
    foreach ($root in @($sharedData, $userData)) {
        if (Test-Path -LiteralPath $root) {
            Assert-Condition (@(Get-ChildItem -LiteralPath $root -Force | Where-Object { $_.Name.StartsWith('ContextualPronunciation', [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) "Startup created optional model, staging, lock or log files: $root"
        }
    }
    Assert-Condition (@(Get-ChildItem -LiteralPath (Join-Path $machineInstall 'OptionalRuntime') -Force).Count -eq 0) 'Startup populated OptionalRuntime without explicit optional setup.'
}
function Assert-Data([string]$Root, [string]$Choice) {
    if ($Choice -eq 'everything') { Assert-Condition (-not (Test-Path -LiteralPath $Root)) "Remove everything retained data: $Root"; return }
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $Root 'config.json') -Raw) -eq '{"scopeFixture":"keep settings"}') "Cleanup changed settings: $Root"
    foreach ($file in @('logs\fixture.log', 'ContextualPronunciation\logs\fixture.log', 'ContextualPronunciation\settings.json')) { Assert-Condition (Test-Path -LiteralPath (Join-Path $Root $file)) "Cleanup deleted preserved personal data: $file" }
    foreach ($file in @('Models\fixture-cache.bin', 'ContextualPronunciation\models\fixture.gguf', 'ContextualPronunciation\runtime\fixture.dll', 'ContextualPronunciation\installation-manifest.json')) {
        Assert-Condition ((Test-Path -LiteralPath (Join-Path $Root $file)) -eq ($Choice -eq 'preserve')) "Wrong dependency cleanup for $file ($Choice)"
    }
}

try {
    # Preserve earlier evidence if a failed run left a report in the mapped folder.
    foreach ($name in @('result.json', 'stage.json')) {
        $prior = Join-Path $reports $name
        if (Test-Path -LiteralPath $prior) { Copy-Item -LiteralPath $prior -Destination (Join-Path $reports ($name + '.prior-' + $started.ToString('yyyyMMddTHHmmssfff') + '.json')) }
    }
    Write-Stage 'Checking fresh guest and inputs'
    foreach ($root in @($userInstall, $machineInstall, $userData, $sharedData, $otherProfile)) { Assert-Condition (-not (Test-Path -LiteralPath $root)) "Fresh guest required; existing fixture/application data: $root" }
    Assert-Condition (-not (Test-Path -LiteralPath $syntheticProfileKey)) 'Synthetic profile key already exists.'
    $existingHive = [Microsoft.Win32.Registry]::Users.OpenSubKey($syntheticSid)
    try { Assert-Condition ($null -eq $existingHive) 'Synthetic loaded hive key already exists.' } finally { if ($null -ne $existingHive) { $existingHive.Dispose() } }
    $installers = @(Get-ChildItem -LiteralPath $inputs -Filter 'PulseReader-*-win-x64-Setup.exe' -File)
    Assert-Condition ($installers.Count -eq 1) 'Inputs must contain exactly one standard self-contained installer.'
    $script:installer = $installers[0].FullName
    foreach ($name in $runnerFiles) { Assert-Condition (Test-Path -LiteralPath (Join-Path $inputs ('test-runner\' + $name))) "Missing test helper input: $name" }
    New-Item -ItemType Directory -Path $cacheRoot | Out-Null
    Fixture-File $foreignFile 'keep unrelated current-user file'

    Write-Stage 'Checking the fresh installer default without a scope override'
    Install 'default' 'user-default-install'
    Assert-Marker $userInstall 'user'
    Assert-Condition (-not (Test-Path -LiteralPath $machineInstall)) 'The default installer selected all-users scope.'
    Assert-Condition (-not (Test-Path -LiteralPath $userData)) 'The installer created user configuration/data before the app ran.'
    $termsHash = (Get-Content -LiteralPath (Join-Path $userInstall 'distribution-terms.json') -Raw | ConvertFrom-Json).TermsSha256
    Assert-Condition ($termsHash -eq (Get-FileHash -LiteralPath (Join-Path $userInstall 'Distribution-Terms.txt') -Algorithm SHA256).Hash) 'Package terms hash is inconsistent.'
    Pass 'FreshDefaultIsPerUserAndCreatesNoProfileData'

    # Exercise each current-user choice with both owned and foreign named startup values.
    foreach ($choice in @('preserve', 'dependencies', 'everything')) {
        foreach ($foreign in @($false, $true)) {
            $label = 'user-' + $choice + '-' + $foreign
            if (-not (Test-Path -LiteralPath (Join-Path $userInstall 'PulseReader.exe'))) { Install 'CURRENTUSER' ($label + '-install') }
            Assert-Marker $userInstall 'user'
            Seed-Data $userData
            $startup = '"' + (Join-Path $userInstall 'PulseReader.exe') + '" --startup'
            if ($foreign) { $startup = $script:foreignStartupCommand }
            Seed-Registry ([Microsoft.Win32.Registry]::CurrentUser) $startup
            $argument = $choice; if ($choice -eq 'preserve') { $argument = 'default' }
            Uninstall $userInstall $argument ($label + '-uninstall')
            Assert-Condition (-not (Test-Path -LiteralPath $userInstall)) 'Per-user uninstall retained an empty application directory.'
            Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $userInstall 'PulseReader.exe'))) 'Current-user uninstall retained the executable.'
            Assert-Data $userData $choice
            Assert-Registry ([Microsoft.Win32.Registry]::CurrentUser) ($choice -eq 'everything') $foreign
            Assert-Condition ((Get-Content -LiteralPath $foreignFile -Raw) -eq 'keep unrelated current-user file') 'Current-user cleanup removed an unrelated file.'
            Pass ('CurrentUser-' + $choice + '-ForeignStartup=' + $foreign)
        }
    }

    Write-Stage 'Checking all-users prerequisites'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $qualificationNeeded = $true
        throw 'All-users qualification is blocked: the guest does not have an administrator token. No UAC automation was attempted.'
    }
    $currentSid = $identity.User.Value
    Assert-Condition (Test-Path -LiteralPath (Join-Path $profileKey $currentSid)) 'The current guest is absent from ProfileList; all-profile cleanup cannot be qualified.'
    foreach ($profile in Get-ChildItem -LiteralPath $profileKey) {
        if ($profile.PSChildName -notmatch '^(S-1-5-21-|S-1-12-1-)' -or $profile.PSChildName.EndsWith('.bak')) { continue }
        $loaded = [Microsoft.Win32.Registry]::Users.OpenSubKey($profile.PSChildName)
        try {
            if ($null -eq $loaded) { $qualificationNeeded = $true; throw "A normal guest profile hive is not loaded: $($profile.PSChildName). No fake successful cleanup will be reported." }
        } finally { if ($null -ne $loaded) { $loaded.Dispose() } }
    }
    # Synthetic cleanup profile only; no Windows account or credentials are created.
    # Register it before installation so both installer and first app launch are
    # checked against a profile whose personal data has not been manufactured.
    New-Item -ItemType Directory -Path $otherProfile | Out-Null
    New-Item -Path $syntheticProfileKey | Out-Null
    New-ItemProperty -LiteralPath $syntheticProfileKey -Name ProfileImagePath -Value $otherProfile -PropertyType ExpandString | Out-Null
    # HKU roots must be mounted Windows hives. Seed a private HKCU key, save it
    # into this synthetic profile, and mount that file at the exact synthetic SID.
    $hivePath = Join-Path $otherProfile 'NTUSER.DAT'
    Assert-GuestTarget $hivePath
    Assert-Condition (-not (Test-Path -LiteralPath $hivePath)) 'Synthetic hive file already exists.'
    $seedSubkey = 'Software\PulseReaderScopeHiveSeed_' + [Guid]::NewGuid().ToString('N')
    $existingSeed = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($seedSubkey)
    try { Assert-Condition ($null -eq $existingSeed) 'Synthetic HKCU hive seed already exists.' }
    finally { if ($null -ne $existingSeed) { $existingSeed.Dispose() } }
    $seedKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($seedSubkey)
    try { $seedKey.SetValue('ScopeFixtureHiveSeed', 'synthetic cleanup hive only') }
    finally { $seedKey.Dispose() }
    try { Invoke-GuestReg @('SAVE', ('HKCU\' + $seedSubkey), $hivePath, '/y') 'synthetic-hive-create-save' }
    finally { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($seedSubkey) }
    Invoke-GuestReg @('LOAD', ('HKU\' + $syntheticSid), $hivePath) 'synthetic-hive-create-load'
    Fixture-File (Join-Path $otherProfile 'foreign.txt') 'keep unrelated profile file'
    Install 'ALLUSERS' 'machine-first-install'
    Assert-Marker $machineInstall 'machine'
    Assert-Condition (-not (Test-Path -LiteralPath $userInstall)) 'All-users setup recreated the current-user installation.'
    Assert-Condition (-not (Test-Path -LiteralPath $userData)) 'All-users installer manufactured a profile configuration.'
    Assert-Condition (-not (Test-Path -LiteralPath $otherData)) 'All-users installer manufactured another profile configuration.'
    foreach ($path in @($machineInstall, $sharedData, (Join-Path $machineInstall 'OptionalRuntime'))) { Assert-Protected $path }
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $sharedData 'Models\kokoro.onnx'))) 'The live setup check requires no existing global model.'
    Pass 'MachineCanonicalMarkerAndProtectedStorage'

    Assert-Condition (-not (Test-Path -LiteralPath $otherData)) 'Synthetic profile data was created before app setup.'
    # The maintainer accepted the exact package terms for this approved test. This is guest HKCU only.
    $receipt = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($receiptSubkey)
    try { $receipt.SetValue($receiptName, $termsHash) } finally { $receipt.Dispose() }
    Write-Stage 'Running actual installed all-users first-run Kokoro download and privileged IPC'
    Launch-Reader $machineInstall
    $model = Join-Path $sharedData 'Models\kokoro.onnx'
    $deadline = [DateTime]::UtcNow.AddMinutes(20)
    $sawSetup = $false
    do {
        Start-Sleep -Seconds 2
        $application.Refresh()
        Assert-Condition (-not $application.HasExited) 'The actual installed reader exited before shared setup completed.'
        if ($application.MainWindowTitle -eq 'Welcome to Pulse Reader') { $sawSetup = $true }
        $bytes = 0; if (Test-Path -LiteralPath $model) { $bytes = (Get-Item -LiteralPath $model).Length }
        $downloadBytes = 0
        if (Test-Path -LiteralPath (Join-Path $sharedData 'Models')) {
            foreach ($partial in @(Get-ChildItem -LiteralPath (Join-Path $sharedData 'Models') -Filter 'kokoro.onnx.*.download' -File)) { $downloadBytes += $partial.Length }
        }
        @{ Stage = $stage; Utc = [DateTime]::UtcNow.ToString('O'); ModelBytes = $bytes; InProgressDownloadBytes = $downloadBytes; MainWindowTitle = $application.MainWindowTitle; ReaderPid = $application.Id; SawSetupDialog = $sawSetup; PassedCases = @($cases.ToArray()) } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reports 'live-setup.json') -Encoding UTF8
        if ($bytes -eq 325508342 -and (Test-Path -LiteralPath (Join-Path $userData 'config.json')) -and $application.MainWindowTitle -ne 'Welcome to Pulse Reader' -and $application.MainWindowTitle -ne 'Pulse Reader') { break }
        if ([DateTime]::UtcNow -ge $deadline) {
            $qualificationNeeded = $true
            Get-CimInstance Win32_Process | Where-Object Name -eq 'PulseReader.exe' | Select-Object ProcessId, ParentProcessId, ExecutablePath, CommandLine | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reports 'setup-timeout-processes.json') -Encoding UTF8
            throw 'Actual all-users first-run setup did not finish within 20 minutes. Inspect live-setup.json and process diagnostics; UAC/setup remains unqualified.'
        }
    } while ($true)
    Assert-Condition ((Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash -eq $modelHash) 'The actual shared Kokoro download failed its pinned SHA256.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $userData 'Models\kokoro.onnx'))) 'All-users startup downloaded Kokoro into the private cache.'
    Assert-Condition (-not (Test-Path -LiteralPath $otherData)) 'The real app generated another profile configuration.'
    Assert-NoOptionalDownload
    Stop-GuestReaders
    Copy-Item -LiteralPath $model -Destination $cachedModel
    Pass 'ActualMachineFirstRunPinnedDownloadAndSetupCompleted'
    Check-Payload $machineInstall
    Pass 'InstalledMachineModelProducedPCM'
    Pass 'ProductionVoiceSelectionWithAllInstalledVoicesReadOnly'

    Write-Stage 'Checking actual missing optional-model startup reconciliation'
    $configPath = Join-Path $userData 'config.json'
    $configuration = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $configurationBefore = $configuration | ConvertTo-Json -Depth 10 -Compress
    $configuration.contextualPronunciationEnabled = $true
    $configuration | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding UTF8
    Launch-Reader $machineInstall
    Start-Sleep -Seconds 5
    $application.Refresh()
    Assert-Condition (-not $application.HasExited) 'Reader exited during missing optional-model restart.'
    $reconciledConfiguration = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    Assert-Condition (-not $reconciledConfiguration.contextualPronunciationEnabled) 'Missing optional installation did not persist the setting off.'
    Assert-Condition (($reconciledConfiguration | ConvertTo-Json -Depth 10 -Compress) -eq $configurationBefore) 'Missing optional startup changed other settings or the hotkey.'
    Assert-Condition ($application.MainWindowTitle -ne 'Optional pronunciation model') 'Startup opened an optional download dialog.'
    Assert-NoOptionalDownload
    Stop-GuestReaders
    Pass 'ActualMissingOptionalStartupResetWithoutDownload'

    foreach ($choice in @('preserve', 'dependencies', 'everything')) {
        Write-Stage ('All-users cleanup choice: ' + $choice)
        if (-not (Test-Path -LiteralPath (Join-Path $machineInstall 'PulseReader.exe'))) { Install 'ALLUSERS' ('machine-' + $choice + '-install') }
        Assert-Marker $machineInstall 'machine'
        Seed-Data $sharedData; Seed-Data $userData; Seed-Data $otherData
        $model = Join-Path $sharedData 'Models\kokoro.onnx'
        if (-not (Test-Path -LiteralPath $model)) { Copy-Item -LiteralPath $cachedModel -Destination $model }
        $runtimeFixture = Join-Path $machineInstall ('OptionalRuntime\' + $manifestHash + '\fixture.dll')
        Fixture-File $runtimeFixture 'machine runtime fixture, not executable'
        $ownedStartup = '"' + (Join-Path $machineInstall 'PulseReader.exe') + '" --startup'
        Seed-Registry ([Microsoft.Win32.Registry]::CurrentUser) $ownedStartup
        $otherHive = [Microsoft.Win32.Registry]::Users.OpenSubKey($syntheticSid, $true)
        try { Seed-Registry $otherHive $script:foreignStartupCommand } finally { $otherHive.Dispose() }
        if ($choice -eq 'everything') {
            # Offline registries are deliberately preserved, even though their
            # Pulse Reader files must be removed by full all-users cleanup.
            $otherHive = [Microsoft.Win32.Registry]::Users.OpenSubKey($syntheticSid, $true)
            try { Seed-Registry $otherHive $ownedStartup } finally { $otherHive.Dispose() }
            $hivePath = Join-Path $otherProfile 'NTUSER.DAT'
            Assert-GuestTarget $hivePath
            Assert-Condition (Test-Path -LiteralPath $hivePath -PathType Leaf) 'Synthetic offline hive file is missing.'
            Invoke-GuestReg @('UNLOAD', ('HKU\' + $syntheticSid)) 'synthetic-hive-before-cleanup-unload'
            $offlineHiveHash = (Get-FileHash -LiteralPath $hivePath -Algorithm SHA256).Hash
            Assert-Condition (-not ([Microsoft.Win32.Registry]::Users.GetSubKeyNames() -contains $syntheticSid)) 'Synthetic fixture hive remained loaded.'
            Assert-Condition (@([Microsoft.Win32.Registry]::Users.GetSubKeyNames() | Where-Object { $_.StartsWith('PulseReaderUninstall_', [StringComparison]::Ordinal) }).Count -eq 0) 'An uninstaller temporary hive was already loaded.'
        }
        $argument = $choice; if ($choice -eq 'preserve') { $argument = 'default' }
        if ($choice -eq 'preserve') {
            $hivePath = Join-Path $otherProfile 'NTUSER.DAT'
            Invoke-GuestReg @('UNLOAD', ('HKU\' + $syntheticSid)) 'synthetic-hive-before-preserve-unload'
            $offlineHiveHash = (Get-FileHash -LiteralPath $hivePath -Algorithm SHA256).Hash
        }
        Uninstall $machineInstall $argument ('machine-' + $choice + '-uninstall')
        Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $machineInstall 'PulseReader.exe'))) 'Machine uninstall retained the executable.'
        Assert-Data $sharedData $choice
        $profileChoice = 'preserve'; if ($choice -eq 'everything') { $profileChoice = 'everything' }
        Assert-Data $userData $profileChoice; Assert-Data $otherData $profileChoice
        Assert-Condition ((Test-Path -LiteralPath $runtimeFixture) -eq ($choice -eq 'preserve')) 'Machine dependency cleanup used the wrong OptionalRuntime layout.'
        if ($choice -ne 'preserve') { Assert-Condition (-not (Test-Path -LiteralPath $machineInstall)) 'Machine uninstall retained an empty application directory.' }
        Assert-Registry ([Microsoft.Win32.Registry]::CurrentUser) ($choice -eq 'everything') $false
        if ($choice -in @('preserve', 'everything')) {
            Assert-Condition ((Get-FileHash -LiteralPath $hivePath -Algorithm SHA256).Hash -eq $offlineHiveHash) 'Uninstall changed an offline user registry file.'
            Assert-Condition (-not ([Microsoft.Win32.Registry]::Users.GetSubKeyNames() -contains $syntheticSid)) 'Uninstall loaded an offline user registry.'
            Assert-Condition (@([Microsoft.Win32.Registry]::Users.GetSubKeyNames() | Where-Object { $_.StartsWith('PulseReaderUninstall_', [StringComparison]::Ordinal) }).Count -eq 0) 'Uninstaller left its temporary offline hive mounted.'
            Invoke-GuestReg @('LOAD', ('HKU\' + $syntheticSid), (Join-Path $otherProfile 'NTUSER.DAT')) ('synthetic-hive-inspect-' + $choice + '-load')
            try {
                $otherHive = [Microsoft.Win32.Registry]::Users.OpenSubKey($syntheticSid)
                try {
                    if ($choice -eq 'preserve') { Assert-Registry $otherHive $false $true }
                    else {
                        Assert-Condition ((Read-Registry $otherHive $runSubkey 'Pulse Reader') -eq $ownedStartup) 'Offline startup record was changed.'
                        Assert-Condition ((Read-Registry $otherHive $receiptSubkey $receiptName) -eq $termsHash) 'Offline terms record was changed.'
                        Assert-Condition ((Read-Registry $otherHive $runSubkey 'Pulse Reader scope foreign value') -eq 'keep foreign value') 'Offline foreign registry value was changed.'
                    }
                } finally { $otherHive.Dispose() }
            }
            finally { if ($choice -eq 'everything') { Invoke-GuestReg @('UNLOAD', ('HKU\' + $syntheticSid)) 'synthetic-hive-inspect-unload' } }
            Pass ('OfflineRegistryUnchanged-' + $choice)
        }
        else {
            $otherHive = [Microsoft.Win32.Registry]::Users.OpenSubKey($syntheticSid)
            try { Assert-Registry $otherHive $false $true } finally { $otherHive.Dispose() }
        }
        Assert-Condition ((Get-Content -LiteralPath (Join-Path $otherProfile 'foreign.txt') -Raw) -eq 'keep unrelated profile file') 'All-users cleanup deleted unrelated profile files.'
        Assert-Condition ((Get-Content -LiteralPath $foreignFile -Raw) -eq 'keep unrelated current-user file') 'All-users cleanup removed an unrelated current-user file.'
        if ($choice -eq 'preserve') { Assert-Condition ((Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash -eq $modelHash) 'Preserve changed the shared model.' }
        else { Assert-Condition (-not (Test-Path -LiteralPath $model)) 'Dependency/everything cleanup retained the shared model.' }
        Pass ('Machine-' + $choice + '-SharedAndSyntheticProfileCleanup')
        if ($choice -eq 'everything') { Pass 'MachineEverythingUsesSingleExplicitChoice' }
    }
    Write-Stage 'Checking actual empty shared-container removal'
    Install 'ALLUSERS' 'machine-empty-containers-install'
    Assert-Marker $machineInstall 'machine'
    Assert-GuestTarget $sharedData
    $emptyModels = Join-Path $sharedData 'Models'
    $emptyOptional = Join-Path $sharedData 'ContextualPronunciation'
    New-Item -ItemType Directory -Path $emptyModels | Out-Null
    New-Item -ItemType Directory -Path $emptyOptional | Out-Null
    Copy-Item -LiteralPath $cachedModel -Destination (Join-Path $emptyModels 'kokoro.onnx')
    Uninstall $machineInstall 'dependencies' 'machine-empty-containers-uninstall'
    Assert-Condition (-not (Test-Path -LiteralPath $machineInstall)) 'Empty Program Files application root survived removal.'
    Assert-Condition (-not (Test-Path -LiteralPath $sharedData)) 'Empty ProgramData model/optional containers survived removal.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $otherProfile 'foreign.txt') -Raw) -eq 'keep unrelated profile file') 'Empty-container cleanup removed unrelated profile data.'
    Pass 'ActualEmptyProgramFilesAndProgramDataContainersRemoved'
    Write-Stage 'Completed'
    @{ Passed = $true; Status = 'Passed'; Stage = $stage; PassedCases = @($cases.ToArray()); ElapsedSeconds = ([DateTime]::UtcNow - $started).TotalSeconds; ActualMachineDownload = $true; OptionalModelDownloaded = $false; SyntheticProfileOnly = $true; StandardUserUacQualificationRequired = $true; QualificationNeeded = $false } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reports 'result.json') -Encoding UTF8
}
catch {
    $status = 'Failed'; if ($qualificationNeeded) { $status = 'QualificationNeeded' }
    @{ Passed = $false; Status = $status; Stage = $stage; Error = $_.Exception.ToString(); Where = $_.ScriptStackTrace; PassedCases = @($cases.ToArray()); ElapsedSeconds = ([DateTime]::UtcNow - $started).TotalSeconds; QualificationNeeded = $qualificationNeeded; SyntheticProfileOnly = $true } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reports 'result.json') -Encoding UTF8
    exit 1
}
finally { Stop-GuestReaders }
