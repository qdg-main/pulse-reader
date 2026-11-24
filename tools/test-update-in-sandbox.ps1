#requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = 'Prepare')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][string]$CandidateDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][string]$OutputRoot,
    [Parameter(ParameterSetName = 'Prepare')][string]$ReuseBuildRoot,
    [Parameter(ParameterSetName = 'Prepare')][string]$IsccPath,
    [Parameter(ParameterSetName = 'Prepare')][ValidateSet('win-x64', 'win-x64-framework-dependent')][string]$Variant = 'win-x64',
    [Parameter(ParameterSetName = 'Prepare')][string]$DesktopRuntimeInstaller,
    [switch]$InstallerQualification,
    [switch]$InteractiveQualification,
    [Parameter(ParameterSetName = 'Prepare')][switch]$ShippingRelaunch,
    [Parameter(ParameterSetName = 'Prepare')][switch]$PhysicalCancelProbe,
    [Parameter(ParameterSetName = 'Prepare')][switch]$MachineElevationPayload,
    [Parameter(Mandatory, ParameterSetName = 'Guest')][switch]$Guest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Condition([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 16) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Assert-RegularDirectory([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        $entry = $null
        try { $entry = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] { }
        if ($null -ne $entry -and
            (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or -not $entry.PSIsContainer)) {
            throw "Unsafe directory ancestor: $cursor"
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
function Assert-RegularTree([string]$Root) {
    Assert-RegularDirectory $Root
    Assert-Condition (Test-Path -LiteralPath $Root -PathType Container) "Fixture source directory is missing: $Root"
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Fixture source contains a link: $($entry.FullName)" }
    }
}
function Assert-DisjointTrees([string]$First, [string]$Second) {
    $firstRoot = [IO.Path]::GetFullPath($First).TrimEnd('\')
    $secondRoot = [IO.Path]::GetFullPath($Second).TrimEnd('\')
    # Device/extended namespaces can name the same directory differently and
    # have different Win32 normalization rules. Do not rewrite them into an
    # ordinary path and then copy using the original, ambiguous spelling.
    foreach ($root in @($firstRoot, $secondRoot)) {
        if ($root.StartsWith('\\?\', [StringComparison]::Ordinal) -or
            $root.StartsWith('\\.\', [StringComparison]::Ordinal) -or
            $root.StartsWith('\??\', [StringComparison]::Ordinal)) {
            throw "Device namespace fixture paths are unsupported: $root"
        }
    }
    if ($firstRoot.Equals($secondRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $firstRoot.StartsWith($secondRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $secondRoot.StartsWith($firstRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Fixture source and output trees overlap: $First; $Second"
    }
}
function Copy-Tree([string]$Source, [string]$Destination) {
    Assert-DisjointTrees $Source $Destination
    Assert-RegularTree $Source
    Assert-RegularDirectory $Destination
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
        $relative = $entry.FullName.Substring($Source.TrimEnd('\').Length + 1)
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $entry.FullName -Destination $target -Force
    }
}

if (-not $Guest) {
    $repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\')
    $taskOutput = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
    Assert-Condition ($taskOutput.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) 'Preparation output must be a fresh directory below repository artifacts.'
    Assert-Condition (-not (Test-Path -LiteralPath $taskOutput)) 'Preparation output already exists; choose a fresh directory.'
    Assert-RegularDirectory $taskOutput
    $candidate = [IO.Path]::GetFullPath($CandidateDirectory)
    Assert-DisjointTrees $candidate $taskOutput
    $selfContained = $Variant -eq 'win-x64'
    Assert-Condition (-not $ShippingRelaunch -or $InteractiveQualification) 'Shipping relaunch requires the interactive qualification parent.'
    Assert-Condition (-not $PhysicalCancelProbe -or $InteractiveQualification) 'Physical cancellation requires the app-context UI fixture.'
    Assert-Condition (-not $MachineElevationPayload -or ($InstallerQualification -and $selfContained -and -not $InteractiveQualification)) 'Machine elevation payloads require the self-contained installer and dedicated machine controller.'
    Assert-Condition ($selfContained -or $InteractiveQualification) 'Framework-dependent qualification requires the app-context UI fixture.'
    $payload = Join-Path $candidate ('payload\' + $Variant)
    Assert-RegularTree $payload
    Assert-Condition (Test-Path -LiteralPath (Join-Path $payload 'payload-files.txt') -PathType Leaf) 'A built win-x64 candidate payload is required.'
    $inventory = Get-Content -LiteralPath (Join-Path $payload 'dependency-inventory.json') -Raw | ConvertFrom-Json
    Assert-Condition ($inventory.AppVersion -eq '1.0.0' -and $inventory.Variant -eq $Variant) 'This fixture requires the selected 1.0.0 package variant.'
    if (-not $selfContained) {
        Assert-Condition ([bool]$DesktopRuntimeInstaller -and (Test-Path -LiteralPath $DesktopRuntimeInstaller -PathType Leaf)) 'Framework-dependent guests require an official Desktop Runtime installer.'
        $runtimeSignature = Get-AuthenticodeSignature -LiteralPath $DesktopRuntimeInstaller
        Assert-Condition ($runtimeSignature.Status -eq 'Valid' -and $runtimeSignature.SignerCertificate.Subject -match 'Microsoft Corporation') 'The Desktop Runtime installer must have a valid Microsoft signature.'
    }
    if ($ReuseBuildRoot) {
        $reuse = [IO.Path]::GetFullPath($ReuseBuildRoot).TrimEnd('\')
        Assert-Condition ($reuse.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) 'Reused isolated builds must stay below repository artifacts.'
        Assert-DisjointTrees $reuse $taskOutput
        $reusedRunner = Join-Path $reuse 'Inputs\Controller'
        $reusedFuture = Join-Path $reuse 'Build\future-publish'
        Assert-RegularTree $reusedRunner
        Assert-RegularTree $reusedFuture
        Assert-Condition ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $reusedRunner 'PulseReader.dll')).FileVersion -eq '1.0.0.0') 'Reused controller must contain the original 1.0.0 core.'
        Assert-Condition ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $reusedFuture 'PulseReader.dll')).FileVersion -eq '1.0.1.0') 'Reused future publish must contain a real 1.0.1 core.'
        $reusedIdentity = Get-Content -LiteralPath (Join-Path $reuse 'Inputs\qualification-input.json') -Raw | ConvertFrom-Json
        Assert-Condition ($reusedIdentity.Variant -eq $Variant) 'Reused build flavour must match the selected variant.'
    }
    [IO.Directory]::CreateDirectory($taskOutput) | Out-Null
    $inputs = Join-Path $taskOutput 'Inputs'
    $reports = Join-Path $taskOutput 'Reports'
    $work = Join-Path $taskOutput 'Build'
    [IO.Directory]::CreateDirectory($inputs) | Out-Null
    [IO.Directory]::CreateDirectory($reports) | Out-Null
    if (-not $selfContained) {
        Copy-Item -LiteralPath $DesktopRuntimeInstaller -Destination (Join-Path $inputs 'windowsdesktop-runtime-win-x64.exe')
    }
    $runner = Join-Path $inputs 'Controller'
    $future = Join-Path $work 'future-publish'
    if ($ReuseBuildRoot) {
        $future = $reusedFuture
        Copy-Tree $reusedRunner $runner
    }
    else {
        $packageCache = Join-Path $env:USERPROFILE '.nuget\packages'
        Assert-Condition (Test-Path -LiteralPath $packageCache -PathType Container) 'Offline NuGet package cache is required.'
        # Independent ArtifactsPath roots prevent either publish from touching
        # the running app, ordinary Release output or another worker's obj files.
        & dotnet publish (Join-Path $repositoryRoot 'tests\PulseReader.InputSequenceTests\PulseReader.InputSequenceTests.csproj') `
            -c Release -r win-x64 --self-contained $selfContained.ToString().ToLowerInvariant() -m:1 -p:Version=1.0.0 -p:NuGetAudit=false `
            ('-p:ArtifactsPath=' + (Join-Path $work 'runner-artifacts')) --source $packageCache -o $runner
        Assert-Condition ($LASTEXITCODE -eq 0) 'Offline isolated test runner publish failed.'
        & dotnet publish (Join-Path $repositoryRoot 'src\PulseReader\PulseReader.csproj') `
            -c Release -r win-x64 --self-contained $selfContained.ToString().ToLowerInvariant() -m:1 -p:Version=1.0.1 -p:NuGetAudit=false `
            ('-p:ArtifactsPath=' + (Join-Path $work 'future-artifacts')) --source $packageCache -o $future
        Assert-Condition ($LASTEXITCODE -eq 0) 'Offline isolated future application publish failed.'
    }

    $old = Join-Path $inputs 'OldPayload'
    $new = Join-Path $work 'NewPayload'
    Copy-Tree $payload $old
    $runnerNames = @('PulseReader.InputSequenceTests.exe', 'PulseReader.InputSequenceTests.dll',
        'PulseReader.InputSequenceTests.deps.json', 'PulseReader.InputSequenceTests.runtimeconfig.json')
    foreach ($name in $runnerNames) { Copy-Item -LiteralPath (Join-Path $runner $name) -Destination (Join-Path $old $name) }
    Copy-Item -LiteralPath (Join-Path $runner 'PulseReader.InputSequenceTests.exe') -Destination (Join-Path $old 'PulseReader.exe') -Force
    Write-Json (Join-Path $old 'update-qualification-fixture.json') @{ Fixture = 'disposable-portable-update'; SchemaVersion = 1 }
    if ($MachineElevationPayload) {
        Write-Json (Join-Path $old 'machine-elevation-fixture.json') @{ Fixture = 'disposable-machine-elevation'; SchemaVersion = 1 }
    }
    if ($InteractiveQualification) {
        Write-Json (Join-Path $old 'update-interactive-qualification-fixture.json') @{ Fixture = 'disposable-interactive-updater-ui'; SchemaVersion = 1 }
    }
    function Write-PayloadList([string]$Root) {
        $names = @(Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring($Root.Length + 1)
            if ($InstallerQualification) { $relative } else { $relative.Replace('\', '/') }
        } | Sort-Object)
        if ($names -notcontains 'payload-files.txt') { $names += 'payload-files.txt' }
        [IO.File]::WriteAllLines((Join-Path $Root 'payload-files.txt'), $names, [Text.UTF8Encoding]::new($false))
    }
    Write-PayloadList $old
    Copy-Tree $old $new
    # Use the complete 1.0.1 publish, including its matching dependency assembly
    # versions and runtime manifests. Restore only the fixture apphost afterward.
    Copy-Tree $future $new
    if (-not $ShippingRelaunch) {
        Copy-Item -LiteralPath (Join-Path $runner 'PulseReader.InputSequenceTests.exe') -Destination (Join-Path $new 'PulseReader.exe') -Force
    }
    $futureInventory = Get-Content -LiteralPath (Join-Path $new 'dependency-inventory.json') -Raw | ConvertFrom-Json
    $futureInventory.AppVersion = '1.0.1'
    $futureInventory.LocalFrontend.DllSha256 = (Get-FileHash -LiteralPath (Join-Path $new 'MisakiSharp.dll') -Algorithm SHA256).Hash
    Write-Json (Join-Path $new 'dependency-inventory.json') $futureInventory
    $terms = "Disposable qualification fixture: changed 1.0.1 distribution terms.`r`n`r`n" +
        [IO.File]::ReadAllText((Join-Path $new 'Distribution-Terms.txt'))
    [IO.File]::WriteAllText((Join-Path $new 'Distribution-Terms.txt'), $terms, [Text.UTF8Encoding]::new($false))
    $termsHash = (Get-FileHash -LiteralPath (Join-Path $new 'Distribution-Terms.txt') -Algorithm SHA256).Hash
    Write-Json (Join-Path $new 'distribution-terms.json') @{ SchemaVersion = 1; TermsFileName = 'Distribution-Terms.txt'; TermsSha256 = $termsHash }
    Write-PayloadList $new
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $inputs ('PulseReader-1.0.1-' + $Variant + '-Portable.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($new, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    if ($InstallerQualification) {
        if (-not $IsccPath) { $IsccPath = Join-Path $env:LOCALAPPDATA 'Programs\InnoSetup6\ISCC.exe' }
        Assert-Condition (Test-Path -LiteralPath $IsccPath -PathType Leaf) 'Installer qualification requires the existing Inno compiler.'
        Copy-Tree $new (Join-Path $inputs 'NewPayload')
        Copy-Item -LiteralPath (Join-Path $new 'Distribution-Terms.txt') -Destination (Join-Path $inputs 'UpdateTerms.txt')
        foreach ($entry in @(@{ Version = '1.0.0'; Payload = $old }, @{ Version = '1.0.1'; Payload = $new })) {
            $compiledTerms = (Get-FileHash -LiteralPath (Join-Path $entry.Payload 'Distribution-Terms.txt') -Algorithm SHA256).Hash
            & $IsccPath '/Qp' ('/DAppVersion=' + $entry.Version) ('/DPayloadDir=' + $entry.Payload) ('/DReleaseDir=' + $inputs) `
                ('/DVariantName=' + $Variant) ('/DDistributionTermsSha256=' + $compiledTerms) ('/DSelfContained=' + [int]$selfContained) `
                (Join-Path $repositoryRoot 'installer\PulseReader.iss')
            Assert-Condition ($LASTEXITCODE -eq 0) ('Synthetic installer compile failed for ' + $entry.Version)
        }
    }
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $inputs 'test-update-in-sandbox.ps1')
    $oldHash = (Get-FileHash -LiteralPath (Join-Path $old 'PulseReader.dll') -Algorithm SHA256).Hash
    $newHash = (Get-FileHash -LiteralPath (Join-Path $new 'PulseReader.dll') -Algorithm SHA256).Hash
    Write-Json (Join-Path $inputs 'qualification-input.json') @{
        CandidateDirectory = $candidate; CandidateVersion = '1.0.0'; UpdateVersion = '1.0.1'; Variant = $Variant
        QualificationKind = $(if ($InstallerQualification) { 'Installer' } else { 'Portable' })
        InteractiveQualification = [bool]$InteractiveQualification
        ShippingRelaunch = [bool]$ShippingRelaunch
        PhysicalCancelProbe = [bool]$PhysicalCancelProbe
        OldDllSha256 = $oldHash; NewDllSha256 = $newHash; TermsSha256 = $termsHash
        PackageSha256 = (Get-FileHash -LiteralPath $(if ($InstallerQualification) { Join-Path $inputs ('PulseReader-1.0.1-' + $Variant + '-Setup.exe') } else { $zip }) -Algorithm SHA256).Hash
        DesktopRuntimeSha256 = $(if ($selfContained) { $null } else { (Get-FileHash -LiteralPath (Join-Path $inputs 'windowsdesktop-runtime-win-x64.exe') -Algorithm SHA256).Hash })
        FixtureApphost = 'Renamed test runner; real unchanged candidate PulseReader.dll is used for the old helper'
        Networking = 'Disabled in guest; synthetic metadata retains fixed repository/name/version bounds'
    }
    if ($MachineElevationPayload) {
        Write-Output "Prepared machine-scope fixture payloads only: $taskOutput"
        Write-Output 'Use only with the UAC-enabled disposable guest and dedicated machine setup/controller.'
        return
    }
    $escapeInputs = [Security.SecurityElement]::Escape($inputs)
    $escapeReports = [Security.SecurityElement]::Escape($reports)
    $guestMode = if ($InstallerQualification) { ' -InstallerQualification' } else { '' }
    if ($InteractiveQualification) { $guestMode += ' -InteractiveQualification' }
    $wsb = @"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$escapeInputs</HostFolder><SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\Inputs</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$escapeReports</HostFolder><SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\Reports</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -WindowStyle Hidden -File C:\Users\WDAGUtilityAccount\Desktop\Inputs\test-update-in-sandbox.ps1 -Guest$guestMode</Command></LogonCommand>
</Configuration>
"@
    [IO.File]::WriteAllText((Join-Path $taskOutput 'UpdateQualification.wsb'), $wsb, [Text.UTF8Encoding]::new($false))
    Write-Output "Prepared isolated qualification: $taskOutput"
    Write-Output 'Sandbox was not launched. Review the guest script and approve the exact disposable fixture first.'
    return
}

# Every account, registry, ACL and application mutation below runs exclusively
# in this disposable guest. Host mappings are inputs and exported reports only.
Assert-Condition ($env:USERNAME -eq 'WDAGUtilityAccount' -and $env:USERPROFILE -eq 'C:\Users\WDAGUtilityAccount') 'Guest qualification is restricted to Windows Sandbox.'
$inputs = 'C:\Users\WDAGUtilityAccount\Desktop\Inputs'
$mappedReports = 'C:\Users\WDAGUtilityAccount\Desktop\Reports'
Assert-Condition ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -eq $inputs) 'Guest script must come from the fixed read-only Inputs mapping.'
Assert-Condition (Test-Path -LiteralPath $mappedReports -PathType Container) 'Reports mapping is required.'
$guestRoot = 'C:\PulseReaderUpdateQualification'
$localInputs = Join-Path $guestRoot 'Inputs'
$localReports = Join-Path $guestRoot 'Reports'
$account = 'PulseUpdateFixture'
$stage = 'Guest preflight'
function Export-Reports {
    if (Test-Path -LiteralPath $localReports -PathType Container) {
        # The fixture publishes JSON by atomic rename. Its temporary files are
        # not reports and can disappear between enumeration and Copy-Item.
        foreach ($entry in Get-ChildItem -LiteralPath $localReports -File | Where-Object Name -NotMatch '\.tmp-[0-9a-fA-F]{32}$') {
            Copy-Item -LiteralPath $entry.FullName -Destination $mappedReports -Force
        }
    }
}
function Guest-Stage([string]$Value) {
    $script:stage = $Value
    Write-Json (Join-Path $mappedReports 'guest-stage.json') @{ Stage = $Value; Utc = [DateTime]::UtcNow.ToString('O') }
}
try {
    Guest-Stage 'Copying guest-only inputs and creating disposable standard account'
    Assert-Condition (-not (Test-Path -LiteralPath $guestRoot)) 'Guest fixture root already exists; use a fresh Sandbox.'
    [IO.Directory]::CreateDirectory($guestRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $guestRoot 'guest-only.marker'), 'Disposable Windows Sandbox qualification only.')
    Copy-Tree $inputs $localInputs
    [IO.Directory]::CreateDirectory($localReports) | Out-Null
    $qualificationInput = Get-Content -LiteralPath (Join-Path $localInputs 'qualification-input.json') -Raw | ConvertFrom-Json
    $guestVariant = [string]$qualificationInput.Variant
    Assert-Condition ($guestVariant -in @('win-x64', 'win-x64-framework-dependent')) 'Unsupported guest package variant.'
    if ($guestVariant -eq 'win-x64-framework-dependent') {
        Guest-Stage 'Installing the verified Desktop Runtime only inside the disposable guest'
        $runtimePath = Join-Path $localInputs 'windowsdesktop-runtime-win-x64.exe'
        $runtimeSignature = Get-AuthenticodeSignature -LiteralPath $runtimePath
        Assert-Condition ($runtimeSignature.Status -eq 'Valid' -and $runtimeSignature.SignerCertificate.Subject -match 'Microsoft Corporation') 'Guest runtime installer signature is invalid.'
        Assert-Condition ((Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash -eq $qualificationInput.DesktopRuntimeSha256) 'Guest runtime installer hash changed.'
        $runtime = Start-Process -FilePath $runtimePath -ArgumentList @('/install', '/quiet', '/norestart', '/log', (Join-Path $localReports 'desktop-runtime-install.log')) -WindowStyle Hidden -PassThru
        $null = $runtime.Handle
        Assert-Condition ($runtime.WaitForExit(600000)) 'Guest-only Desktop Runtime installation timed out; no process was killed.'
        $runtime.Refresh()
        Assert-Condition ($runtime.ExitCode -in @(0, 3010)) ('Guest-only Desktop Runtime installation failed: ' + $runtime.ExitCode)
    }
    $password = ConvertTo-SecureString ([Guid]::NewGuid().ToString('N') + 'aA!7') -AsPlainText -Force
    New-LocalUser -Name $account -Password $password -PasswordNeverExpires -AccountNeverExpires -Description 'Disposable Pulse Reader update fixture' | Out-Null
    $usersName = ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
    Add-LocalGroupMember -Group $usersName -Member $account
    $acl = Get-Acl -LiteralPath $guestRoot
    $readRule = [Security.AccessControl.FileSystemAccessRule]::new($account, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($readRule)
    Set-Acl -LiteralPath $guestRoot -AclObject $acl
    $reportAcl = Get-Acl -LiteralPath $localReports
    $reportRule = [Security.AccessControl.FileSystemAccessRule]::new($account, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $reportAcl.AddAccessRule($reportRule)
    Set-Acl -LiteralPath $localReports -AclObject $reportAcl
    $credential = [Management.Automation.PSCredential]::new($account, $password)
    $secondaryLogon = Get-Service -Name seclogon
    if ($secondaryLogon.Status -ne 'Running') { Start-Service -Name seclogon }
    Guest-Stage 'Starting real helper qualification under disposable standard user'
    $runnerExe = Join-Path $localInputs 'Controller\PulseReader.InputSequenceTests.exe'
    if ($InstallerQualification) {
        $initialSetup = Join-Path $localInputs ('PulseReader-1.0.0-' + $guestVariant + '-Setup.exe')
        $package = Join-Path $localInputs ('PulseReader-1.0.1-' + $guestVariant + '-Setup.exe')
        $arguments = if ($InteractiveQualification) {
            '--sandbox-update-ui-controller Installer "' + $initialSetup + '" "' + $package + '" "' + $localReports + '"'
        } else {
            '--sandbox-installer-controller "' + $initialSetup + '" "' + $package + '" "' + $localReports + '"'
        }
    }
    else {
        $oldPayload = Join-Path $localInputs 'OldPayload'
        $package = Join-Path $localInputs ('PulseReader-1.0.1-' + $guestVariant + '-Portable.zip')
        $arguments = if ($InteractiveQualification) {
            '--sandbox-update-ui-controller Portable "' + $oldPayload + '" "' + $package + '" "' + $localReports + '"'
        } else {
            '--sandbox-update-controller "' + $oldPayload + '" "' + $package + '" "' + $localReports + '"'
        }
    }
    $process = Start-Process -FilePath $runnerExe -ArgumentList $arguments -WorkingDirectory ([IO.Path]::GetDirectoryName($runnerExe)) `
        -Credential $credential -LoadUserProfile -WindowStyle Hidden -PassThru
    $null = $process.Handle
    $deadline = [DateTime]::UtcNow.AddMinutes(15)
    while (-not $process.WaitForExit(2000)) {
        Export-Reports
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Guest live qualification timed out; processes were not killed.' }
    }
    $process.Refresh()
    Export-Reports
    Assert-Condition ($process.ExitCode -eq 0) "Guest controller failed with exit code $($process.ExitCode)."
    $resultName = if ($InteractiveQualification) { 'interactive-result.json' } else { 'result.json' }
    $result = Get-Content -LiteralPath (Join-Path $localReports $resultName) -Raw | ConvertFrom-Json
    Assert-Condition $result.Passed 'Guest qualification result reports failure.'
    Guest-Stage $(if ($InteractiveQualification) { 'PASS: automated updater UI flow completed' } else { 'PASS: live helper and retained recovery qualification completed' })
}
catch {
    Export-Reports
    Write-Json (Join-Path $mappedReports 'guest-error.json') @{ Stage = $stage; Error = $_.Exception.ToString(); Utc = [DateTime]::UtcNow.ToString('O') }
    throw
}
