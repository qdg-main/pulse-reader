#requires -Version 7.4
[CmdletBinding()]
param([string]$OutputRoot, [string]$IsccPath, [string]$VcRedistDirectory, [switch]$SkipInstaller, [switch]$PublishOnly, [switch]$DownloadSources)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectPath = Join-Path $repositoryRoot 'src\PulseReader\PulseReader.csproj'
[xml]$versionProps = Get-Content (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$version = [string]$versionProps.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Directory.Build.props must declare a stable three-part Version.' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $repositoryRoot ('artifacts\release\PulseReader-{0}-{1}' -f $version, (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$releaseRoot = [IO.Path]::GetFullPath($OutputRoot)
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $releaseRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Release output must stay below $artifactPrefix" }
if (Test-Path -LiteralPath $releaseRoot) { throw "Output already exists. Choose a new -OutputRoot: $releaseRoot" }

function Assert-RegularDirectory([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $entry = Get-Item -LiteralPath $cursor -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or -not $entry.PSIsContainer) { throw "Unsafe directory ancestor: $cursor" }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Copy-ReleaseLicenseFiles([string]$SourceDirectory, [string]$DestinationDirectory) {
    Assert-RegularDirectory $SourceDirectory
    Assert-RegularDirectory $DestinationDirectory
    [IO.Directory]::CreateDirectory($DestinationDirectory) | Out-Null
    foreach ($licenseFile in Get-ChildItem -LiteralPath $SourceDirectory -Force) {
        if ($licenseFile.PSIsContainer -or ($licenseFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Expected regular license file: $($licenseFile.FullName)" }
        $destination = Join-Path $DestinationDirectory $licenseFile.Name
        if (Test-Path -LiteralPath $destination) {
            $existing = Get-Item -LiteralPath $destination -Force
            if ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unsafe license destination: $destination" }
        }
        Copy-Item -LiteralPath $licenseFile.FullName -Destination $destination -Force
    }
}
function Get-VcRedistDirectory {
    if ($VcRedistDirectory) { return [IO.Path]::GetFullPath($VcRedistDirectory) }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Supply -VcRedistDirectory: licensed release x64 VC CRT directory.' }
    $installations = @(& $vswhere -all -products '*' -property installationPath)
    if ($LASTEXITCODE -ne 0) { throw 'vswhere failed.' }
    $candidates = @(foreach ($installation in $installations) {
        $root = Join-Path $installation 'VC\Redist\MSVC'
        if (Test-Path -LiteralPath $root) {
            Get-ChildItem $root -Directory | ForEach-Object {
                Get-ChildItem (Join-Path $_.FullName 'x64') -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue
            }
        }
    })
    $candidate = $candidates | Sort-Object { [version]$_.Parent.Parent.Name } -Descending | Select-Object -First 1
    if ($null -eq $candidate) { throw 'No x64 VC CRT redist directory found.' }
    return $candidate.FullName
}
$vcDirectory = Get-VcRedistDirectory
Assert-RegularDirectory $vcDirectory
if ($vcDirectory -notmatch '(?i)\\VC\\Redist\\MSVC\\[^\\]+\\x64\\Microsoft\.VC\d+\.CRT$') { throw 'CRT sources must be release x64 files under licensed Visual Studio VC/Redist/MSVC.' }
$vcManifest = @(foreach ($name in @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    $source = Join-Path $vcDirectory $name
    $file = Get-Item -LiteralPath $source
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse-point CRT file: $source" }
    $signature = Get-AuthenticodeSignature -LiteralPath $source
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') { throw "Invalid Microsoft CRT signature: $source ($($signature.Status))" }
    $bytes = [IO.File]::ReadAllBytes($source)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) { throw "CRT is not AMD64: $source" }
    [ordered]@{ FileName = $name; RedistributionSource = 'Licensed Visual Studio release x64 VC CRT'; FileVersion = $file.VersionInfo.FileVersion; Length = $file.Length
        Sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        Signer = $signature.SignerCertificate.Subject; SignerThumbprint = $signature.SignerCertificate.Thumbprint }
})
$licenseMap = @{
    'Interop.UIAutomationClient/10.19041.0' = @('Interop.UIAutomationClient-LICENSE.txt')
    'KokoroSharp/0.8.4' = @('KokoroSharp-LICENSE.txt', 'Kokoro-Voices-LICENSE.txt')
    'KokoroSharp.CPU/0.8.4' = @('KokoroSharp-LICENSE.txt')
    'Microsoft.ML.OnnxRuntime/1.22.0' = @('ONNXRuntime-LICENSE.txt')
    'Microsoft.ML.OnnxRuntime.Managed/1.22.0' = @('ONNXRuntime-LICENSE.txt')
    'MisakiSharp/2.1.1' = @('MisakiSharp-LICENSE.txt', 'MisakiSharp-NOTICE.txt', 'MisakiSharp-COMPONENT-NOTICES.txt', 'MisakiSharp-component-notices.json', 'number-to-words-LICENSE.txt', 'num2words-LGPL.txt', 'phonemizer-GPL.txt', 'MisakiSharp-DATA-NOTICE.txt', 'CC-BY-SA-4.0.txt', 'wordfreq-LICENSE.txt', 'wordfreq-NOTICE.md', 'wordfreq-README.md', 'UniDic-3.1.0-BSD.txt', 'UniDic-3.1.0-AUTHORS.txt', 'CC-BY-SA-3.0.txt', 'CC0-1.0.txt')
    'NumSharp/0.30.0' = @('NumSharp-LICENSE.txt')
    'OpenTK.Audio.OpenAL/5.0.0-pre.13' = @('OpenTK-LICENSE.txt')
    'OpenTK.Core/5.0.0-pre.13' = @('OpenTK-LICENSE.txt')
    'OpenTK.Mathematics/5.0.0-pre.13' = @('OpenTK-LICENSE.txt')
    'System.Numerics.Tensors/9.0.5' = @('System.Numerics.Tensors-LICENSE.txt')
    'System.Speech/10.0.11' = @('System.Speech-LICENSE.txt')
}
foreach ($name in @('NAudio', 'NAudio.Asio', 'NAudio.Core', 'NAudio.Midi', 'NAudio.Wasapi', 'NAudio.WinForms', 'NAudio.WinMM')) { $licenseMap["$name/2.2.1"] = @('NAudio-LICENSE.txt') }
$licenseRoot = Join-Path $repositoryRoot 'licenses'
foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $name) -PathType Leaf)) { throw "Required release notice missing: $name" }
}
foreach ($name in @($licenseMap.Values | ForEach-Object { $_ }) + @('Microsoft-VC-Runtime-redistribution.txt', 'Microsoft-VC-Runtime-LICENSE.docx', 'Microsoft-VC-Runtime-LICENSE.txt', 'distribution-terms-introduction.txt', 'Microsoft-Visual-Studio-2026-Community-LICENSE.docx', 'source-inventory.json', 'dependency-source-manifest.json', 'misaki-pulsereader-source.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $licenseRoot $name) -PathType Leaf)) { throw "Required dependency license missing: $name" }
}
$sourceInventory = Get-Content (Join-Path $licenseRoot 'source-inventory.json') -Raw | ConvertFrom-Json
foreach ($entry in $sourceInventory.sources) {
    if ($entry.PSObject.Properties.Name -contains 'packagedSha256') {
        if ((Get-FileHash -LiteralPath (Join-Path $licenseRoot $entry.file) -Algorithm SHA256).Hash -ne $entry.packagedSha256) { throw "Reviewed licence checksum mismatch: $($entry.file)" }
    }
}
if (-not $SkipInstaller -and -not $PublishOnly) {
    if (-not $IsccPath) { $IsccPath = Join-Path $env:LOCALAPPDATA 'Programs\InnoSetup6\ISCC.exe' }
    if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw 'Supply -IsccPath, or use -SkipInstaller / -PublishOnly.' }
}
Assert-RegularDirectory $releaseRoot
New-Item -ItemType Directory -Path $releaseRoot | Out-Null
& (Join-Path $PSScriptRoot 'build-dependency-sources.ps1') -OutputRoot (Join-Path $releaseRoot 'dependency-sources') -DownloadMissing:$DownloadSources
$sourceAsset = Get-Item -LiteralPath (Join-Path $releaseRoot "dependency-sources\PulseReader-$version-DependencySources.zip")
Copy-Item -LiteralPath $sourceAsset.FullName -Destination $releaseRoot
$dependencySourceManifest = Get-Content -LiteralPath (Join-Path $licenseRoot 'dependency-source-manifest.json') -Raw | ConvertFrom-Json
if ($dependencySourceManifest.schemaVersion -ne 2 -or $dependencySourceManifest.library.kind -ne 'local-project') { throw 'Release requires the reviewed local frontend source manifest.' }
$frontendSnapshotPath = Join-Path $releaseRoot 'dependency-sources\content\local-frontend\frontend-source-files.json'
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$sdkVersion = & $dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'Could not read .NET SDK version.' }
$variants = @(@{ Name = 'win-x64'; SelfContained = $true }, @{ Name = 'win-x64-framework-dependent'; SelfContained = $false })
$isolatedBuildRoot = Join-Path $releaseRoot 'build'
$isolatedAssetsPath = Join-Path $isolatedBuildRoot 'obj\PulseReader\project.assets.json'
$results = @()
foreach ($variant in $variants) {
    $payload = Join-Path $releaseRoot ('payload\' + $variant.Name)
    New-Item -ItemType Directory -Path $payload | Out-Null
    $publishArgs = @('publish', $projectPath, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', $variant.SelfContained.ToString().ToLowerInvariant(), '--output', $payload, '--artifacts-path', $isolatedBuildRoot,
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false', '-p:DebugType=None', '-p:DebugSymbols=false')
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($variant.Name)" }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE'), (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $payload
    $reviewDocs = Join-Path $payload 'docs'
    New-Item -ItemType Directory -Path $reviewDocs | Out-Null
    foreach ($name in @('dependency-source-build.md', 'licensing-microsoft-runtime-review.md', 'licensing-misaki-code-review.md', 'licensing-pronunciation-data-review.md', 'permissive-g2p-replacement-plan.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot ('docs\' + $name)) -Destination $reviewDocs
    }
    Copy-ReleaseLicenseFiles $licenseRoot (Join-Path $payload 'licenses')
    $distributionTerms = (Get-Content -LiteralPath (Join-Path $licenseRoot 'distribution-terms-introduction.txt') -Raw).TrimEnd() + "`r`n`r`n" +
        (Get-Content -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Raw).TrimEnd() + "`r`n`r`nORIGINAL MICROSOFT RUNTIME TERMS`r`n`r`n" +
        (Get-Content -LiteralPath (Join-Path $licenseRoot 'Microsoft-VC-Runtime-LICENSE.txt') -Raw).TrimEnd() + "`r`n"
    [IO.File]::WriteAllText((Join-Path $payload 'Distribution-Terms.txt'), $distributionTerms, [Text.UTF8Encoding]::new($false))
    $distributionTermsHash = (Get-FileHash -LiteralPath (Join-Path $payload 'Distribution-Terms.txt') -Algorithm SHA256).Hash
    Write-Json (Join-Path $payload 'distribution-terms.json') ([ordered]@{
        SchemaVersion = 1; TermsFileName = 'Distribution-Terms.txt'; TermsSha256 = $distributionTermsHash
    })
    foreach ($nativeFile in $vcManifest) { Copy-Item -LiteralPath (Join-Path $vcDirectory $nativeFile.FileName) -Destination (Join-Path $payload $nativeFile.FileName) }
    Write-Json (Join-Path $payload 'native-runtime-manifest.json') ([ordered]@{ SchemaVersion = 1; Architecture = 'x64'; Files = $vcManifest })
    $assets = Get-Content -LiteralPath $isolatedAssetsPath -Raw | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    foreach ($target in $assets.targets.PSObject.Properties) {
        $stockFrontend = $target.Value.PSObject.Properties['MisakiSharp/2.1.1']
        if ($null -ne $stockFrontend) {
            foreach ($kind in @('compile', 'runtime', 'runtimeTargets')) {
                $runtimeAssets = $stockFrontend.Value.PSObject.Properties[$kind]
                if ($null -ne $runtimeAssets -and @($runtimeAssets.Value.PSObject.Properties.Name | Where-Object { $_ -match '(?i)MisakiSharp\.dll$' }).Count -gt 0) { throw 'Stock MisakiSharp package assets were not excluded from the application.' }
            }
        }
    }
    $resolvedDependencies = @(foreach ($entry in $assets.libraries.PSObject.Properties) {
        if ($entry.Value.type -ne 'package') { continue }
        if (-not $licenseMap.ContainsKey($entry.Name)) { throw "Unreviewed NuGet package/version: $($entry.Name)" }
        $packageRoot = $null
        foreach ($root in $packageRoots) {
            $candidate = Join-Path $root $entry.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageRoot = $candidate; break }
        }
        if ($null -eq $packageRoot) { throw "Resolved package unavailable: $($entry.Name)" }
        $noticeFiles = @(Get-ChildItem $packageRoot -File | Where-Object Name -Match '(?i)third.?party.*notices?')
        if ($entry.Name -match '^(Microsoft\.ML\.OnnxRuntime|System\.Numerics\.Tensors|System\.Speech)/' -and $noticeFiles.Count -eq 0) { throw "Required third-party notices absent: $($entry.Name)" }
        $copiedNotices = @(foreach ($notice in $noticeFiles) {
            $destinationName = ($entry.Name -replace '[/\\]', '-') + '-' + $notice.Name
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $payload ('licenses\' + $destinationName))
            $destinationName
        })
        [ordered]@{
            Package = $entry.Name; NuGetSha512 = $entry.Value.sha512; LicenseFiles = @($licenseMap[$entry.Name]) + $copiedNotices
            RuntimeAssetsExcluded = ($entry.Name -eq 'MisakiSharp/2.1.1')
        }
    })
    $runtimeInventory = @()
    if ($variant.SelfContained) {
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $payload 'PulseReader.runtimeconfig.json') -Raw | ConvertFrom-Json
        $runtimeInventory = @(foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
            $packName = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
            $packRoot = $null
            foreach ($root in $packageRoots) {
                $candidate = Join-Path $root ($packName + '\' + $framework.version)
                if (Test-Path -LiteralPath $candidate -PathType Container) { $packRoot = $candidate; break }
            }
            if ($null -eq $packRoot) { throw "Runtime license pack unavailable: $packName/$($framework.version)" }
            $runtimeLicenses = @(Get-ChildItem -LiteralPath $packRoot -File | Where-Object Name -Match '(?i)^LICENSE(\.TXT)?$|^THIRD-PARTY-NOTICES\.TXT$')
            if ($runtimeLicenses.Count -eq 0) { throw "Runtime license missing: $packRoot" }
            if ($framework.name -eq 'Microsoft.NETCore.App' -and -not ($runtimeLicenses.Name -match '^THIRD-PARTY-NOTICES')) { throw '.NET runtime third-party notices missing.' }
            $copiedRuntimeLicenses = @(foreach ($license in $runtimeLicenses) {
                $destinationName = $packName + '-' + $framework.version + '-' + $license.Name
                Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $payload ('licenses\' + $destinationName))
                $destinationName
            })
            [ordered]@{ Runtime = $framework.name; Version = $framework.version; LicenseFiles = $copiedRuntimeLicenses }
        })
    }
    $payloadSourceInventory = Get-Content -LiteralPath (Join-Path $licenseRoot 'source-inventory.json') -Raw | ConvertFrom-Json
    foreach ($sourceEntry in $payloadSourceInventory.sources) {
        if ([IO.Path]::IsPathRooted([string]$sourceEntry.source)) {
            if ($sourceEntry.file -ne 'LLVM-OpenMP-LICENSE.txt') { throw "Unreviewed local path in distributed license provenance: $($sourceEntry.file)" }
            $sourceEntry.source = 'llama.cpp b10662 CPU x64 release archive: LICENSE-LLVM-OpenMP'
        }
    }
    Write-Json (Join-Path $payload 'licenses\source-inventory.json') $payloadSourceInventory
    Write-Json (Join-Path $payload 'dependency-inventory.json') ([ordered]@{
        SchemaVersion = 1; AppVersion = $version; Variant = $variant.Name; DotNetSdk = [string]$sdkVersion
        PublicReleaseReady = $false; PublicReleaseGates = @($sourceInventory.publicReleaseGates)
        NuGetPackages = $resolvedDependencies; BundledRuntimes = $runtimeInventory
        LocalFrontend = [ordered]@{
            Project = $dependencySourceManifest.library.localProject; PatchId = $dependencySourceManifest.library.patchId
            DllSha256 = (Get-FileHash -LiteralPath (Join-Path $payload 'MisakiSharp.dll') -Algorithm SHA256).Hash
            BuildRecord = 'misaki-frontend-build.json'; SourceContract = 'licenses/misaki-pulsereader-source.json'
        }
        RequiredExternalRuntime = $(if ($variant.SelfContained) { $null } else { '.NET 10 Windows Desktop Runtime x64' })
    })
    & (Join-Path $PSScriptRoot 'release-verify-payload.ps1') -PayloadDirectory $payload -ExpectedVersion $version -SelfContained $variant.SelfContained -RecordFrontendBuild -FrontendSourceSnapshot $frontendSnapshotPath
    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object { $_.FullName.Substring($payload.Length + 1) } | Sort-Object)
    [IO.File]::WriteAllLines((Join-Path $payload 'payload-files.txt'), @($payloadFiles) + 'payload-files.txt', [Text.UTF8Encoding]::new($false))
    $fileManifest = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Path = $_.FullName.Substring($payload.Length + 1); Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    Write-Json (Join-Path $releaseRoot ($variant.Name + '-payload-manifest.json')) $fileManifest
    if (-not $PublishOnly) {
        $zipPath = Join-Path $releaseRoot ("PulseReader-$version-$($variant.Name)-Portable.zip")
        [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
        if (-not $SkipInstaller) {
            $innoArgs = @('/Qp', "/DAppVersion=$version", "/DPayloadDir=$payload", "/DReleaseDir=$releaseRoot", "/DVariantName=$($variant.Name)", "/DDistributionTermsSha256=$distributionTermsHash", ('/DSelfContained=' + [int]$variant.SelfContained), (Join-Path $repositoryRoot 'installer\PulseReader.iss'))
            & $IsccPath @innoArgs
            if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $($variant.Name)" }
        }
    }
    $payloadBytes = [long](($fileManifest | ForEach-Object { [long]$_['Length'] } | Measure-Object -Sum).Sum)
    $results += [ordered]@{ Variant = $variant.Name; Payload = 'payload\' + $variant.Name; FileCount = $fileManifest.Count; Bytes = $payloadBytes }
}
if (-not $PublishOnly -and -not $SkipInstaller) {
    $updatePackages = @(foreach ($variant in $variants) {
        $portableName = "PulseReader-$version-$($variant.Name)-Portable.zip"
        $portableFile = Get-Item -LiteralPath (Join-Path $releaseRoot $portableName)
        $installerName = "PulseReader-$version-$($variant.Name)-Setup.exe"
        $installerInfo = $null
        if (-not $SkipInstaller) {
            $installerFile = Get-Item -LiteralPath (Join-Path $releaseRoot $installerName)
            $installerInfo = [ordered]@{ Name = $installerName; Length = $installerFile.Length; Sha256 = (Get-FileHash -LiteralPath $installerFile.FullName -Algorithm SHA256).Hash }
        }
        [ordered]@{
            Variant = $variant.Name
            Installer = $installerInfo
            Portable = [ordered]@{ Name = $portableName; Length = $portableFile.Length; Sha256 = (Get-FileHash -LiteralPath $portableFile.FullName -Algorithm SHA256).Hash }
        }
    })
    Write-Json (Join-Path $releaseRoot 'update-manifest.json') ([ordered]@{
        SchemaVersion = 1; Version = $version
        TermsSha256 = $distributionTermsHash; TermsText = $distributionTerms
        Packages = $updatePackages
    })
}
$checksums = @(Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object { $_.Extension -in '.zip', '.exe' -or $_.Name -eq 'update-manifest.json' } | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
})
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
Write-Json (Join-Path $releaseRoot 'build-manifest.json') ([ordered]@{
    SchemaVersion = 1; Version = $version; BuiltAtUtc = [DateTime]::UtcNow.ToString('O'); DotNetSdk = [string]$sdkVersion
    InstallerBuilt = (-not $SkipInstaller -and -not $PublishOnly); PortableBuilt = (-not $PublishOnly)
    AppAndInstallerSigned = $false; PublicReleaseReady = $false
    PublicReleaseGates = @($sourceInventory.publicReleaseGates); Variants = $results
    DependencySourceAsset = "PulseReader-$version-DependencySources.zip"
    LocalFrontendPatch = $dependencySourceManifest.library.patchId
    IntermediateBuildOutput = 'build'
})
Write-Warning 'Unsigned local release candidates: public distribution is gated by the dependency license review in build-manifest.json.'
Write-Host "Release candidates: $releaseRoot"
