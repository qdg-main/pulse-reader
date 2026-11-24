#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PayloadDirectory,
    [Parameter(Mandatory)] [string]$ExpectedVersion,
    [Parameter(Mandatory)] [bool]$SelfContained,
    [switch]$FrontendOnly,
    [switch]$RecordFrontendBuild,
    [string]$FrontendSourceSnapshot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-FrontendTypeName($Reader, $Handle) {
    $definition = $Reader.GetTypeDefinition($Handle)
    $name = $Reader.GetString($definition.Name)
    $declaring = $definition.GetDeclaringType()
    if (-not $declaring.IsNil) { return (Get-FrontendTypeName $Reader $declaring) + '+' + $name }
    $namespace = $Reader.GetString($definition.Namespace)
    if ($namespace) { return $namespace + '.' + $name }
    return $name
}
function Get-FrontendBinaryInventory([string]$Path, $Contract) {
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($hash -eq $Contract.sourceBase.rejectedStockDllSha256) { throw 'Stock upstream MisakiSharp.dll is not a distributable Pulse Reader frontend.' }
    $null = [Reflection.Assembly]::Load('System.Reflection.Metadata')
    $stream = [IO.File]::OpenRead($Path)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if (-not $pe.HasMetadata) { throw 'Frontend is not a managed assembly.' }
        $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        if (-not $reader.IsAssembly) { throw 'Frontend metadata is not an assembly.' }
        $assembly = $reader.GetAssemblyDefinition()
        if ($reader.GetString($assembly.Name) -ne $Contract.assemblyName -or $assembly.Version.ToString() -ne $Contract.assemblyVersion) { throw 'Unexpected frontend assembly identity.' }
        $attributes = @{}
        $patchMarker = $null
        foreach ($handle in $assembly.GetCustomAttributes()) {
            $attribute = $reader.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind.ToString() -ne 'MemberReference') { continue }
            $memberHandle = [Reflection.Metadata.Ecma335.MetadataTokens]::MemberReferenceHandle([Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber($attribute.Constructor))
            $member = $reader.GetMemberReference($memberHandle)
            if ($member.Parent.Kind.ToString() -ne 'TypeReference') { continue }
            $typeHandle = [Reflection.Metadata.Ecma335.MetadataTokens]::TypeReferenceHandle([Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber($member.Parent))
            $attributeType = $reader.GetTypeReference($typeHandle)
            if ($reader.GetString($attributeType.Namespace) -ne 'System.Reflection') { continue }
            $attributeName = $reader.GetString($attributeType.Name)
            if ($attributeName -notin @('AssemblyMetadataAttribute', 'AssemblyFileVersionAttribute', 'AssemblyInformationalVersionAttribute')) { continue }
            $blob = $reader.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -ne 1) { throw 'Invalid frontend assembly attribute.' }
            $value = $blob.ReadSerializedString()
            if ($attributeName -eq 'AssemblyMetadataAttribute') {
                if ($value -eq $Contract.assemblyMetadata.key) { $patchMarker = $blob.ReadSerializedString() }
            } else { $attributes[$attributeName] = $value }
        }
        if ($patchMarker -ne $Contract.assemblyMetadata.value) { throw 'Frontend patch marker is absent or incorrect.' }
        if ($attributes['AssemblyFileVersionAttribute'] -ne $Contract.fileVersion) { throw 'Frontend file version does not identify the local replacement.' }
        $informationalVersion = [string]$attributes['AssemblyInformationalVersionAttribute']
        if ($informationalVersion -ne $Contract.informationalVersionPrefix -and -not $informationalVersion.StartsWith($Contract.informationalVersionPrefix + '+', [StringComparison]::Ordinal)) { throw 'Unexpected frontend informational version.' }
        $methodsByType = @{}
        foreach ($handle in $reader.TypeDefinitions) {
            $name = Get-FrontendTypeName $reader $handle
            foreach ($pattern in $Contract.forbiddenTypeNamePatterns) { if ($name -match $pattern) { throw "Removed frontend helper type is still present: $name" } }
            $definition = $reader.GetTypeDefinition($handle)
            $methodsByType[$name] = @($definition.GetMethods() | ForEach-Object { $reader.GetString($reader.GetMethodDefinition($_).Name) })
        }
        foreach ($helper in $Contract.requiredHelpers) {
            if (-not $methodsByType.ContainsKey($helper.typeName)) { throw "Required replacement type is missing: $($helper.typeName)" }
            foreach ($method in $helper.methods) { if ($method -notin $methodsByType[$helper.typeName]) { throw "Required replacement method is missing: $($helper.typeName).$method" } }
        }
        foreach ($helper in $Contract.forbiddenMethods) {
            if ($methodsByType.ContainsKey($helper.typeName)) {
                foreach ($method in $helper.methods) { if ($method -in $methodsByType[$helper.typeName]) { throw "Removed frontend helper method is still present: $($helper.typeName).$method" } }
            }
        }
        if ($reader.ManifestResources.Count -ne $Contract.embeddedResources.Count) { throw 'Frontend embedded resource count changed.' }
        $resourcePins = @{}
        foreach ($resource in $Contract.embeddedResources) { $resourcePins[$resource.manifestName] = $resource }
        $resourceBlock = $pe.GetSectionData($pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress)
        $resources = @(foreach ($handle in $reader.ManifestResources) {
            $resource = $reader.GetManifestResource($handle)
            $name = $reader.GetString($resource.Name)
            if (-not $resource.Implementation.IsNil -or -not $resourcePins.ContainsKey($name)) { throw "Unexpected external or unpinned frontend resource: $name" }
            $blob = $resourceBlock.GetReader([int]$resource.Offset, $resourceBlock.Length - [int]$resource.Offset)
            $length = $blob.ReadInt32()
            $bytes = $blob.ReadBytes($length)
            $resourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            if ($resourceHash -ne $resourcePins[$name].sha256 -or $length -ne $resourcePins[$name].length) { throw "Frontend embedded resource checksum mismatch: $name" }
            [ordered]@{ Name = $name; Length = $length; Sha256 = $resourceHash }
        })
        return [ordered]@{
            SchemaVersion = 1; PatchId = $Contract.patchId; AssemblyName = $Contract.assemblyName
            AssemblyVersion = $assembly.Version.ToString(); FileVersion = $attributes['AssemblyFileVersionAttribute']
            InformationalVersion = $informationalVersion; DllSha256 = $hash
            RequiredHelperTypes = @($Contract.requiredHelpers.typeName); Resources = $resources
        }
    } finally { $pe.Dispose(); $stream.Dispose() }
}
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$contractPath = Join-Path $repositoryRoot 'licenses\misaki-pulsereader-source.json'
$frontendContract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
if ($frontendContract.schemaVersion -ne 1 -or $frontendContract.patchId -ne 'number-and-punctuation-v1' -or $frontendContract.embeddedResources.Count -ne 20) { throw 'Unreviewed frontend verification contract.' }
$payloadRoot = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
$cursor = $payloadRoot
while ($cursor) {
    $entry = Get-Item -LiteralPath $cursor -Force
    if (-not $entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unsafe payload ancestor: $cursor" }
    $parent = Split-Path -Parent $cursor
    if ($parent -eq $cursor) { break }
    $cursor = $parent
}
$frontendBinary = Get-FrontendBinaryInventory (Join-Path $payloadRoot 'MisakiSharp.dll') $frontendContract
$frontendContractHash = (Get-FileHash -LiteralPath $contractPath -Algorithm SHA256).Hash
$frontendBuildPath = Join-Path $payloadRoot 'misaki-frontend-build.json'
if ($RecordFrontendBuild) {
    $artifactPrefix = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\') + '\'
    if (-not $payloadRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not $FrontendSourceSnapshot) { throw 'Recording a frontend build requires an artifact payload and its source-asset snapshot.' }
    $snapshotPath = [IO.Path]::GetFullPath($FrontendSourceSnapshot)
    if (-not $snapshotPath.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Frontend source snapshot must be an artifact file.' }
    $snapshotItem = Get-Item -LiteralPath $snapshotPath -Force
    if ($snapshotItem.PSIsContainer -or ($snapshotItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unsafe frontend source snapshot.' }
    $snapshotParent = Split-Path -Parent $snapshotPath
    while ($snapshotParent) {
        if (((Get-Item -LiteralPath $snapshotParent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unsafe frontend source snapshot ancestor.' }
        $nextParent = Split-Path -Parent $snapshotParent
        if ($nextParent -eq $snapshotParent) { break }
        $snapshotParent = $nextParent
    }
    $snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
    if ($snapshot.SchemaVersion -ne 1 -or $snapshot.PatchId -ne $frontendContract.patchId -or $snapshot.Project -ne $frontendContract.localProject -or $snapshot.SourceContractSha256 -ne $frontendContractHash) { throw 'Frontend source snapshot does not match the patch contract.' }
    $sourcePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $snapshot.Files) {
        if ($file.Path -match '(^[\\/]|:|(^|[\\/])\.\.([\\/]|$))' -or -not $sourcePaths.Add($file.Path)) { throw 'Unsafe or duplicate frontend source-snapshot path.' }
        $sourcePath = Join-Path $repositoryRoot $file.Path
        $sourceFile = Get-Item -LiteralPath $sourcePath -Force
        if ($sourceFile.PSIsContainer -or ($sourceFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unsafe frontend source file: $($file.Path)" }
        $sourceParent = Split-Path -Parent $sourcePath
        while ($sourceParent) {
            $parentItem = Get-Item -LiteralPath $sourceParent -Force
            if (($parentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unsafe frontend source ancestor: $sourceParent" }
            $nextParent = Split-Path -Parent $sourceParent
            if ($nextParent -eq $sourceParent) { break }
            $sourceParent = $nextParent
        }
        if ($sourceFile.Length -ne $file.Length -or (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $file.Sha256) { throw "Frontend source changed after source bundling: $($file.Path)" }
    }
    foreach ($requiredPath in @($frontendContract.localProject, 'Directory.Build.props', 'src/MisakiSharp.PulseReader/AssemblyInfo.cs', 'src/MisakiSharp.PulseReader/PermissiveNumberWords.cs', 'src/MisakiSharp.PulseReader/PermissivePunctuation.cs', 'licenses/misaki-pulsereader-source.json') + @($frontendContract.embeddedResources | ForEach-Object { 'src/MisakiSharp.PulseReader/' + $_.file })) {
        if (-not $sourcePaths.Contains($requiredPath)) { throw "Required frontend source is absent from source asset: $requiredPath" }
    }
    $frontendBinary['SourceContractSha256'] = $frontendContractHash
    $frontendBinary['SourceSnapshotSha256'] = (Get-FileHash -LiteralPath $snapshotPath -Algorithm SHA256).Hash
    $frontendBinary['SourceFiles'] = @($snapshot.Files)
    if (Test-Path -LiteralPath $frontendBuildPath) { throw 'Frontend build record already exists; refusing overwrite.' }
    [IO.File]::WriteAllText($frontendBuildPath, ($frontendBinary | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
if ($FrontendOnly) {
    Write-Host "Local frontend verified: $payloadRoot ($($frontendBinary.PatchId), 20 pinned resources)"
    return
}
if (-not (Test-Path -LiteralPath $frontendBuildPath -PathType Leaf)) { throw 'Frontend per-build source/resource record is missing.' }
$frontendBuild = Get-Content -LiteralPath $frontendBuildPath -Raw | ConvertFrom-Json
if ($frontendBuild.SchemaVersion -ne 1 -or $frontendBuild.PatchId -ne $frontendContract.patchId -or $frontendBuild.SourceContractSha256 -ne $frontendContractHash -or $frontendBuild.DllSha256 -ne $frontendBinary.DllSha256 -or $frontendBuild.SourceSnapshotSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Frontend build record does not match the shipped local DLL.' }
if ($frontendBuild.Resources.Count -ne $frontendBinary.Resources.Count) { throw 'Frontend build record resource inventory is incomplete.' }
foreach ($resource in $frontendBinary.Resources) {
    $recorded = @($frontendBuild.Resources | Where-Object Name -eq $resource.Name)
    if ($recorded.Count -ne 1 -or $recorded[0].Length -ne $resource.Length -or $recorded[0].Sha256 -ne $resource.Sha256) { throw "Frontend build record resource mismatch: $($resource.Name)" }
}
foreach ($file in $frontendBuild.SourceFiles) {
    if ($file.Path -match '(^[\\/]|:|(^|[\\/])\.\.([\\/]|$))' -or $file.Sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid frontend per-build source inventory.' }
}
if ($FrontendSourceSnapshot -and (Get-FileHash -LiteralPath $FrontendSourceSnapshot -Algorithm SHA256).Hash -ne $frontendBuild.SourceSnapshotSha256) { throw 'Frontend build record does not match the paired source asset.' }
$pendingDirectories = [Collections.Generic.Queue[string]]::new()
$pendingDirectories.Enqueue($payloadRoot)
$files = [Collections.Generic.List[IO.FileInfo]]::new()
while ($pendingDirectories.Count -gt 0) {
    foreach ($entry in Get-ChildItem -LiteralPath $pendingDirectories.Dequeue() -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in payload: $($entry.FullName)" }
        if ($entry.PSIsContainer) { $pendingDirectories.Enqueue($entry.FullName) } else { $files.Add($entry) }
    }
}
$forbidden = @($files | Where-Object {
    $relative = $_.FullName.Substring($payloadRoot.Length + 1)
    $_.Extension -in '.onnx', '.gguf', '.pdb', '.download' -or
    $relative -match '(?i)(^|\\)(models|contextualpronunciation|optionalruntime|diagnostics|logs|tests|runtime-staging|runtime-backup)(\\|$)' -or
    $_.Name -match '(?i)^(config|settings|installation-manifest|installation-scope)\.json$|^llama.*\.(exe|dll)$|^ggml.*\.(exe|dll)$' -or
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
})
if ($forbidden.Count -gt 0) { throw "Forbidden release payload: $($forbidden.FullName -join ', ')" }
foreach ($name in @('PulseReader.exe', 'PulseReader.dll', 'PulseReader.runtimeconfig.json', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll',
    'LICENSE', 'THIRD-PARTY-NOTICES.md', 'voices\LICENSE', 'native-runtime-manifest.json', 'dependency-inventory.json', 'licenses\source-inventory.json',
    'licenses\dependency-source-manifest.json', 'licenses\misaki-pulsereader-source.json', 'licenses\number-to-words-LICENSE.txt', 'misaki-frontend-build.json',
    'licenses\MisakiSharp-COMPONENT-NOTICES.txt', 'licenses\MisakiSharp-DATA-NOTICE.txt',
    'licenses\Microsoft-VC-Runtime-LICENSE.docx', 'licenses\Microsoft-Visual-Studio-2026-Community-LICENSE.docx')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $name) -PathType Leaf)) { throw "Missing required payload: $name" }
}
$voiceDirectory = Join-Path $payloadRoot 'voices'
foreach ($name in @('Distribution-Terms.txt', 'distribution-terms.json', 'licenses\Microsoft-VC-Runtime-LICENSE.txt', 'licenses\distribution-terms-introduction.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $name) -PathType Leaf)) { throw "Distribution terms are missing: $name" }
}
$contractPath = Join-Path $payloadRoot 'distribution-terms.json'
$termsPath = Join-Path $payloadRoot 'Distribution-Terms.txt'
foreach ($boundedFile in @(@{ Path = $contractPath; Limit = 16384 }, @{ Path = $termsPath; Limit = 262144 })) {
    $item = Get-Item -LiteralPath $boundedFile.Path -Force
    if ($item.Length -le 0 -or $item.Length -gt $boundedFile.Limit -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unsafe or oversized distribution terms input.' }
}
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$contractDocument = [Text.Json.JsonDocument]::Parse($strictUtf8.GetString([IO.File]::ReadAllBytes($contractPath)))
try {
    $contractRoot = $contractDocument.RootElement
    if ($contractRoot.ValueKind.ToString() -ne 'Object') { throw 'Distribution terms contract must be an object.' }
    $contractProperties = @($contractRoot.EnumerateObject())
    if ($contractProperties.Count -ne 3) { throw 'Distribution terms contract must have exactly three properties.' }
    foreach ($propertyName in @('SchemaVersion', 'TermsFileName', 'TermsSha256')) {
        if (@($contractProperties | Where-Object { $_.Name -ceq $propertyName }).Count -ne 1) { throw 'Missing or duplicate distribution terms property.' }
    }
    $schemaProperty = $contractRoot.GetProperty('SchemaVersion')
    $fileProperty = $contractRoot.GetProperty('TermsFileName')
    $hashProperty = $contractRoot.GetProperty('TermsSha256')
    [int]$schemaVersion = 0
    if ($schemaProperty.ValueKind.ToString() -ne 'Number' -or -not $schemaProperty.TryGetInt32([ref]$schemaVersion) -or $schemaVersion -ne 1 -or
        $fileProperty.ValueKind.ToString() -ne 'String' -or $fileProperty.GetString() -cne 'Distribution-Terms.txt' -or
        $hashProperty.ValueKind.ToString() -ne 'String' -or $hashProperty.GetString() -notmatch '^[a-fA-F0-9]{64}$' -or
        $hashProperty.GetString() -ne (Get-FileHash -LiteralPath $termsPath -Algorithm SHA256).Hash) { throw 'Packaged distribution terms do not match the application contract.' }
} finally { $contractDocument.Dispose() }
$termsText = $strictUtf8.GetString([IO.File]::ReadAllBytes($termsPath))
if ($termsText.StartsWith([char]0xFEFF)) { $termsText = $termsText.Substring(1) }
if ([string]::IsNullOrWhiteSpace($termsText) -or $termsText.Contains([char]0)) { throw 'Distribution terms text is empty or contains invalid characters.' }
foreach ($source in @((Join-Path $payloadRoot 'LICENSE'), (Join-Path $payloadRoot 'licenses\distribution-terms-introduction.txt'), (Join-Path $payloadRoot 'licenses\Microsoft-VC-Runtime-LICENSE.txt'))) {
    if (-not $termsText.Contains((Get-Content -LiteralPath $source -Raw).TrimEnd(), [StringComparison]::Ordinal)) { throw 'Packaged agreement omitted or changed an original terms section.' }
}
$voiceFiles = @(Get-ChildItem -LiteralPath $voiceDirectory -File -Filter '*.npy')
if ($voiceFiles.Count -ne 54 -or (Test-Path -LiteralPath (Join-Path $voiceDirectory 'voices-zh'))) { throw 'The payload must contain exactly 54 standard Kokoro v1.0 voices.' }
$exeVersion = (Get-Item -LiteralPath (Join-Path $payloadRoot 'PulseReader.exe')).VersionInfo.ProductVersion
if ($exeVersion.Split('+')[0] -ne $ExpectedVersion) { throw "Executable product version is $exeVersion, expected $ExpectedVersion." }
$nativeManifest = Get-Content -LiteralPath (Join-Path $payloadRoot 'native-runtime-manifest.json') -Raw | ConvertFrom-Json
$expectedNativeFiles = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
if ($nativeManifest.Files.Count -ne 4 -or @(Compare-Object $expectedNativeFiles @($nativeManifest.Files.FileName)).Count -ne 0) {
    throw 'The native runtime manifest must pin the four required x64 CRT files.'
}
foreach ($entry in $nativeManifest.Files) {
    $path = Join-Path $payloadRoot $entry.FileName
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Sha256 -or (Get-Item -LiteralPath $path).Length -ne $entry.Length) {
        throw "Native runtime checksum mismatch: $path"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw "Invalid Microsoft signature in packaged CRT: $path"
    }
}
$inventory = Get-Content -LiteralPath (Join-Path $payloadRoot 'dependency-inventory.json') -Raw | ConvertFrom-Json
if ($inventory.LocalFrontend.PatchId -ne $frontendContract.patchId -or $inventory.LocalFrontend.DllSha256 -ne $frontendBinary.DllSha256 -or $inventory.LocalFrontend.BuildRecord -ne 'misaki-frontend-build.json') { throw 'Dependency inventory does not identify the actual local frontend.' }
$sourceManifest = Get-Content -LiteralPath (Join-Path $payloadRoot 'licenses\dependency-source-manifest.json') -Raw | ConvertFrom-Json
if ($sourceManifest.schemaVersion -ne 2 -or $sourceManifest.library.kind -ne 'local-project' -or $sourceManifest.library.patchId -ne $frontendContract.patchId -or $sourceManifest.library.sourceContractSha256 -ne $frontendContractHash -or
    (Get-FileHash -LiteralPath (Join-Path $payloadRoot 'licenses\misaki-pulsereader-source.json') -Algorithm SHA256).Hash -ne $frontendContractHash) { throw 'Shipped source manifest does not identify the reviewed local frontend.' }
$licenseInventory = Get-Content -LiteralPath (Join-Path $payloadRoot 'licenses\source-inventory.json') -Raw | ConvertFrom-Json
foreach ($entry in $licenseInventory.sources) {
    if ($entry.file -notmatch '^[a-zA-Z0-9._-]+$') { throw "Invalid licence inventory filename: $($entry.file)" }
    $licensePath = Join-Path $payloadRoot ('licenses\' + $entry.file)
    if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw "Inventoried licence missing: $($entry.file)" }
    if ($entry.PSObject.Properties.Name -contains 'packagedSha256') {
        if ((Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash -ne $entry.packagedSha256) { throw "Reviewed licence checksum mismatch: $($entry.file)" }
    }
}
foreach ($manifestName in @('native-runtime-manifest.json', 'dependency-inventory.json', 'misaki-frontend-build.json', 'licenses\source-inventory.json', 'licenses\misaki-pulsereader-source.json')) {
    $manifestText = Get-Content -LiteralPath (Join-Path $payloadRoot $manifestName) -Raw
    if ($manifestText -match '(?i)"[a-z]:[\\/]' -or $manifestText -match '"\\\\\\\\') { throw "Absolute machine path in distributed manifest: $manifestName" }
}
foreach ($entry in @($inventory.NuGetPackages) + @($inventory.BundledRuntimes)) {
    foreach ($license in $entry.LicenseFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot ('licenses\' + $license)) -PathType Leaf)) { throw "Dependency license missing: $license" }
    }
}
$hasCoreRuntime = Test-Path -LiteralPath (Join-Path $payloadRoot 'coreclr.dll') -PathType Leaf
if ($hasCoreRuntime -ne $SelfContained) { throw 'Payload runtime content does not match the selected distribution variant.' }
if ($SelfContained -and $inventory.BundledRuntimes.Count -lt 2) { throw 'Self-contained payload must inventory both .NET and WindowsDesktop runtimes.' }
Write-Host "Payload verified: $payloadRoot ($($files.Count) files, 54 voices)"
