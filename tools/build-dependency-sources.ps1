#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputRoot, [string]$SourceCacheDirectory, [switch]$DownloadMissing)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\') + '\'
$bundleRoot = [IO.Path]::GetFullPath($OutputRoot)
if (-not $SourceCacheDirectory) { $SourceCacheDirectory = Join-Path $repositoryRoot 'artifacts\license-source-audit\code' }
$sourceCache = [IO.Path]::GetFullPath($SourceCacheDirectory)
foreach ($directory in @($repositoryRoot, (Join-Path $repositoryRoot 'licenses'), (Join-Path $repositoryRoot 'docs'))) {
    $item = Get-Item -LiteralPath $directory -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unsafe source input directory: $directory" }
}
foreach ($directory in @($bundleRoot, $sourceCache)) {
    if (-not $directory.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Source output/cache must stay below $artifactPrefix" }
    $cursor = $directory
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unsafe source directory: $cursor" }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
if (Test-Path -LiteralPath $bundleRoot) { throw "Source output already exists: $bundleRoot" }
$manifestPath = Join-Path $repositoryRoot 'licenses\dependency-source-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2 -or $manifest.archives.Count -ne 5 -or $manifest.library.kind -ne 'local-project') { throw 'Unreviewed dependency source manifest.' }
$contractPath = Join-Path $repositoryRoot 'licenses\misaki-pulsereader-source.json'
$contractFile = Get-Item -LiteralPath $contractPath -Force
if ($contractFile.PSIsContainer -or ($contractFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unsafe local frontend contract.' }
$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
if ($contract.schemaVersion -ne 1 -or $contract.localProject -ne 'src/MisakiSharp.PulseReader/MisakiSharp.PulseReader.csproj' -or
    $contract.patchId -ne $manifest.library.patchId -or $contract.sourceBase.sourceRef -ne $manifest.library.sourceRef -or
    (Get-FileHash -LiteralPath $contractPath -Algorithm SHA256).Hash -ne $manifest.library.sourceContractSha256) { throw 'Local frontend contract does not match the reviewed source manifest.' }
$frontendRoot = Join-Path $repositoryRoot 'src\MisakiSharp.PulseReader'
foreach ($directory in @((Join-Path $repositoryRoot 'src'), $frontendRoot, (Join-Path $frontendRoot 'data'))) {
    $item = Get-Item -LiteralPath $directory -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unsafe frontend source directory: $directory" }
}
$frontendFiles = [Collections.Generic.List[IO.FileInfo]]::new()
$pendingFrontendDirectories = [Collections.Generic.Queue[string]]::new()
$pendingFrontendDirectories.Enqueue($frontendRoot)
while ($pendingFrontendDirectories.Count -gt 0) {
    foreach ($item in Get-ChildItem -LiteralPath $pendingFrontendDirectories.Dequeue() -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in frontend source: $($item.FullName)" }
        if ($item.PSIsContainer) {
            if ($item.Name -notin @('bin', 'obj', '.git', '.vs')) { $pendingFrontendDirectories.Enqueue($item.FullName) }
        } else {
            if ($item.Extension -in @('.dll', '.exe', '.pdb', '.onnx', '.gguf', '.download')) { throw "Unexpected generated/model file in frontend source: $($item.FullName)" }
            $frontendFiles.Add($item)
        }
    }
}
if (@(Get-ChildItem -LiteralPath (Join-Path $frontendRoot 'data') -File).Count -ne $contract.embeddedResources.Count -or $contract.embeddedResources.Count -ne 20) { throw 'Expected exactly twenty pinned frontend resources.' }
foreach ($resource in $contract.embeddedResources) {
    if ($resource.file -notmatch '^data/[a-zA-Z0-9._-]+$') { throw 'Unsafe frontend resource path.' }
    $path = Join-Path $frontendRoot $resource.file
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $resource.sha256 -or (Get-Item -LiteralPath $path).Length -ne $resource.length) { throw "Frontend resource pin mismatch: $($resource.file)" }
}
[xml]$versionProps = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$version = [string]$versionProps.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a stable three-part application version.' }
$copyFiles = @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'docs\dependency-source-build.md', 'docs\licensing-microsoft-runtime-review.md', 'docs\licensing-misaki-code-review.md', 'docs\licensing-pronunciation-data-review.md', 'docs\permissive-g2p-replacement-plan.md')
foreach ($relative in $copyFiles) {
    $file = Get-Item -LiteralPath (Join-Path $repositoryRoot $relative) -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unsafe source guide: $relative" }
}
[IO.Directory]::CreateDirectory($sourceCache) | Out-Null
foreach ($archive in $manifest.archives) {
    if ($archive.file -notmatch '^[a-zA-Z0-9._-]+\.(zip|tar\.gz)$' -or $archive.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid source archive metadata.' }
    $uri = [Uri]$archive.url
    if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('codeload.github.com', 'files.pythonhosted.org')) { throw "Unreviewed source host: $uri" }
    $path = Join-Path $sourceCache $archive.file
    if (-not (Test-Path -LiteralPath $path)) {
        if (-not $DownloadMissing) { throw "Required source archive missing: $path. Use -DownloadMissing to retrieve pinned public sources." }
        $temporary = Join-Path $sourceCache ([Guid]::NewGuid().ToString('N') + '.download')
        try {
            Invoke-WebRequest -Uri $uri -OutFile $temporary -TimeoutSec 300
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $archive.sha256) { throw "Downloaded source checksum mismatch: $($archive.file)" }
            # Never overwrite a cache entry created concurrently by another build.
            [IO.File]::Move($temporary, $path)
        }
        finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unsafe source archive: $path" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $archive.sha256) { throw "Cached source checksum mismatch: $($archive.file)" }
}
$contentRoot = Join-Path $bundleRoot 'content'
$archivesRoot = Join-Path $contentRoot 'archives'
[IO.Directory]::CreateDirectory($archivesRoot) | Out-Null
foreach ($archive in $manifest.archives) {
    Copy-Item -LiteralPath (Join-Path $sourceCache $archive.file) -Destination $archivesRoot
    if ((Get-FileHash -LiteralPath (Join-Path $archivesRoot $archive.file) -Algorithm SHA256).Hash -ne $archive.sha256) { throw "Copied source checksum mismatch: $($archive.file)" }
}
foreach ($relative in $copyFiles) {
    $destination = Join-Path $contentRoot $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $relative) -Destination $destination
}
$licensesDestination = Join-Path $contentRoot 'licenses'
[IO.Directory]::CreateDirectory($licensesDestination) | Out-Null
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'licenses') -Force) {
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Expected regular source notice: $($file.FullName)" }
    Copy-Item -LiteralPath $file.FullName -Destination $licensesDestination
}
$localFrontendDestination = Join-Path $contentRoot 'local-frontend'
$frontendSnapshotFiles = @(foreach ($file in $frontendFiles | Sort-Object FullName) {
    $relative = $file.FullName.Substring($repositoryRoot.Length + 1).Replace('\', '/')
    $destination = Join-Path $localFrontendDestination $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $file.FullName -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw "Frontend source changed during copy: $relative" }
    [ordered]@{ Path = $relative; Length = $file.Length; Sha256 = $hash }
})
$frontendContextFiles = @('Directory.Build.props', 'licenses\misaki-pulsereader-source.json') + @($contract.requiredNotices)
foreach ($relative in $frontendContextFiles | Sort-Object -Unique) {
    if ($relative -match '(^[\\/]|:|(^|[\\/])\.\.([\\/]|$))') { throw 'Unsafe frontend build-context path.' }
    $source = Join-Path $repositoryRoot $relative
    $file = Get-Item -LiteralPath $source -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unsafe frontend build-context file: $relative" }
    $destination = Join-Path $localFrontendDestination $relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $source -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw "Frontend build context changed during copy: $relative" }
    $frontendSnapshotFiles += [ordered]@{ Path = $relative.Replace('\', '/'); Length = $file.Length; Sha256 = $hash }
}
$frontendSnapshot = [ordered]@{
    SchemaVersion = 1; PatchId = $contract.patchId; Project = $contract.localProject
    SourceContractSha256 = $manifest.library.sourceContractSha256; Files = @($frontendSnapshotFiles | Sort-Object { $_['Path'] })
}
[IO.File]::WriteAllText((Join-Path $localFrontendDestination 'frontend-source-files.json'), ($frontendSnapshot | ConvertTo-Json -Depth 6) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$bundleInventory = @(Get-ChildItem -LiteralPath $contentRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = $_.FullName.Substring($contentRoot.Length + 1); Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[IO.File]::WriteAllText((Join-Path $contentRoot 'source-files.json'), ($bundleInventory | ConvertTo-Json -Depth 5) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$zipPath = Join-Path $bundleRoot "PulseReader-$version-DependencySources.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($contentRoot, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "Dependency source asset: $zipPath"
