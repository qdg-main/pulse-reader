param([string]$InstallerScriptPath = (Join-Path $PSScriptRoot '..\..\installer\PulseReader.iss'))

$ErrorActionPreference = 'Stop'
$installerSource = [IO.File]::ReadAllText([IO.Path]::GetFullPath($InstallerScriptPath))
$prefix = [regex]::Match($installerSource, '(?ms)^  Script := (?<Expression>.*?);\r?\n  \{ Parents')
$ancestor = [regex]::Match($installerSource, '(?ms)^  ParentScript := Script \+ (?<Expression>.*?);\r?\n  if not Exec')
$tree = [regex]::Match($installerSource, '(?ms)^  Script := Script \+ (?<Expression>.*?);\r?\n  if not Exec')
if (-not $prefix.Success -or -not $ancestor.Success -or -not $tree.Success) { throw 'Cannot extract the actual installer ACL policies.' }

function Convert-PascalPolicyExpression([string]$Expression) {
    $builder = [Text.StringBuilder]::new()
    $tokens = [regex]::Matches($Expression, '''(?<Literal>(?:''''|[^''])*)''|(?<Volume>\bVolumeRoot\b)|(?<Parent>\bQuotedParent\b)|(?<Root>\bQuotedRoot\b)')
    foreach ($token in $tokens) {
        if ($token.Groups['Literal'].Success) { [void]$builder.Append($token.Groups['Literal'].Value.Replace("''", "'")) }
        elseif ($token.Groups['Volume'].Success) { [void]$builder.Append('C:\') }
        elseif ($token.Groups['Root'].Success) { [void]$builder.Append('C:\Program Files\PulseReader') }
        else { [void]$builder.Append('C:\Program Files') }
    }
    $builder.ToString()
}
$policy = [scriptblock]::Create((Convert-PascalPolicyExpression $prefix.Groups['Expression'].Value) +
    (Convert-PascalPolicyExpression $ancestor.Groups['Expression'].Value))
$treePolicy = [scriptblock]::Create((Convert-PascalPolicyExpression $prefix.Groups['Expression'].Value) +
    (Convert-PascalPolicyExpression $tree.Groups['Expression'].Value))

# These injected objects replace filesystem/ACL reads. No host ACLs are read or changed.
function Get-Item { param([string]$LiteralPath, [switch]$Force) $script:FixtureItems[$LiteralPath] }
function Get-Acl { param([string]$LiteralPath) $script:FixtureAcls[$LiteralPath] }
function Get-ChildItem { param([string]$LiteralPath, [switch]$Recurse, [switch]$Force) $script:FixtureDescendantItems }
function New-FixtureAcl([string]$Owner, [object[]]$Rules) {
    $acl = [pscustomobject]@{ OwnerSid = $Owner; Rules = $Rules }
    $acl | Add-Member ScriptMethod GetOwner { param($Type) [pscustomobject]@{ Value = $this.OwnerSid } }
    $acl | Add-Member ScriptMethod GetAccessRules { param($Explicit, $Inherited, $Type) $this.Rules }
    $acl
}
function New-FixtureRule([int]$Rights, [int]$Propagation = 0, [string]$Sid = 'S-1-5-11') {
    [pscustomobject]@{
        IdentityReference = [pscustomobject]@{ Value = $Sid }
        FileSystemRights = [Enum]::ToObject([Security.AccessControl.FileSystemRights], $Rights)
        AccessControlType = [Security.AccessControl.AccessControlType]::Allow
        PropagationFlags = [Security.AccessControl.PropagationFlags]$Propagation
    }
}

function Test-TreePolicyCase([string]$Name, [object[]]$RootRules, [object[]]$ChildRules, [bool]$ExpectAllowed) {
    $rootPath = 'C:\Program Files\PulseReader'
    $childPath = $rootPath + '\model.onnx'
    $script:FixtureItems = @{ $rootPath = [pscustomobject]@{ FullName = $rootPath } }
    $script:FixtureDescendantItems = @([pscustomobject]@{ FullName = $childPath })
    $script:FixtureAcls = @{
        $rootPath = (New-FixtureAcl 'S-1-5-18' $RootRules)
        $childPath = (New-FixtureAcl 'S-1-5-18' $ChildRules)
    }
    $allowed = $true
    $failure = $null
    try { & $treePolicy }
    catch { $allowed = $false; $failure = $_.Exception.Message }
    if ($allowed -ne $ExpectAllowed) { throw "${Name}: expected allowed=$ExpectAllowed, actual=$allowed. $failure" }
    Write-Output "PASS: $Name"
}
function Test-PolicyCase([string]$Name, [object[]]$RootRules, [object[]]$ParentRules,
    [bool]$ExpectAllowed, [string]$RootOwner = 'S-1-5-18', [string]$RootPath = 'C:\') {
    $root = [pscustomobject]@{ FullName = $RootPath; Parent = $null }
    $parent = [pscustomobject]@{ FullName = 'C:\Program Files'; Parent = $root }
    $script:FixtureItems = @{ 'C:\Program Files' = $parent }
    $script:FixtureAcls = @{
        'C:\Program Files' = (New-FixtureAcl 'S-1-5-18' $ParentRules)
        $RootPath = (New-FixtureAcl $RootOwner $RootRules)
    }
    $allowed = $true
    $failure = $null
    try { & $policy }
    catch { $allowed = $false; $failure = $_.Exception.Message }
    if ($allowed -ne $ExpectAllowed) { throw "${Name}: expected allowed=$ExpectAllowed, actual=$allowed. $failure" }
    Write-Output "PASS: $Name"
}

# Actual Sandbox root ACE: Modify+Synchronize, including DELETE but excluding DELETE_CHILD.
Test-PolicyCase 'Guest volume-root Modify ACE permits the protected install' @((New-FixtureRule 1245631)) @() $true
Test-PolicyCase 'The same Modify ACE on Program Files remains rejected' @() @((New-FixtureRule 1245631)) $false
Test-PolicyCase 'Volume-root DELETE_CHILD remains rejected' @((New-FixtureRule 64)) @() $false
Test-PolicyCase 'Volume-root WRITE_DAC remains rejected' @((New-FixtureRule 262144)) @() $false
Test-PolicyCase 'Volume-root TAKE_OWNER remains rejected' @((New-FixtureRule 524288)) @() $false
Test-PolicyCase 'Volume-root untrusted ownership remains rejected' @() @() $false 'S-1-5-11'
Test-PolicyCase 'Inherited-only root Modify does not grant ancestor rights' @((New-FixtureRule 1245631 2)) @() $true
Test-PolicyCase 'Create-child permissions on a protected ancestor remain allowed' @() @((New-FixtureRule 6)) $true
Test-PolicyCase 'A different drive-letter root is not attested' @((New-FixtureRule 65536)) @() $false 'S-1-5-18' 'X:\'
Test-PolicyCase 'UNC share root DELETE is not exempt' @((New-FixtureRule 65536)) @() $false 'S-1-5-18' '\\server\share\'
Test-PolicyCase 'Effective generic-all on volume root remains rejected' @((New-FixtureRule 268435456)) @() $false
Test-PolicyCase 'Generic-write on an ancestor does not imply deletion authority' @() @((New-FixtureRule 1073741824)) $true
Test-TreePolicyCase 'A read-only trusted existing tree remains allowed' @((New-FixtureRule 1179817)) @((New-FixtureRule 1179817)) $true
Test-TreePolicyCase 'Ordinary-user generic-all inheritable-only root ACE is rejected' @((New-FixtureRule 268435456 2 'S-1-5-32-545')) @() $false
Test-TreePolicyCase 'Ordinary-user generic-write descendant ACE is rejected' @() @((New-FixtureRule 1073741824)) $false
Test-TreePolicyCase 'Ordinary-user generic-all descendant ACE is rejected' @() @((New-FixtureRule 268435456)) $false
Test-TreePolicyCase 'Administrator generic-all remains allowed' @((New-FixtureRule 268435456 0 'S-1-5-32-544')) @() $true
Write-Output 'PASS: actual embedded installer ancestor and existing-tree policies passed all injected-ACL regressions.'
