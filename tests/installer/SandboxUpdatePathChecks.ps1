#requires -Version 5.1
param([string]$RepositoryRoot = (Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath($RepositoryRoot)
$preparationScript = Join-Path $repository 'tools\test-update-in-sandbox.ps1'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($preparationScript, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Sandbox preparation script has parser errors.' }
# Load only the production guards and copy routine, not preparation or guest actions.
foreach ($name in @('Assert-Condition', 'Assert-RegularDirectory', 'Assert-RegularTree', 'Assert-DisjointTrees', 'Copy-Tree')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if ($null -eq $definition) { throw "Missing production function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$fixture = Join-Path $repository ('artifacts\sandbox-path-checks-' + [Guid]::NewGuid().ToString('N'))
Assert-RegularDirectory $fixture
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$target = Join-Path $fixture 'redirect-target'
[IO.Directory]::CreateDirectory($target) | Out-Null
[IO.File]::WriteAllText((Join-Path $target 'sentinel.txt'), 'Do not change this file.')

function New-Junction([string]$Path) {
    New-Item -ItemType Junction -Path $Path -Target $target | Out-Null
    if (((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "Fixture did not create a real junction: $Path"
    }
}
function Assert-Rejected([scriptblock]$Action, [string]$ExpectedMessage) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($ExpectedMessage)) {
        throw "Expected rejection '$ExpectedMessage'; received '$failure'."
    }
}
function Assert-PreparationRejected([string]$Candidate, [string]$Output, [string]$Reuse = '',
    [string]$ExpectedMessage = 'Unsafe directory ancestor:') {
    $arguments = @{ CandidateDirectory = $Candidate; OutputRoot = $Output }
    if ($Reuse) { $arguments.ReuseBuildRoot = $Reuse }
    Assert-Rejected { & $preparationScript @arguments } $ExpectedMessage
    if (Test-Path -LiteralPath $Output) { throw "Rejected preparation created its output: $Output" }
}

# Regular paths, including a missing final directory, remain valid.
$regular = Join-Path $fixture 'regular'
[IO.Directory]::CreateDirectory($regular) | Out-Null
[IO.File]::WriteAllText((Join-Path $regular 'example.txt'), 'Regular fixture payload.')
Assert-RegularDirectory (Join-Path $regular 'missing\leaf')
Assert-RegularTree $regular
$copy = Join-Path $fixture 'regular-copy'
Copy-Tree $regular $copy
if ([IO.File]::ReadAllText((Join-Path $copy 'example.txt')) -ne 'Regular fixture payload.') { throw 'Regular copy changed payload bytes.' }

# Compare canonical paths with a separator boundary, not a raw name prefix.
$regularSibling = Join-Path $fixture 'regular-sibling'
Assert-DisjointTrees $regular $regularSibling
Copy-Tree $regular $regularSibling
Assert-Rejected { Assert-DisjointTrees ($regular + '\') $regular.ToUpperInvariant() } 'trees overlap:'
Assert-Rejected { Assert-DisjointTrees $regular (Join-Path $regular 'child\..\nested') } 'trees overlap:'
Assert-Rejected { Assert-DisjointTrees (Join-Path $regular 'nested') $regular } 'trees overlap:'
Assert-Rejected { Copy-Tree $regular $regular } 'trees overlap:'
Assert-Rejected { Copy-Tree $regular (Join-Path $regular 'nested-copy') } 'trees overlap:'
Assert-Rejected { Copy-Tree $regular $fixture } 'trees overlap:'
Assert-Rejected { Copy-Tree ('\\?\' + $regular) (Join-Path $regular 'extended-copy') } 'Device namespace fixture paths are unsupported:'
Assert-Rejected { Copy-Tree $regular ('\\?\' + $regularSibling) } 'Device namespace fixture paths are unsupported:'
Assert-Rejected { Assert-DisjointTrees ('\\.\' + $regular) $regularSibling } 'Device namespace fixture paths are unsupported:'
if (Test-Path -LiteralPath (Join-Path $regular 'nested-copy')) { throw 'Overlapping copy created a destination.' }

$link = Join-Path $fixture 'redirect'
New-Junction $link
Assert-Rejected { Assert-RegularDirectory (Join-Path $link 'missing\leaf') } 'Unsafe directory ancestor:'
Assert-Rejected { Assert-RegularTree $link } 'Unsafe directory ancestor:'
Assert-Rejected { Copy-Tree $regular (Join-Path $link 'copy') } 'Unsafe directory ancestor:'
$fileAncestor = Join-Path $fixture 'file-ancestor'
[IO.File]::WriteAllText($fileAncestor, 'Not a directory.')
Assert-Rejected { Assert-RegularDirectory (Join-Path $fileAncestor 'leaf') } 'Unsafe directory ancestor:'
$nested = Join-Path $fixture 'nested-source'
[IO.Directory]::CreateDirectory($nested) | Out-Null
New-Junction (Join-Path $nested 'link')
Assert-Rejected { Copy-Tree $nested (Join-Path $fixture 'rejected-copy') } 'Fixture source contains a link:'
if (Test-Path -LiteralPath (Join-Path $fixture 'rejected-copy')) { throw 'Linked source created a copy destination.' }

# Run the actual preparation entry point. A linked output is rejected even
# before candidate validation, without invoking dotnet or starting Sandbox.
Assert-PreparationRejected (Join-Path $fixture 'missing-candidate') (Join-Path $link 'output')

# A tiny candidate is enough to reach reused-build preflight. Unsafe sources
# must be rejected before any output folders or build processes are created.
$candidate = Join-Path $fixture 'candidate'
$payload = Join-Path $candidate 'payload\win-x64'
[IO.Directory]::CreateDirectory($payload) | Out-Null
[IO.File]::WriteAllText((Join-Path $payload 'payload-files.txt'), 'dependency-inventory.json')
[IO.File]::WriteAllText((Join-Path $payload 'dependency-inventory.json'), '{"AppVersion":"1.0.0","Variant":"win-x64"}')
Assert-PreparationRejected $candidate (Join-Path $candidate 'new-output') '' 'trees overlap:'
Assert-PreparationRejected $candidate (Join-Path $payload 'new-output') '' 'trees overlap:'
Assert-PreparationRejected $candidate (Join-Path $payload 'unused\..\canonical-output') '' 'trees overlap:'
Assert-PreparationRejected $candidate.ToUpperInvariant() (Join-Path $payload 'mixed-case-output') '' 'trees overlap:'
Assert-PreparationRejected ('\\?\' + $candidate) (Join-Path $candidate 'extended-output') '' 'Device namespace fixture paths are unsupported:'
Assert-PreparationRejected $link (Join-Path $fixture 'candidate-root-output')
Assert-PreparationRejected (Join-Path $link 'candidate') (Join-Path $fixture 'candidate-ancestor-output')
Assert-PreparationRejected $candidate (Join-Path $fixture 'reuse-root-output') $link

$reuse = Join-Path $fixture 'reuse'
[IO.Directory]::CreateDirectory((Join-Path $reuse 'Inputs')) | Out-Null
New-Junction (Join-Path $reuse 'Inputs\Controller')
Assert-PreparationRejected $candidate (Join-Path $fixture 'reuse-controller-output') $reuse
$futureReuse = Join-Path $fixture 'reuse-future'
[IO.Directory]::CreateDirectory((Join-Path $futureReuse 'Inputs\Controller')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $futureReuse 'Build')) | Out-Null
New-Junction (Join-Path $futureReuse 'Build\future-publish')
Assert-PreparationRejected $candidate (Join-Path $fixture 'reuse-future-output') $futureReuse

$regularReuse = Join-Path $fixture 'regular-reuse'
[IO.Directory]::CreateDirectory((Join-Path $regularReuse 'Inputs\Controller')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $regularReuse 'Build\future-publish')) | Out-Null
Assert-PreparationRejected $candidate (Join-Path $regularReuse 'new-output') $regularReuse 'trees overlap:'
Assert-PreparationRejected $candidate (Join-Path $regularReuse 'Inputs\Controller\new-output') $regularReuse 'trees overlap:'
Assert-PreparationRejected $candidate (Join-Path $regularReuse 'Build\future-publish\new-output') $regularReuse 'trees overlap:'
Assert-PreparationRejected $candidate (Join-Path $regularReuse 'extended-output') ('\\?\' + $regularReuse) 'Reused isolated builds must stay below repository artifacts.'
if (@(Get-ChildItem -LiteralPath $payload -Force).Count -ne 2 -or
    @(Get-ChildItem -LiteralPath $regularReuse -Recurse -File -Force).Count -ne 0) {
    throw 'Rejected preparation contaminated candidate or reused build sources.'
}

# Simulate a linked artifacts root without changing the real repository root.
$fakeRepository = Join-Path $fixture 'fake-repository'
[IO.Directory]::CreateDirectory((Join-Path $fakeRepository 'tools')) | Out-Null
$fakeScript = Join-Path $fakeRepository 'tools\test-update-in-sandbox.ps1'
Copy-Item -LiteralPath $preparationScript -Destination $fakeScript
New-Junction (Join-Path $fakeRepository 'artifacts')
Assert-Rejected { & $fakeScript -CandidateDirectory $candidate -OutputRoot (Join-Path $fakeRepository 'artifacts\root-output') } 'Unsafe directory ancestor:'

if ([IO.File]::ReadAllText((Join-Path $target 'sentinel.txt')) -ne 'Do not change this file.' -or
    @(Get-ChildItem -LiteralPath $target -Force).Count -ne 1) { throw 'Rejected paths changed the redirect target.' }
Write-Output 'PASS: regular paths accepted; linked paths and overlapping candidate, reused-build and copy trees rejected before writes.'
Write-Output "Retained isolated fixture (including junctions): $fixture"
