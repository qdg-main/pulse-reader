param([string]$InstallerScriptPath = (Join-Path $PSScriptRoot '..\..\installer\PulseReader.iss'))
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText([IO.Path]::GetFullPath($InstallerScriptPath))
if ($source -notmatch 'CreateCustomForm\(ScaleX\(\d+\), ScaleY\(\d+\), True, True\)') {
    throw 'Custom uninstall form allows WizardSizePercent to grow blank space beyond its fixed controls.'
}
if ($source -notmatch 'ContinueButton\.SetBounds\(Form\.ClientWidth\s*-\s*ScaleX' -or
    $source -notmatch 'CancelButton\.SetBounds\(Form\.ClientWidth\s*-\s*ScaleX') {
    throw 'Uninstall action buttons are not anchored to the client right margin.'
}
if ($source -notmatch 'Description: "Launch Pulse Reader"; Check: CanLaunchAsOriginalUser;.*runasoriginaluser') {
    throw 'Post-install launch availability does not attest the original unelevated user.'
}
if ($source -notmatch 'ExecAsOriginalUser\(' -or $source -notmatch 'WindowsBuiltInRole.*Administrator') {
    throw 'Original-user launch availability has no token-elevation probe.'
}
if ($source -notmatch '\$ErrorActionPreference=' -or $source -notmatch 'catch \{ exit 2 \}') {
    throw 'Original-user token query may treat a failed role check as an unelevated user.'
}
if ($source -match '\[UninstallDelete\][\s\S]*?Type:\s*filesandordirs') {
    throw 'Uninstall empty-container pruning uses recursive file deletion.'
}
foreach ($directory in @('{app}', '{app}\OptionalRuntime', '{commonappdata}\PulseReader', '{commonappdata}\PulseReader\ContextualPronunciation')) {
    $pattern = 'Type: dirifempty; Name: "' + [regex]::Escape($directory) + '"; Check: '
    if ($source -notmatch $pattern) { throw "Missing guarded non-recursive uninstall prune for $directory" }
}
if ([regex]::Matches($source, 'Form\.ShowModal').Count -ne 1 -or $source -match 'ConfirmationEdit|Type DELETE-OTHER-USERS-DATA|ConfirmAllUsersData\s*:=') {
    throw 'Uninstall still requires an additional all-users confirmation page.'
}
if ($source -notmatch '\{param:UNINSTALLDATA\|preserve\}' -or $source -match "Arguments := Arguments \+ ' --confirm-all-users-data'") {
    throw 'Uninstall does not use the selected data choice as its complete confirmation contract.'
}
if ($source -notmatch 'OTHER USERS' -or $source -notmatch 'Logged-out users keep their Windows startup and terms-acceptance records\.') {
    throw 'First-page all-users warning or offline registry limitation is missing.'
}
if ($source -notmatch "'/confirmallusersdata'" -or $source -notmatch 'Obsolete uninstall confirmation parameter') {
    throw 'Uninstall silently accepts the removed confirmation parameter.'
}
Write-Output 'PASS: uninstall form sizing policy, original-user launch attestation and guarded empty-container pruning.'
