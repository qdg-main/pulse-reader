[CmdletBinding()]
param(
    [ValidateSet(
        'qwen3-0.6b',
        'qwen3-1.7b',
        'qwen3-4b-instruct-2507',
        'qwen3.5-4b',
        'qwen3.5-9b',
        'qwen3.8-2b-distill',
        'qwen2.5-1.5b',
        'qwen2.5-3b',
        'smollm2-1.7b')]
    [string]$Model = 'qwen3-0.6b',
    [string]$InstallRoot
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    Join-Path $repositoryRoot 'artifacts\local-text-cleanup'
}
else {
    [System.IO.Path]::GetFullPath($InstallRoot)
}
$runtimeDirectory = Join-Path $artifactRoot 'runtime'
$modelDirectory = Join-Path $artifactRoot 'models'
$runtimeStagingDirectory = Join-Path $artifactRoot 'runtime-staging'
$runtimeBackupDirectory = Join-Path $artifactRoot 'runtime-backup'
$quarantineDirectory = Join-Path $artifactRoot 'quarantine'
$runtimeRevisionFileName = '.source-sha256'
$artifactRootFullPath = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd('\', '/')

if (-not [string]::IsNullOrWhiteSpace($InstallRoot)) {
    $supportedInstallParent = [System.IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA 'PulseReader')).TrimEnd('\', '/')
    $supportedInstallPrefix = $supportedInstallParent + [System.IO.Path]::DirectorySeparatorChar
    if (-not $artifactRootFullPath.StartsWith(
            $supportedInstallPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Custom installs must stay under ${supportedInstallParent}: $artifactRootFullPath"
    }
}

function Assert-NoExistingReparsePoint {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [switch]$SkipLeaf
    )

    $cursor = [System.IO.Path]::GetFullPath($Path)
    if ($SkipLeaf) {
        $cursor = Split-Path -Parent $cursor
    }

    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        try {
            $attributes = [System.IO.File]::GetAttributes($cursor)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to use a path whose existing ancestor is a reparse point: $cursor"
            }
        }
        catch [System.IO.FileNotFoundException] {
            # Continue upward until reaching an existing ancestor.
        }
        catch [System.IO.DirectoryNotFoundException] {
            # Continue upward until reaching an existing ancestor.
        }

        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $cursor) {
            break
        }

        $cursor = $parent
    }
}

Assert-NoExistingReparsePoint -Path $artifactRoot
New-Item -ItemType Directory -Force -Path $artifactRoot, $modelDirectory | Out-Null
Assert-NoExistingReparsePoint -Path $artifactRoot

$runtime = @{
    Url = 'https://github.com/ggml-org/llama.cpp/releases/download/b10662/llama-b10662-bin-win-cpu-x64.zip'
    FileName = 'llama-b10662-bin-win-cpu-x64.zip'
    Sha256 = 'BB56503314A53EEB3044504E44275D5A3B55FD4AC8BB1E33679B40423404B33A'
}

$models = @{
    'qwen3-0.6b' = @{
        Url = 'https://huggingface.co/ggml-org/Qwen3-0.6B-GGUF/resolve/a41486f827d17edd055fe6b3b0ba3f8d427c0519/Qwen3-0.6B-Q4_0.gguf'
        FileName = 'Qwen3-0.6B-Q4_0.gguf'
        Sha256 = '36814D5BAC4EA56910793B7AA07033998E92B75AC8F729012C0B82C5A673141B'
    }
    'qwen3-1.7b' = @{
        Url = 'https://huggingface.co/ggml-org/Qwen3-1.7B-GGUF/resolve/daeb8e2d528a760970442092f6bf1e55c3b659eb/Qwen3-1.7B-Q4_K_M.gguf'
        FileName = 'Qwen3-1.7B-Q4_K_M.gguf'
        Sha256 = 'D2387CA2DBFEE2FFABCE7120D3770DADCA0B293052BC2F0E138FDC940D9BC7B5'
    }
    'qwen3-4b-instruct-2507' = @{
        Url = 'https://huggingface.co/lmstudio-community/Qwen3-4B-Instruct-2507-GGUF/resolve/4edb920b6f14e3b9284d4502a6485103d72cde05/Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
        FileName = 'Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
        Sha256 = '8CDB57CBB880D313736A9BC4E3D3D2485F145B5E19CF33783746E753E82641FC'
    }
    'qwen3.5-4b' = @{
        Url = 'https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/720bb031aae5488eae5d6a78768e6d826662b2ae/Qwen3.5-4B-Q4_K_M.gguf'
        FileName = 'Qwen3.5-4B-Q4_K_M.gguf'
        Sha256 = '00FE7986FF5F6B463E62455821146049DB6F9313603938A70800D1FB69EF11A4'
    }
    'qwen3.5-9b' = @{
        Url = 'https://huggingface.co/bartowski/Qwen_Qwen3.5-9B-GGUF/resolve/2dcd842c59ea5eb119267064550a7a4c592b16c3/Qwen_Qwen3.5-9B-Q4_K_M.gguf'
        FileName = 'Qwen_Qwen3.5-9B-Q4_K_M.gguf'
        Sha256 = 'D784CE9EDA1A5A7B51E8F705A9E6310844BF4F173654D115823C775FDEA56D43'
    }
    'qwen3.8-2b-distill' = @{
        Url = 'https://huggingface.co/empero-ai/Qwen3.8-2B-Distill-GGUF/resolve/f4f73582d0b149595450c719b9a7521a03894f9c/Qwen3.8-2B-Q4_K_M.gguf'
        FileName = 'Qwen3.8-2B-Q4_K_M.gguf'
        Sha256 = '4AA0FB13C431514262F259D420ECC95A8714DF58AC2A2384514E20B93983F0FF'
    }
    'qwen2.5-1.5b' = @{
        Url = 'https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/dd26da4/qwen2.5-1.5b-instruct-q4_k_m.gguf'
        FileName = 'qwen2.5-1.5b-instruct-q4_k_m.gguf'
        Sha256 = '6A1A2EB6D15622BF3C96857206351BA97E1AF16C30D7A74EE38970E434E9407E'
    }
    'qwen2.5-3b' = @{
        Url = 'https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/af75b7aaf5bb163ce4c5dab4e6b84d844e96265d/qwen2.5-3b-instruct-q4_k_m.gguf'
        FileName = 'qwen2.5-3b-instruct-q4_k_m.gguf'
        Sha256 = '626B4A6678B86442240E33DF819E00132D3BA7DDDFE1CDC4FBB18E0A9615C62D'
    }
    'smollm2-1.7b' = @{
        Url = 'https://huggingface.co/HuggingFaceTB/SmolLM2-1.7B-Instruct-GGUF/resolve/6a7e793/smollm2-1.7b-instruct-q4_k_m.gguf'
        FileName = 'smollm2-1.7b-instruct-q4_k_m.gguf'
        Sha256 = 'DECD2598BC2C8ED08C19ADC3C8FDD461EE19ED5708679D1C54EF54A5A30D4F33'
    }
}

function Get-ArtifactChildPath {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [switch]$AllowLeafReparsePoint
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $artifactRootFullPath + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside ${artifactRootFullPath}: $fullPath"
    }

    Assert-NoExistingReparsePoint -Path $fullPath -SkipLeaf:$AllowLeafReparsePoint
    return $fullPath
}

function Assert-NoReparsePointInTree {
    param(
        [Parameter(Mandatory)] [string]$Path
    )

    $safePath = Get-ArtifactChildPath -Path $Path
    if (-not (Test-Path -LiteralPath $safePath -PathType Container)) {
        return
    }

    $pendingDirectories = New-Object 'System.Collections.Generic.Stack[string]'
    $pendingDirectories.Push($safePath)
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $currentDirectory -Force) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to traverse a directory containing a reparse point: $($item.FullName)"
            }

            if ($item.PSIsContainer) {
                $pendingDirectories.Push($item.FullName)
            }
        }
    }
}

function Remove-ArtifactItem {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [switch]$Recurse
    )

    $safePath = Get-ArtifactChildPath -Path $Path
    if (Test-Path -LiteralPath $safePath) {
        if ($Recurse) {
            Assert-NoReparsePointInTree -Path $safePath
        }

        Remove-Item -LiteralPath $safePath -Force -Recurse:$Recurse
    }
}

function Test-ArtifactEntryExists {
    param(
        [Parameter(Mandatory)] [string]$Path
    )

    $safePath = Get-ArtifactChildPath -Path $Path -AllowLeafReparsePoint
    try {
        [void][System.IO.File]::GetAttributes($safePath)
        return $true
    }
    catch [System.IO.FileNotFoundException] {
        return $false
    }
    catch [System.IO.DirectoryNotFoundException] {
        return $false
    }
}

function Test-ArtifactRegularFile {
    param(
        [Parameter(Mandatory)] [string]$Path
    )

    $safePath = Get-ArtifactChildPath -Path $Path -AllowLeafReparsePoint
    try {
        $attributes = [System.IO.File]::GetAttributes($safePath)
    }
    catch [System.IO.FileNotFoundException] {
        return $false
    }
    catch [System.IO.DirectoryNotFoundException] {
        return $false
    }

    $isDirectory = ($attributes -band [System.IO.FileAttributes]::Directory) -ne 0
    $isReparsePoint = ($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    return -not $isDirectory -and -not $isReparsePoint
}

function Test-ArtifactRegularDirectory {
    param(
        [Parameter(Mandatory)] [string]$Path
    )

    $safePath = Get-ArtifactChildPath -Path $Path -AllowLeafReparsePoint
    try {
        $attributes = [System.IO.File]::GetAttributes($safePath)
    }
    catch [System.IO.FileNotFoundException] {
        return $false
    }
    catch [System.IO.DirectoryNotFoundException] {
        return $false
    }

    $isDirectory = ($attributes -band [System.IO.FileAttributes]::Directory) -ne 0
    $isReparsePoint = ($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    return $isDirectory -and -not $isReparsePoint
}

function Move-ArtifactEntryToQuarantine {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Reason
    )

    $safePath = Get-ArtifactChildPath -Path $Path -AllowLeafReparsePoint
    $attributes = [System.IO.File]::GetAttributes($safePath)
    $safeQuarantineDirectory = Get-ArtifactChildPath -Path $quarantineDirectory
    New-Item -ItemType Directory -Force -Path $safeQuarantineDirectory | Out-Null
    $safeQuarantineDirectory = Get-ArtifactChildPath -Path $safeQuarantineDirectory

    $timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $quarantineName = "$(Split-Path -Leaf $safePath).${Reason}.${timestamp}.$([Guid]::NewGuid().ToString('N'))"
    $quarantinePath = Get-ArtifactChildPath -Path (Join-Path $safeQuarantineDirectory $quarantineName)
    if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
        [System.IO.Directory]::Move($safePath, $quarantinePath)
    }
    else {
        [System.IO.File]::Move($safePath, $quarantinePath)
    }

    Write-Warning "Quarantined a ${Reason} cache entry as $quarantinePath"
}

function Get-RuntimeFileManifest {
    param(
        [Parameter(Mandatory)] [string]$Directory
    )

    $safeDirectory = Get-ArtifactChildPath -Path $Directory
    if (-not (Test-Path -LiteralPath $safeDirectory -PathType Container)) {
        throw "Runtime directory was not found: $safeDirectory"
    }

    $manifest = @()
    $pendingDirectories = New-Object 'System.Collections.Generic.Stack[string]'
    $pendingDirectories.Push($safeDirectory)

    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $currentDirectory -Force) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Runtime contents include a reparse point: $($item.FullName)"
            }

            if ($item.PSIsContainer) {
                $pendingDirectories.Push($item.FullName)
                continue
            }

            $relativePath = $item.FullName.Substring($safeDirectory.Length).TrimStart('\', '/')
            if ($relativePath -eq $runtimeRevisionFileName) {
                continue
            }

            $manifest += [pscustomobject]@{
                Path = $relativePath
                Length = $item.Length
                Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            }
        }
    }

    return @($manifest | Sort-Object -Property Path)
}

function Test-RuntimeMatchesManifest {
    param(
        [Parameter(Mandatory)] [string]$Directory,
        [Parameter(Mandatory)] [object[]]$ExpectedManifest
    )

    if (-not (Test-ArtifactEntryExists -Path $Directory) -or
        -not (Test-ArtifactRegularDirectory -Path $Directory)) {
        return $false
    }

    try {
        $actualManifest = @(Get-RuntimeFileManifest -Directory $Directory)
    }
    catch {
        Write-Warning "Could not verify runtime contents at $Directory. $($_.Exception.Message)"
        return $false
    }

    if ($actualManifest.Count -ne $ExpectedManifest.Count) {
        return $false
    }

    for ($index = 0; $index -lt $ExpectedManifest.Count; $index++) {
        $expected = $ExpectedManifest[$index]
        $actual = $actualManifest[$index]
        if ($actual.Path -cne $expected.Path -or
            $actual.Length -ne $expected.Length -or
            $actual.Sha256 -ne $expected.Sha256) {
            return $false
        }
    }

    return $true
}

function Get-ActiveRuntimeProcesses {
    $runtimeRoots = @(
        (Get-ArtifactChildPath -Path $runtimeDirectory -AllowLeafReparsePoint),
        (Get-ArtifactChildPath -Path $runtimeStagingDirectory -AllowLeafReparsePoint),
        (Get-ArtifactChildPath -Path $runtimeBackupDirectory -AllowLeafReparsePoint)
    )

    $activeProcesses = @()
    foreach ($process in Get-Process) {
        try {
            $processPath = $process.Path
        }
        catch {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($processPath)) {
            continue
        }

        $fullProcessPath = [System.IO.Path]::GetFullPath($processPath)
        foreach ($runtimeRoot in $runtimeRoots) {
            $runtimePrefix = $runtimeRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
            if ($fullProcessPath.StartsWith($runtimePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $activeProcesses += $process
                break
            }
        }
    }

    return $activeProcesses
}

function Assert-RuntimeInactive {
    $activeProcesses = @(Get-ActiveRuntimeProcesses)
    if ($activeProcesses.Count -eq 0) {
        return
    }

    $processSummary = ($activeProcesses | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
    throw "The local text-cleanup runtime is active: $processSummary. Stop it and rerun setup; the installed runtime was not changed."
}

function Repair-InterruptedRuntimeSwap {
    param(
        [Parameter(Mandatory)] [object[]]$ExpectedManifest
    )

    Assert-RuntimeInactive

    $runtimeExists = Test-ArtifactEntryExists -Path $runtimeDirectory
    $backupExists = Test-ArtifactEntryExists -Path $runtimeBackupDirectory
    $runtimeIsValid = $runtimeExists -and (Test-RuntimeMatchesManifest -Directory $runtimeDirectory -ExpectedManifest $ExpectedManifest)
    $backupIsValid = $backupExists -and (Test-RuntimeMatchesManifest -Directory $runtimeBackupDirectory -ExpectedManifest $ExpectedManifest)

    if ($runtimeIsValid) {
        if ($backupExists) {
            if ($backupIsValid) {
                Remove-ArtifactItem -Path $runtimeBackupDirectory -Recurse
            }
            else {
                Move-ArtifactEntryToQuarantine -Path $runtimeBackupDirectory -Reason 'invalid-runtime-backup'
            }
        }

        return
    }

    if ($runtimeExists) {
        Move-ArtifactEntryToQuarantine -Path $runtimeDirectory -Reason 'invalid-runtime'
    }

    if ($backupIsValid) {
        try {
            Move-Item -LiteralPath (Get-ArtifactChildPath -Path $runtimeBackupDirectory) -Destination (Get-ArtifactChildPath -Path $runtimeDirectory)
            Write-Warning 'Restored the verified previous runtime after an interrupted swap.'
        }
        catch {
            throw "The invalid runtime was quarantined, but the verified backup could not be restored. The backup remains at $runtimeBackupDirectory. $($_.Exception.Message)"
        }

        return
    }

    if ($backupExists) {
        Move-ArtifactEntryToQuarantine -Path $runtimeBackupDirectory -Reason 'invalid-runtime-backup'
    }
}

function Get-VerifiedFile {
    param(
        [Parameter(Mandatory)] [string]$Url,
        [Parameter(Mandatory)] [string]$Destination,
        [Parameter(Mandatory)] [string]$Sha256
    )

    $Destination = Get-ArtifactChildPath -Path $Destination -AllowLeafReparsePoint
    if (Test-ArtifactEntryExists -Path $Destination) {
        if (-not (Test-ArtifactRegularFile -Path $Destination)) {
            Move-ArtifactEntryToQuarantine -Path $Destination -Reason 'wrong-type'
        }
        else {
            $existingHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
            if ($existingHash -eq $Sha256) {
                Write-Host "Verified existing $(Split-Path -Leaf $Destination)"
                return
            }

            Move-ArtifactEntryToQuarantine -Path $Destination -Reason 'checksum-mismatch'
        }
    }

    $Destination = Get-ArtifactChildPath -Path $Destination
    $downloadPath = Get-ArtifactChildPath -Path "$Destination.download" -AllowLeafReparsePoint
    if (Test-ArtifactEntryExists -Path $downloadPath) {
        if (-not (Test-ArtifactRegularFile -Path $downloadPath)) {
            Move-ArtifactEntryToQuarantine -Path $downloadPath -Reason 'wrong-type'
        }
        else {
            $partialHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
            if ($partialHash -eq $Sha256) {
                [System.IO.File]::Move($downloadPath, $Destination)
                Write-Host "Recovered and verified $(Split-Path -Leaf $Destination) from an interrupted download"
                return
            }

            Remove-ArtifactItem -Path $downloadPath
        }
    }

    $downloadPath = Get-ArtifactChildPath -Path $downloadPath
    try {
        & curl.exe -L --fail --retry 3 --output $downloadPath $Url
        if ($LASTEXITCODE -ne 0) {
            throw "Download failed with curl exit code ${LASTEXITCODE}: $Url"
        }

        if (-not (Test-ArtifactRegularFile -Path $downloadPath)) {
            if (Test-ArtifactEntryExists -Path $downloadPath) {
                Move-ArtifactEntryToQuarantine -Path $downloadPath -Reason 'wrong-type'
            }

            throw "Download did not produce a regular file: $downloadPath"
        }

        $downloadHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
        if ($downloadHash -ne $Sha256) {
            throw "Downloaded file failed checksum verification: $Url"
        }

        [System.IO.File]::Move($downloadPath, $Destination)
        Write-Host "Downloaded and verified $(Split-Path -Leaf $Destination)"
    }
    finally {
        if (Test-ArtifactEntryExists -Path $downloadPath) {
            if (Test-ArtifactRegularFile -Path $downloadPath) {
                Remove-ArtifactItem -Path $downloadPath
            }
            else {
                Move-ArtifactEntryToQuarantine -Path $downloadPath -Reason 'wrong-type'
            }
        }
    }
}

Assert-RuntimeInactive
$runtimeArchive = Join-Path $artifactRoot $runtime.FileName
Get-VerifiedFile -Url $runtime.Url -Destination $runtimeArchive -Sha256 $runtime.Sha256

try {
    if (Test-ArtifactEntryExists -Path $runtimeStagingDirectory) {
        if (-not (Test-ArtifactRegularDirectory -Path $runtimeStagingDirectory)) {
            Move-ArtifactEntryToQuarantine -Path $runtimeStagingDirectory -Reason 'wrong-type'
        }
        else {
            try {
                Remove-ArtifactItem -Path $runtimeStagingDirectory -Recurse
            }
            catch {
                Move-ArtifactEntryToQuarantine -Path $runtimeStagingDirectory -Reason 'unsafe-staging'
            }
        }
    }

    $runtimeStagingDirectory = Get-ArtifactChildPath -Path $runtimeStagingDirectory
    Expand-Archive -LiteralPath $runtimeArchive -DestinationPath $runtimeStagingDirectory -Force
    $expectedRuntimeManifest = @(Get-RuntimeFileManifest -Directory $runtimeStagingDirectory)
    if ($expectedRuntimeManifest.Count -eq 0 -or
        -not ($expectedRuntimeManifest.Path -ccontains 'llama-server.exe')) {
        throw "llama-server.exe was not present after extracting $runtimeArchive"
    }

    $stagedRevisionPath = Join-Path $runtimeStagingDirectory $runtimeRevisionFileName
    Set-Content -LiteralPath $stagedRevisionPath -Value $runtime.Sha256 -NoNewline

    Repair-InterruptedRuntimeSwap -ExpectedManifest $expectedRuntimeManifest
    Assert-RuntimeInactive
    if (Test-RuntimeMatchesManifest -Directory $runtimeDirectory -ExpectedManifest $expectedRuntimeManifest) {
        Write-Host 'Verified existing llama.cpp runtime against the pinned archive manifest.'
    }
    else {
        if (Test-Path -LiteralPath (Get-ArtifactChildPath -Path $runtimeDirectory)) {
            Move-Item -LiteralPath (Get-ArtifactChildPath -Path $runtimeDirectory) -Destination (Get-ArtifactChildPath -Path $runtimeBackupDirectory)
        }

        try {
            Move-Item -LiteralPath (Get-ArtifactChildPath -Path $runtimeStagingDirectory) -Destination (Get-ArtifactChildPath -Path $runtimeDirectory)
            if (-not (Test-RuntimeMatchesManifest -Directory $runtimeDirectory -ExpectedManifest $expectedRuntimeManifest)) {
                throw "The installed runtime did not match the manifest derived from the pinned archive: $runtimeDirectory"
            }
        }
        catch {
            $swapError = $_
            if (Test-Path -LiteralPath (Get-ArtifactChildPath -Path $runtimeDirectory)) {
                if (-not (Test-RuntimeMatchesManifest -Directory $runtimeDirectory -ExpectedManifest $expectedRuntimeManifest)) {
                    Remove-ArtifactItem -Path $runtimeDirectory -Recurse
                }
            }

            if (Test-Path -LiteralPath (Get-ArtifactChildPath -Path $runtimeBackupDirectory)) {
                if (-not (Test-Path -LiteralPath (Get-ArtifactChildPath -Path $runtimeDirectory)) -and
                    (Test-RuntimeMatchesManifest -Directory $runtimeBackupDirectory -ExpectedManifest $expectedRuntimeManifest)) {
                    try {
                        Move-Item -LiteralPath (Get-ArtifactChildPath -Path $runtimeBackupDirectory) -Destination (Get-ArtifactChildPath -Path $runtimeDirectory)
                    }
                    catch {
                        throw "Runtime replacement failed and the verified previous runtime could not be restored. Its backup remains at $runtimeBackupDirectory. Swap error: $($swapError.Exception.Message). Restore error: $($_.Exception.Message)"
                    }
                }
                elseif (-not (Test-Path -LiteralPath (Get-ArtifactChildPath -Path $runtimeDirectory))) {
                    throw "Runtime replacement failed. The previous runtime did not match the pinned archive manifest and remains recoverable at $runtimeBackupDirectory. Swap error: $($swapError.Exception.Message)"
                }
            }

            throw $swapError
        }

        if (Test-Path -LiteralPath (Get-ArtifactChildPath -Path $runtimeBackupDirectory)) {
            if (-not (Test-RuntimeMatchesManifest -Directory $runtimeDirectory -ExpectedManifest $expectedRuntimeManifest)) {
                throw "Refusing to discard the runtime backup because the installed runtime does not match the pinned archive manifest: $runtimeDirectory"
            }

            Remove-ArtifactItem -Path $runtimeBackupDirectory -Recurse
        }
    }
}
finally {
    if (Test-ArtifactEntryExists -Path $runtimeStagingDirectory) {
        try {
            if (Test-ArtifactRegularDirectory -Path $runtimeStagingDirectory) {
                Remove-ArtifactItem -Path $runtimeStagingDirectory -Recurse
            }
            else {
                Move-ArtifactEntryToQuarantine -Path $runtimeStagingDirectory -Reason 'wrong-type'
            }
        }
        catch {
            Write-Warning "Could not clean runtime staging at $runtimeStagingDirectory. A later setup run will retry cleanup. $($_.Exception.Message)"
        }
    }
}

$serverPath = Join-Path $runtimeDirectory 'llama-server.exe'

$selectedModel = $models[$Model]
$modelPath = Join-Path $modelDirectory $selectedModel.FileName
Get-VerifiedFile -Url $selectedModel.Url -Destination $modelPath -Sha256 $selectedModel.Sha256

$installationManifest = [ordered]@{
    SchemaVersion = 1
    RuntimeArchiveSha256 = $runtime.Sha256
    ModelFileName = $selectedModel.FileName
    ModelSha256 = $selectedModel.Sha256
    RuntimeFiles = @($expectedRuntimeManifest | ForEach-Object {
        [ordered]@{
            Path = $_.Path
            Length = $_.Length
            Sha256 = $_.Sha256
        }
    })
}
$manifestPath = Get-ArtifactChildPath -Path (Join-Path $artifactRoot 'installation-manifest.json')
$manifestJson = $installationManifest | ConvertTo-Json -Depth 5 -Compress
[System.IO.File]::WriteAllText(
    $manifestPath,
    $manifestJson,
    [System.Text.UTF8Encoding]::new($false))
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash

Write-Host ''
Write-Host "Local runtime: $serverPath"
Write-Host "Local model:   $modelPath"
Write-Host "Manifest:      $manifestPath"
Write-Host "Manifest SHA:  $manifestHash"
