# Prepare the first release

This guide is for maintainers preparing Pulse Reader 1.0.0. The installer definition and application setup flow are implemented, but distribution is not approved yet. Do not publish a tag or upload a public release until the checks below are complete.

## Remaining before public release

- Bitdefender previously quarantined `tools/build-release.ps1` with a `Heur.BZC.PZQ.Boxter` detection. The user restored it for exact-source review; the completed recipe now builds both package variants. This does not establish an antivirus-clean result. Defer a possible false-positive report until the final packaging state is available. No antivirus exclusions or protection changes are part of this workflow.
- Finish final-package and publication checks. The [30 October qualification](updater-qualification-20261030.md) covers framework-dependent portable/per-user updates, the real shipping startup path, an actual Cancel update click, and self-contained all-users UAC cancellation/approval with a different administrator. Approval passed after a Windows consent-process crash and retry; that failure remains recorded. Public asset delivery is still unavailable for testing, and broader OS/DPI/power-loss coverage remains limited. Earlier candidate records below are historical, not the latest qualification status.
- Obtain explicit approval for the final release assets and publication. On 24 November 2026, the maintainer chose a no-spend, unsigned GitHub Releases preview, with no paid signing, Store submission or alternative distribution. This settles the signing choice, but final antivirus assessment and clean Windows qualification remain gates. `PublicReleaseReady` stays false.

The maintainer accepted all ten frontend listening pairs. The current code/data
and [Microsoft runtime reviews](licensing-microsoft-runtime-review.md) support
the documented notice and scoped agreement route. The maintainer confirmed
individual development and acknowledged the Community publisher condition.
These records do not certify contract enforceability or every contributor's
rights. Keep the reviewed notices and complete modified frontend source with
the approved assets.

Pulse Reader's own code is MIT licensed. That does not replace the licences or obligations of bundled components. The planned GitHub Releases preview will be unsigned. Its download page must identify the recommended installer, supply matching SHA-256 checksums and briefly disclose Windows publisher/reputation warnings. The unsigned decision does not waive antivirus assessment or qualify any package for release.

The [permissive replacement investigation](permissive-g2p-replacement-plan.md)
now records the C# replacement and assembled-library comparisons. Existing
models, voices and pronunciation resources remain unchanged. User listening
acceptance and automated package checks are complete. These checks do not
clear the release gates above.

## Intended download choices

| Package | Intended user | Included dependencies |
| --- | --- | --- |
| Standard Windows x64 installer | Recommended for most users | .NET runtime, native speech runtime DLLs and voice data |
| Standard portable ZIP | Users who prefer an extracted folder | Same payload as the standard installer |
| Framework-dependent installer or ZIP | Users with .NET already installed | Native speech runtime DLLs and voice data; requires .NET 10 Windows Desktop Runtime for x64 |

None includes Kokoro's ONNX model, the optional Qwen model, or the optional llama runtime. Do not include developer models, diagnostics, configuration files, test programs or evaluation tools. The smaller installer checks for .NET and directs users to the official download page if it is absent. It does not silently install a system-wide runtime.

The installer defaults to the stable per-user location
`%LOCALAPPDATA%\Programs\PulseReader`. It also offers an administrator-approved
all-users installation at `%ProgramFiles%\PulseReader`. Upgrades retain the
chosen scope. Shared models live under `%ProgramData%\PulseReader`; the optional
native runtime lives in the protected application's `OptionalRuntime` directory.
Settings and logs always remain under each user's `%LOCALAPPDATA%\PulseReader`
and are created on that user's first launch. Startup remains a per-user opt-in.

The installer writes `installation-scope.json`; portable ZIPs must not contain
that marker. Machine setup accepts only canonical protected paths. It creates
new shared roots with a protected administrator-owned ACL and rejects unsafe
existing trees without taking them over. The normal reader requests fixed-kind
model setup from a separate elevated helper. Named-pipe progress and cancellation
are bounded, and both sides verify the peer process before setup begins.

The finish-page launch option uses Inno's original-user context and verifies that
the launch token is not administrative. Already-elevated setup or an unavailable
original token leaves launch to the normal-session Start menu shortcut.

Uninstall preserves downloaded assets and settings by default. The other choices
remove downloaded dependencies, or remove all Pulse Reader files. Full machine
cleanup explicitly warns about other users' settings on the choice page.
Choosing full cleanup and continuing is sufficient; there is no typed phrase or
additional confirmation page. Unattended uninstall defaults to preserving data;
`/UNINSTALLDATA=dependencies` or `/UNINSTALLDATA=everything` is an explicit
destructive request, including for machine installations. Cleanup runs before application
files are removed. Failure stops uninstall and reports that data may already
have been partly removed; it must not pretend to be a complete wipe.

Profile cleanup uses only Windows' registered normal profiles and exact Pulse
Reader paths. Native directory handles pin existing ancestors during deletion.
Uninstall removes a user's startup Run value only when it points to the removed
installation; a full cleanup also removes the owned agreement receipt. These
registry changes apply only to already-loaded user hives. Offline user registries
are never loaded or modified. Their small startup and terms records remain even
when all their Pulse Reader files are removed. The installer warning states that
limit. Unrelated values and other applications' directories remain untouched.

The earlier offline-loading experiment failed its source-symlink refusal test.
The maintainer approved the limited registry policy instead of a broader hive
loading design. Production code contains no offline hive loader or fallback.

The installer presents `Distribution-Terms.txt`, combining the unchanged MIT
licence and original Microsoft runtime text with a scoped distributor introduction.
Silent installation requires `/ACCEPTDISTRIBUTIONTERMS=yes` after reviewing that
file. Portable launch presents the same agreement before setup or speech.
Acceptance saves the text's SHA-256 under the Windows user's
`HKCU\Software\QuantumDotGames\PulseReader\AcceptedDistributionTermsSha256`.
An installer acceptance avoids a duplicate portable prompt for the same text;
changed terms require fresh acceptance. Declining exits without downloading
models or changing application settings. An unaccepted startup launch exits
quietly; open the app manually to review the terms.

## Build and check the application

Use `Directory.Build.props` as the authoritative version. Run the release scripts with PowerShell 7.4 or later; they use modern .NET hash and stream APIs from the PowerShell host, independently of the installed .NET SDK. Inno Setup 6.7.3 is the initial compiler used for the installer definition. Build-tool installation is separate from application installation.

```powershell
dotnet build PulseReader.slnx --configuration Release
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release
```

For focused setup and speech-failure checks:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --model-setup-checks
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --speech-validation-failure-checks
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --startup-failure-checks
pwsh -NoProfile -File tests/installer/ReleaseBuildRuntimeChecks.ps1
pwsh -NoProfile -File tests/installer/SandboxUpdatePathChecks.ps1
```

These use small HTTP fixtures and offscreen forms, not real model downloads. They cover corruption, cancellation, retries, stalled responses, concurrent requests, unsafe archives, interrupted commit recovery, log preservation during optional-model repair, optional-download gating, progress-dialog retry, and saving the optional setting only after installation succeeds. Speech-failure checks verify that invalid model downloads leave Kokoro idle, remain retryable, and trigger Windows fallback for normal playback without escaping from preview. Startup-failure checks use isolated installation metadata and reporting callbacks to verify nonzero failures without opening dialogs or launching the app. The runtime PowerShell check verifies the release scripts' minimum host version and corresponding documentation. The Sandbox path check creates junctions in a fresh retained `artifacts` fixture and verifies that linked paths and overlapping source/output trees are rejected before preparation writes; it does not launch Sandbox or publish packages.

Build both package variants with a new output directory:

```powershell
./tools/build-release.ps1 -IsccPath "$env:LOCALAPPDATA\Programs\InnoSetup6\ISCC.exe"
```

The recipe copies exact restored NuGet/runtime licence texts, app-local C++ runtime files, voice data, and the reviewed source notices. It verifies each payload before compiling installers, then writes portable ZIPs, inventories, a build manifest and SHA-256 checksums under `artifacts/release`. A full build also emits `update-manifest.json` with the version, exact distribution terms and both installer/portable package flavours. The checksum list includes this manifest. `-PublishOnly` and `-SkipInstaller` do not produce an updater manifest. See [release metadata](updating.md#release-metadata-reference) for the required schema and asset names. A separate `DependencySources.zip` retains the pinned MisakiSharp source/resources and original-lineage references, plus notices and rebuild guidance. See [the source guide](dependency-source-build.md) for its scope; it is not complete combined-application corresponding source.

Existing output directories are not overwritten. Source archives must match the checked-in hashes. Add `-DownloadSources` on a clean build machine to retrieve only those pinned public archives; an existing reviewed cache needs no network download. `-SkipInstaller` builds ZIPs without Inno Setup; `-PublishOnly` prepares verified payloads and the dependency source asset without application distribution archives. Any changed MisakiSharp DLL requires a new source review/pin rather than silently retaining old source evidence.

`tools/release-verify-payload.ps1` rejects missing native/runtime files, wrong versions, model/configuration/test payloads, and missing notices. A successful build deliberately retains `PublicReleaseReady: false`; passing the build does not approve final distribution or publication. Test packages are unsigned.

An earlier local publish reported `NU1900` while the NuGet endpoint was unavailable. A subsequent forced restore reached the service and completed without vulnerability warnings. Refresh that check when preparing the actual public release; package compilation alone is not a security audit.

### Optional developer fixture omitted from the clean snapshot

This clean snapshot intentionally omits
`tools/test-release-in-sandbox.ps1`. Bitdefender quarantined its isolated staging
copy. The fixture remains only in the original private history at
`dc50053465a913bf2bb978f67f807590889905e1`; do not restore, recover, exclude,
obfuscate or execute it as part of this snapshot. The omission was accepted on
24 November 2026. This does not approve release publication or public visibility.

It is an optional developer-only Sandbox orchestrator. The application,
installer definition, build/package recipes, updater and compiled test runner
do not depend on it. Production code and validation controls are unchanged.
The network-enabled install/upgrade/uninstall and package-flavour-transition
journey that it orchestrated is unavailable as an automated route here. Its
historical results do not qualify a future clean-snapshot build.

Available validation routes remain:

- `tools/test-release-offline-in-sandbox.ps1` in a fresh disposable Windows
  Sandbox configured with `Networking` disabled before startup. It seeds only
  a hash-verified Kokoro cache and checks installed/portable startup, synthesis
  without audio playback, the real portable terms prompt and rejection of an
  unaccepted startup. This does not test a first internet model download.
- `tools/test-update-in-sandbox.ps1` for the documented portable/per-user
  updater routes, including its opt-in interactive qualification. See the
  updater section below for preparation and guest-only execution boundaries.
- `tools/test-installation-scopes-in-sandbox.ps1` and the separately documented
  machine-update fixtures for scoped installation, cleanup and UAC checks in
  disposable guests. Never run their guest modes on the host.
- The focused setup, speech-failure and startup-failure test-runner commands
  above, plus `--distribution-terms-checks`, `--update-catalog-checks` and
  `--update-handoff-checks`. `--release-model-smoke` verifies synthesis using
  installed application dependencies and an already verified model cache;
  run that payload smoke only in its intended isolated test environment.

First-download/cancellation, install/upgrade/uninstall, startup ownership,
locked-file cleanup retry and package-flavour transitions still need approved
clean-Windows qualification or an explicit acceptance of scoped limits before
release. The remaining fixtures are not asserted to replace every check in the
omitted orchestrator. Rebuild and requalify exact release assets after any
replacement commit; previous binaries, hashes and scan status do not carry over.
Keep retained failed results alongside later passes. No protection change,
vendor submission or publication is part of this alternative.

## Qualify the updater

After a full candidate build, check all four actual application assets through
the production `UpdateClient` with local fixture HTTP responses:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --update-built-catalog "P:\Code\Pulse-Reader\artifacts\release\PulseReader-1.0.0-candidate29"
```

Replace the final argument with the candidate being qualified. This explicit
packaging check verifies the manifest, terms, checksum list and downloaded bytes
for both installer/portable flavours. It uses local files and does not contact
GitHub, apply an update or publish a release.

`tools/test-update-in-sandbox.ps1` prepares separate portable and per-user
installer fixtures. Its default parameter set is named `Prepare`; there is no
`-Prepare` switch. Use a fresh output root below repository `artifacts`:

```powershell
pwsh -NoProfile -File tools/test-update-in-sandbox.ps1 -CandidateDirectory "P:\Code\Pulse-Reader\artifacts\release\PulseReader-1.0.0-candidate29" -OutputRoot "P:\Code\Pulse-Reader\artifacts\sandbox-update-inputs-candidate29-portable-new"
pwsh -NoProfile -File tools/test-update-in-sandbox.ps1 -CandidateDirectory "P:\Code\Pulse-Reader\artifacts\release\PulseReader-1.0.0-candidate29" -OutputRoot "P:\Code\Pulse-Reader\artifacts\sandbox-update-inputs-candidate29-user-installer-new" -InstallerQualification -IsccPath "$env:LOCALAPPDATA\Programs\InnoSetup6\ISCC.exe"
```

Use the new candidate and output names for each source revision. Preparation
publishes an isolated controller and real 1.0.1 application from the current
source using the local NuGet cache. The old candidate must be a self-contained
1.0.0 `win-x64` payload. The fixture replaces the apphost with the test runner,
changes the future distribution terms and supplies synthetic release metadata.
`-InstallerQualification` compiles old/new packages from the shipping Inno
script; it does not install them on the host.

`-ReuseBuildRoot` accepts a previous fixture root below `artifacts` and reuses its
`Inputs\Controller` and `Build\future-publish`. Use it only when those builds
match the exact source being qualified. The script checks their 1.0.0/1.0.1
versions but does not establish source equivalence. Do not reuse Candidate 28's
controller to qualify a changed Candidate 29 helper.

Preparation emits `UpdateQualification.wsb` and does not launch Sandbox. Review
that configuration and the copied script before launching the approved fixture.
Its networking and clipboard redirection are disabled. Inputs are mapped
read-only; only the dedicated `Reports` folder is writable from the guest.
Each run creates its standard test account, installation and registry receipts
inside the disposable guest. The generated logon command selects `-Guest` and
adds `-InstallerQualification` for the installed route. Do not invoke guest mode
on the host.

Use a fresh Sandbox for each route. Reconnecting to an existing Sandbox does not
recreate it or rerun first-logon preparation. Identify the exact guest owned by
this test and stop that ID before starting the next configuration; do not stop
unrelated guests. After a run, inspect `Reports\result.json`, helper/installer
logs and any `guest-error.json`, then retain the reports before discarding the
guest.

The [Windows Sandbox CLI](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli)
is available starting with Windows 11 24H2. In a normal-user PowerShell session,
resolve its packaged executable and list the guest IDs:

```powershell
$updateSandboxCli = Join-Path (Get-AppxPackage -Name MicrosoftWindows.WindowsSandbox).InstallLocation 'wsb.exe'
& $updateSandboxCli list --raw
```

Before replacing a prior test guest, set `$previousUpdateSandboxId` to its exact
recorded ID, retain its reports, then stop only that guest:

```powershell
& $updateSandboxCli stop --id $previousUpdateSandboxId
```

Start the reviewed configuration and save its returned ID. Connect to establish
the guest login session and run its logon fixture:

```powershell
$reviewedUpdateWsb = 'P:\Code\Pulse-Reader\artifacts\sandbox-update-inputs-candidate29-portable-new\UpdateQualification.wsb'
$updateSandboxId = (& $updateSandboxCli start --config (Get-Content -LiteralPath $reviewedUpdateWsb -Raw) --raw | ConvertFrom-Json).Id
Start-Process -FilePath $updateSandboxCli -ArgumentList @('connect', '--id', $updateSandboxId) -WindowStyle Hidden
```

After exporting and checking the reports, discard this exact guest:

```powershell
& $updateSandboxCli stop --id $updateSandboxId
```

Stopping only the Sandbox client/server process does not establish a fresh
guest. Use the CLI's exact-ID stop before a new run.

### Candidate 28 updater evidence: 14 October 2026

The complete regression suite and full Candidate 28 build passed with no
warnings. Both published payload flavours, both installers and both portable
ZIPs were built. All four actual package assets passed the explicit built-catalog
check above, including their manifest, terms, checksums and bytes. A separate
live probe of the public PowerShell GitHub release API verified the
`2026-03-10` API version and populated SHA-256 asset digests. That probe did not
deliver or install a Pulse Reader update.

The portable guest passed the actual production shadow helper and graceful
parent exit. It replaced real 1.0.0 files with 1.0.1, verified the exact DLL and
changed-terms receipt, preserved model/configuration sentinels and relaunched
normally. The actual retained `--recover-update` helper then restored and
relaunched 1.0.0 from a transaction interrupted after DLL backup. The run took
7.12 seconds. Reports are under
`artifacts/sandbox-update-inputs-candidate28-portable-retest/Reports`.

The per-user installer guest used synthetic packages compiled with the shipping
Inno script. An incorrect compiled terms hash was rejected before any old
payload, uninstaller, scope marker or receipt changed. The production helper
then installed real 1.0.1 into the same canonical user location, verified the
DLL, terms, inventory, scope and uninstaller, preserved user-data sentinels and
relaunched as the normal user. The run took 19.16 seconds. Reports are under
`artifacts/sandbox-update-inputs-candidate28-user-installer/Reports`.

Both guests used a test apphost and synthetic fixed-repository metadata. They
do not qualify real update UI, HTTP delivery, machine installation, UAC or
alternative-administrator credentials. The interruption checkpoint is not a
physical power cut. A subsequent installer failure/relaunch fix changes the
source, so Candidate 28 qualifies only its earlier binaries. Candidate 29's
final-source results are recorded next.

### Candidate 29 final-source updater evidence: 14 October 2026

The final full suite passed, the build reported zero warnings, and all four
actual built installer/portable assets passed the production client's catalog,
terms, checksum and byte checks. Both payload flavours contain the same
`PulseReader.dll` SHA-256:
`9CBAB8624479DDA5B6C4D0BFCB991E8ED4BF77D0A585130112410E7931BA51FF`.
The follow-up source review found no remaining issue.

The fresh portable guest passed normal parent exit, actual production helper
replacement of 1.0.0 with 1.0.1, changed-terms receipt and data preservation. Its
standalone retained helper recovered an interrupted DLL-backup transaction and
relaunched 1.0.0. The run took 8.76 seconds. Reports are under
`artifacts/sandbox-update-inputs-candidate29-portable/Reports`.

The fresh installer guest passed the direct wrong-hash guard, then exercised the
actual helper with accepted offer text inconsistent with the installer's compiled
terms hash. Inno exited 1. The helper verified the unchanged complete old
payload, scope marker, uninstaller, original terms and application file/directory
layout outside user-data paths before
relaunching 1.0.0 as the normal user; the old receipt stayed unchanged.
`guard-failure-helper.log` records the exit, original-build verification and
relaunch in that order. The subsequent valid update installed 1.0.1 at the same
canonical user location, preserved user data, saved the new receipt and relaunched
normally. The run took 24.10 seconds. Reports are under
`artifacts/sandbox-update-inputs-candidate29-user-installer/Reports`.

The installer uses `/NORESTART` and `/RESTARTEXITCODE=3010`, following the
[Inno Setup command-line contract](https://jrsoftware.org/ishelp/topic_setupcmdline.htm).
The helper treats a reboot-required 3010 or any other nonzero exit as failure.
Only installer exit 0 permits relaunch of a verified new build. After a nonzero
exit, only an exact unchanged old installation can relaunch; a partially changed
installation cannot qualify by its new version metadata alone. This is an
original-build verification fallback, not atomic installer rollback.

Candidate 29 closes the final-source rerun for the self-contained portable and
per-user installed routes. These tests still use a test apphost and synthetic
metadata with no real UI or HTTP delivery. Machine UAC approval, cancellation
and alternative-admin credentials, real timer/menu/download/cancellation,
published-release delivery, framework-dependent live helper execution and a
physical power cut remain untested. Controlled transaction interruption and
manual retained-helper recovery passed. No public release is approved.

### Local validation snapshot: 12 October 2026

Candidate 7 builds all four unsigned 1.0.0 packages. The complete regression suite passes. SHA-256 inventories and package checksums are generated with each build; do not reuse these results for later source changes.

A fresh Sandbox profile completed the standard install, verified 325,508,342-byte Kokoro download and actual synthesis without an optional classifier download. Microsoft's .NET installer then spent about two minutes validating each signed MSI, exceeding the original fixture deadline. Its installation ultimately exited successfully. The fixture now allows ten minutes for that guest-only prerequisite step and keeps MSI logs on the guest disk.

After resuming with that real installed runtime, Candidate 7 passed smaller-build installation, standard-to-smaller-to-standard transitions, settings/model/startup preservation, controlled locked-runtime failure and successful retry, and owned/foreign startup-entry uninstall checks. The application DLL matches the fresh-profile-tested Candidate 6 exactly; Candidate 7 corrects the installer prerequisite check to use the 32-bit registry view containing Microsoft's x64 registration. The final result records that this was a resumed stage, not an uninterrupted fresh-profile run.

Local evidence is under `artifacts/sandbox-release-reports-candidate6-retest`; final packages are under `artifacts/release/PulseReader-1.0.0-candidate7`. Earlier fixture timeout reports are retained separately. These are local ignored artifacts, not published release assets.

The subsequent notice/source pass prepares Candidate 10's two payload variants
and the separate dependency-source ZIP. It checks all retained archive hashes,
the packaged MisakiSharp DLL pin, exact reviewed licence hashes, and the presence
of notices in both payloads. Source-bundle validation covers every archived file;
an intentionally wrong cached archive exits nonzero before output creation, and
an existing output directory is rejected. This is a `-PublishOnly` packaging
check, not a new installer or Sandbox qualification. Candidate 7 installers do
not contain these later notice updates and must not be published as current.
NuGet restore in the normal network context also completes without warnings;
the restricted context initially reported `NU1900` when it could not reach the
audit service. PublicReleaseReady remains false because the licensing route has
not been selected or qualified. No app or installation was restarted for this
notice pass.

### Permissive frontend qualification: 13 October 2026

The application now references `src/MisakiSharp.PulseReader` rather than the
stock package's compile/runtime assembly. Its copied number and punctuation
implementations are replaced, while all twenty embedded resources retain their
original hashes. The original source archives remain historical references in
the source asset; `local-frontend` contains the modified project actually built.

The final isolated build passes with zero warnings/errors after a fresh restore.
The complete InputSequenceTests suite passes in the normal desktop context.
Compatibility checks match 772 English and 14 multilingual phoneme/token outputs;
focused numeric/provider fixtures also pass. Ten A/B audio pairs are available
locally for user acceptance. The paired phonemes/tokens match, but PCM is not
byte-identical, including in a repeat of the unchanged baseline. See
[the qualification report](permissive-g2p-replacement-plan.md).

Payload verification requires the replacement patch metadata and helper
methods, rejects the stock DLL and removed helpers, and checks every resource
hash. Each new payload records its actual binary and matching source snapshot
in `misaki-frontend-build.json`. Do not reuse Candidate 7's installer acceptance
as qualification for this changed frontend. The normal running Release has
not been replaced during these isolated checks.

Candidate 11 verifies both isolated payload variants with the local frontend.
Its dependency-source ZIP contains 96 hash-verified files plus its inventory,
including all twenty resources and the complete modified build context. Building
that extracted context succeeds without warnings/errors, and its replacement
metadata/resources verify. The packaged frontend also matches all 786 assembled
phoneme/token comparisons. This was `-PublishOnly`: no new installer, portable
ZIP, Sandbox acceptance, installation or app restart was performed.

## Clean-profile acceptance

The 13 October checks below cover the automated portions of this list. Keep
interactive hotkey, playback controls and installer appearance as maintainer
acceptance, rather than claiming unattended synthesis proves them.

Use a disposable Windows profile or VM. Do not delete a tester's existing settings or models to simulate first launch.

1. Install the standard package without .NET or the C++ runtime already installed. Confirm no administrator prompt and no missing-DLL errors.
2. Launch it with no model cache. Confirm Kokoro setup shows progress and that it is the only model requested. Check successful completion, cancellation to Windows speech, retry after a network failure, and application exit during a download.
3. After canceling to Windows speech, select Kokoro in Settings. Confirm the setup dialog returns and the setting changes only after successful installation.
4. Turn off the network after successful setup. Test speech, preview, navigation, hotkey cancellation and saved settings. Reopen the app and confirm it uses the verified cached model.
5. Enable the optional pronunciation setting. Confirm its roughly 2.5 GB download starts only then, can be canceled, and leaves the setting off if it fails. Successful installation must become available without restarting the app. Verify both pronunciations of `live` through normal playback.
6. Close the app during optional setup and relaunch it. A partial file must not count as installed. A verified, unambiguous staged installation interrupted during its final move must recover without downloading the model again and preserve the existing logs. A matching staging/backup pair takes precedence over an older unpaired stage. Ambiguous or invalid paired recovery must report failure without changing retained files.
7. Upgrade over the same installation. Check model/settings retention, shortcuts and opt-in Windows startup. Test changing between standard and framework-dependent payloads without leaving incompatible old runtime files.
8. Uninstall. Confirm application files and owned shortcuts are removed, user data remains, and only this installation's startup entry is removed.
9. Test the framework-dependent package both with and without the .NET 10 Desktop x64 runtime. Test the portable ZIP after extraction into a new folder.

### Local clean-profile snapshot: 13 October 2026

Candidate 12 builds both unsigned installers and both portable ZIPs with the
modified frontend and scoped distribution agreement. The full regression suite
passes, and its packaged frontend matches all 786 phoneme/token comparisons.
Seven malformed licence-contract cases are rejected after the verifier was
aligned with the application's strict parser.

The networked Windows 11 Sandbox installed the standard package without .NET,
downloaded the pinned 325,508,342-byte Kokoro model and synthesized speech.
The smaller installer rejected missing .NET, and silent setup rejected missing
explicit terms acceptance. Setup cancellation/retry and optional-setting fixtures
passed through the installed dependencies. An added adapter-control check then
failed because the normal guest could not access CIM; its failed report is
preserved. The installer fixture resumed in that same guest and passed upgrade,
both flavour transitions, locked-file failure/retry, settings/model preservation,
and owned/foreign startup uninstall checks. This was not an uninterrupted
fresh-profile pass.

A separate fresh guest had networking disabled in its Sandbox configuration and
reported zero active non-loopback interfaces. With only a hash-verified cache
seeded, installed and portable startup/synthesis passed. Removing the guest's
terms receipt caused the actual portable agreement to appear; unaccepted
`--startup` exited nonzero without prompting. Restoring the previously accepted
receipt allowed portable launch. Decline/close and changed-terms behaviour are
covered by the offscreen consent fixtures.

An independent fresh 8 GiB guest exercised the real Settings checkbox and setup
dialog. It downloaded the pinned 2,497,280,448-byte Qwen model and llama runtime,
verified every file and notice, and saved enablement only after success. The
production classifier returned both `live` labels correctly and delivered the
expected reading units without restarting. First-unit resolution took 7.05
seconds, with 3.19 seconds of server startup and 2.16 seconds of cold inference;
the next inference took 0.56 seconds. The fixture allowed longer request budgets
of 120/60 seconds, but these observed results fit the normal 10/2-second budgets.
No memory/startup policy was overridden. No Kokoro model was downloaded in that
classifier-only run, and the classifier process stopped on disposal.

Reports are retained under `artifacts/sandbox-release-reports-candidate12`,
`artifacts/sandbox-release-reports-candidate12-offline` and
`artifacts/sandbox-release-reports-candidate12-optional`. The recorded guest OS
was Windows NT 10.0.26100.0. All test guests were stopped/discarded after their
reports were retained; the normal application, settings and models were not
replaced. A first full-suite invocation from the isolated published test runner
lacked its voice fixture folder and failed with a contained assertion; copying
the actual payload's voice assets into that test runner produced a full pass.

Candidate 13 was the qualified package set before installation-scope changes, under
`artifacts/release/PulseReader-1.0.0-candidate13`. It adds the completed review
documents to the payload/source asset and retains the same application behaviour.
The rebuilt application and frontend DLL hashes differ from Candidate 12 because
the regex generator's file-local type names contain a hash of the absolute
generated-file path. Both package flavours have identical method bodies,
signatures and embedded resources. A complete-image comparison leaves no byte
differences after normalizing only those path-derived name hashes, the module
identifier and the PE timestamp. This is semantic/build comparison evidence,
not a claim that the DLLs are byte-identical.

A fresh network-disabled Sandbox also passed Candidate 13's actual installed
and portable startup/synthesis, first-launch agreement and unaccepted-startup
checks. Its reports are under
`artifacts/sandbox-release-reports-candidate13-offline`; the binary comparison is
under `artifacts/candidate-binary-comparison`. The Candidate 12 live downloads
and upgrade checks remain applicable to the unchanged executable code, but were
not repeated as fresh downloads for Candidate 13. All five final asset checksums
and all 100 inventoried source-ZIP entries verified. The completed guest was
discarded; no normal installation or model cache was replaced.

## Installation-scope snapshot: 13 October 2026

Candidate 17's fresh guest passed the per-user default and all three current-user
cleanup choices with owned and foreign startup values. Its actual machine
installation downloaded the pinned 325,508,342-byte Kokoro model through the
privileged helper and produced PCM speech. Missing optional-model startup saved
the setting as off without downloading it or changing other settings. Machine
preservation, dependency removal and unconfirmed full-removal rejection passed.
Full removal then failed because the file pin conflicted with Windows hive
loading. The failure stopped application removal and remains recorded.

Candidate 19 repeated the default, current-user and protected machine setup
checks, downloaded Kokoro and produced PCM. Its native hive check failed with
access denied. Adding directory traverse access made regular loading work, but
the source-symlink refusal check then failed because the native loader accepted
the link. Metadata-only and execute-access file-root probes also failed, with
access denied and not-a-directory respectively. None qualifies offline cleanup.

Reports are under `artifacts/sandbox-scope-reports-candidate17-final` and
`artifacts/sandbox-scope-reports-candidate19`. That intermediate source rejected offline
profiles before any deletion and disabled the production native-load command.
At that intermediate stage, temporary-fixture checks verified all three choices
left files and registry calls untouched on the preflight failure. Those checks
and the safety gate were superseded by the approved policy below.

A separate network-disabled guest passed Candidate 17's installed and portable
startup, cached synthesis, actual portable agreement and unaccepted-startup
checks. Its reports are under
`artifacts/sandbox-release-reports-candidate17-offline`. Those binaries precede
the diagnostic native-loader changes and safety gate; they are not final-package
qualification. All guests were discarded, and normal application files, models,
settings and system security settings were not changed.

The maintainer subsequently approved deleting all Pulse Reader files while
leaving offline startup/terms records. The rejected native loader and its unused
guards were removed.

## Approved cleanup qualification: 13 October 2026

Candidate 20 passed a fresh Sandbox's installation-scope matrix. Its synthesis
check used an administrator token and did not cover ordinary-user voice-file
access; the maintainer's global-playback finding exposed that gap below.

- Fresh setup defaulted to per-user installation without creating profile data.
- All three per-user cleanup choices preserved unrelated files and foreign
  startup values.
- The all-users installer created protected application and shared-model
  directories. The real privileged setup downloaded the pinned Kokoro model,
  verified it and produced PCM speech.
- An enabled but missing optional model was persisted as disabled at startup,
  without downloading it or changing other settings.
- Machine preservation, dependency removal and full cleanup passed. Unconfirmed
  full cleanup was rejected before changing files or registry values.
- Full cleanup deleted the synthetic logged-out user's Pulse Reader files while
  its registry file hash and startup/terms records stayed unchanged. Loaded-user
  cleanup removed owned records while retaining unrelated values.

A separate fresh network-disabled guest passed installed and portable cached
synthesis, portable first-launch agreement, unaccepted-startup rejection and
settings preservation. No optional model was downloaded in either guest. The
earlier Candidate 12 real optional-model download remains recorded separately;
it was not repeated for this registry-policy change.

The complete regression suite and all 17 embedded installer ACL-policy checks
passed. Focused cleanup coverage includes loaded/offline profile mixtures,
access-error propagation, locked files, junctions and pre-deletion traversal
limits. All five asset checksums and 100 dependency-source inventory entries
verified. Reports are under `artifacts/sandbox-scope-reports-candidate20` and
`artifacts/sandbox-release-reports-candidate20-offline`; packages are under
`artifacts/release/PulseReader-1.0.0-candidate20`.

Both guests were discarded. The normal installation, user configuration, model
cache and system security settings were not changed. Standard-user UAC
approval/cancellation, alternative-administrator credentials and the interactive
uninstall warning layout still need maintainer acceptance. Sandbox
used an administrator token, so its successful IPC check does not prove those
credential flows. Candidate 20 is unsigned and not approved for publication.

## Installer feedback fixes: 13 October 2026

The maintainer found that Candidate 20's global installation always fell back
to Windows speech. A disposable payload reproduced the failure by making
`af_heart.npy` read-only: production voice selection threw
`UnauthorizedAccessException` because NumSharp's path-based loader requested
read/write access. Pulse Reader now loads the same embeddings through an
explicitly read-only stream. All 54 voices retain identical float values. The
original probe and normal/blended selection with every voice read-only pass.

Candidate 21 also prevents Inno's default form enlargement from adding blank
space to the uninstall dialog, prunes only empty known storage containers, and
offers post-install launch after verifying a non-administrative original-user
token. Setup started already elevated keeps the normal-session shortcut advice.

The full regression suite, 17 ACL-policy checks and installer UI-policy checks
passed. A fresh Sandbox downloaded Kokoro, then produced PCM using production
voice selection with all installed voices marked read-only. All cleanup choices
passed, including retained-data protection and an actual empty Program Files /
ProgramData removal case. The initial root-removal check ran before Inno's final
delayed pass; its preserved failure report led to a completion-based fixture
wait. The rerun passed without changing the installer.

A separate network-disabled guest passed installed and portable synthesis with
read-only voice assets, agreement handling and settings preservation. Reports
are under `artifacts/sandbox-scope-reports-candidate21` and
`artifacts/sandbox-release-reports-candidate21-offline`. Both guests were
discarded. All five Candidate 21 asset checksums and 100 dependency-source
inventory entries verified. The normal installation and user data were untouched.

Candidate 21 is ready for maintainer global-playback, dialog appearance and
finish-page launch checks. Actual standard-user/alternative-admin UAC and
different-DPI interaction remain manual gates. It is unsigned and not approved
for publication.

## Release polish checks on 14 October 2026

Candidate 30 was rebuilt under `artifacts/release/PulseReader-1.0.0-candidate30`
with .NET SDK 10.0.401. Its application and local frontend identify revision
`9a40e85`. The build included only documentation and screenshot-fixture worktree
edits beyond that revision; it is not an attestation of a clean tagged build.

Both payload verifiers pass, including runtime files, 54 voices, source pairing
and required notices. All six release checksums, portable ZIP entries, ownership
inventories and updater asset metadata match. All four built assets also pass
the real `UpdateClient` package-catalog fixture. No models, personal settings,
logs, test programs or optional runtime downloads are bundled.

Compared with Candidate 29, application and frontend managed method bodies and
embedded resource bytes are unchanged. Their embedded revision metadata and
paired inventory hashes changed. Candidate 29's runtime/Sandbox evidence remains
the recorded evidence; Candidate 30's exact installers were not installed,
unpacked or exercised in a fresh Sandbox during this pass. Both installers are
unsigned, and `PublicReleaseReady` remains false.

An isolated restore with `NuGetAudit=true` and `NuGetAuditMode=all`, followed by
`dotnet package list --vulnerable --include-transitive --no-restore`, refreshed
NuGet's public advisory data on 14 October 2026. It reported no known vulnerable
packages for any solution project. The HTTP cache and restore outputs were
isolated under `artifacts/release-polish-20261014`. This check does not assess
unknown vulnerabilities, antivirus detection, custom frontend code or the
optional downloaded native runtime.

The README images were regenerated with the current renderers. Settings includes
automatic updates and the tray preview shows `Check for updates`. The fixture
renders offscreen without activating the form. The focused settings/tray/icon
checks pass. Local documentation links and anchors pass the repository check.

Detailed local evidence is retained under `artifacts/release-polish-20261014`:
`package-verification.md` for Candidate 29, `package-verification-candidate30.md`
for Candidate 30, `dependency-restore.log`, `dependency-vulnerabilities.json`
and `documentation-checks.md`. These ignored files are maintainer evidence,
not public release assets. No host installation, normal application restart,
new model download, antivirus submission or publication occurred.

## Updater UI qualification on 14 October 2026

Candidate 31's self-contained x64 portable and per-user installer update flows
passed in separate fresh Windows Sandbox guests. Each guest used a disposable
standard account, networking and clipboard disabled, read-only fixture inputs
and a reports-only writable mapping. Both guests were discarded after export.

The fixture drove the production application context's menu commands and Settings
checkbox. It verified automatic-check opt-out past the initial timer deadline,
manual discovery without package download, changed-terms decline, cancellation
at real download progress, retry, authenticated handoff, graceful old-process
exit, real helper replacement, version 1.0.1 relaunch and a subsequent current-
version check. The installer log records success without a reboot. A process-
handle handshake verified the relaunched processes' exit code 0. Configuration
bytes, a guest model-file sentinel and the correct terms receipt were preserved.
All 422 resulting owned files matched each route's prepared new payload.

The test-only apphost and controlled HTTP responses are not a public release.
They use the production app context, validation, helper and installer, but bypass
the shipping `Program.Main` startup path. The internal client constructor does
not add a shipping endpoint override. Images are `DrawToBitmap` captures of
visible production forms, not full-desktop screenshots or physical-click tests.
Machine UAC, public HTTP delivery, framework-dependent live updates, physical
input/desktop placement and physical power-loss behaviour remain open gates.

The screenshot walkthrough is `artifacts/updater-flow-review/index.html`.
Raw evidence, capture provenance, owned-file snapshots, installer/helper logs
and exact package hashes are under
`artifacts/sandbox-update-ui-candidate31-portable-r3` and
`artifacts/sandbox-update-ui-candidate31-user-installer-r3`. Preparation uses
`tools/test-update-in-sandbox.ps1 -InteractiveQualification`, with
`-InstallerQualification` for the installed route. Inspect the generated guest
configuration before launching it. These files are ignored maintainer evidence.

The first portable attempt failed a fixture comparison because RichTextBox
normalises line endings. The correction normalises only the displayed text,
while keeping the exact authenticated UTF-8 hash check. Failure reports remain
under `artifacts/sandbox-update-ui-candidate31-portable`; the failure was not
converted into a pass. The corrected fixture also stops automation on failure
and publishes JSON reports atomically. The focused updater UI checks and full
regression suite pass. The suite initially lacked voice assets in its isolated
runner; it passed after copying the candidate's bundled voice fixtures there.

The Candidate 31 captures show `Decline and exit` in the update terms dialog,
although declining correctly leaves the existing app running. The maintainer
approved the flow on 30 October 2026. The follow-up source change now uses
`Cancel update` and explains that the current version stays running. First-launch
terms retain `Decline and exit`. Focused terms checks cover acceptance, decline
and window-close in both modes, including visible and accessible wording. Those
checks and the focused updater UI checks pass in an isolated build with zero
build warnings or errors. The historical captures remain unchanged. Candidate
32's follow-up rebuilt the payloads and exercised the corrected wording; see
the [30 October record](updater-qualification-20261030.md) for its exact scope.
Normal host installation, settings and models
were untouched. No Git commit, public release or antivirus submission occurred
as part of this qualification.

## Repository ownership and launch handoff

The official source and updater location is `qdg-main/pulse-reader`.
The clean-snapshot repository identity is `1470996631`. Its temporary private
preparation name was `qdg-main/pulse-reader-clean-staging`; the required final
name is exactly `qdg-main/pulse-reader`.

The original development history is preserved privately as
`qdg-main/pulse-reader-private-history`, repository ID `1341244162`, including
its branches and pull requests. The reviewed source for this snapshot was
`dc50053465a913bf2bb978f67f807590889905e1`; it is provenance, not a Git parent.
Do not merge or push the original history into the clean repository.

The existing `P:\Code\Pulse-Reader` checkout retains the private history and
must target only its private-history origin. The canonical source uses a fresh
separate clone at `P:\Code\Pulse-Reader-Canonical-20261124`. T3 project
registration and historical PR links must be checked separately through an
authorized client; a cached project name is not proof of the local push target.
Local credential failures must use approved authentication; never copy a token
into the app or bypass authentication.

Source ownership and public update distribution are separate decisions.
The updater uses unauthenticated requests and exact canonical URLs; transferring
a private repository does not make its releases publicly readable. Public
access at the canonical GitHub release location requires separate approval;
keep the repository private until then. Keep origin, redirect, digest, length,
checksum and terms validation intact. No credential belongs in the app.

For the approved preview plan, recommend the standard Windows x64 installer
and provide `SHA256SUMS.txt` covering the exact final downloads. Use a brief
warning such as: "Unsigned preview: Windows may show an unknown-publisher or
SmartScreen reputation warning. Verify the matching SHA-256 checksum before
running." A checksum establishes byte agreement, not an antivirus result.
If the GitHub release is marked as a prerelease, the stable updater deliberately
ignores it; preview users would download manually. No preview update channel is
introduced by this migration. The preview plan does not approve publication,
tagging, visibility changes or skipping the scan and clean Windows gates.

There is no GitHub Actions release workflow in this checkout. The existing
workflow is the local `tools/build-release.ps1` recipe followed by reviewed,
approved uploads through GitHub Releases at the chosen distribution repository.
Do not add automatic publication as part of ownership migration. Build fresh
packages after the owner change; old local candidates contain the old endpoint.
Run `--update-catalog-checks` and `--update-handoff-checks` through the test
project, then qualify the fresh assets using `--update-built-catalog <folder>`.
Upload both installers, both portable ZIPs, the dependency-source ZIP,
`update-manifest.json` and `SHA256SUMS.txt` together only after approval.

Simulated catalog checks validate canonical URLs and integrity failures without
network access. They do not establish live public delivery. Once publication
and public distribution are approved, verify unauthenticated release metadata,
GitHub SHA-256 asset digests and an actual download from the chosen location.
Legacy-install compatibility is not a launch gate for this pre-release move.

## Publish only after approval

The [1.0.0 release-note draft](release-notes-1.0.0.md) describes the download
choices, first-run setup and known limitations. Keep its draft status until
the release gates are complete. The [troubleshooting guide](troubleshooting.md)
covers capture, speech, monitoring, setup and retained update logs.

The updater's full suite, four built-asset catalog checks and Candidate 29
self-contained helper, per-user installer success/failure and retained-recovery
flows pass. Candidate 31's controlled app-context UI flows also pass. The
[30 October follow-up](updater-qualification-20261030.md) adds live
framework-dependent routes, shipping startup, actual pointer cancellation and
self-contained machine-scope UAC with a different administrator. Published
asset delivery and broader platform/physical power-loss coverage remain open.
Portable power-loss recovery is manual, with retained
logs, helper, journal and backup; there is no automatic reboot recovery. Retain
Candidate 29's hashes and reports with the remaining acceptance results.
See [updating and recovery](updating.md).

After the maintainer accepted the installer feedback fixes, the extra typed
all-users cleanup page was removed. Choosing full cleanup and continuing is now
the only interactive confirmation. Silent `/UNINSTALLDATA=everything` also
suffices; silent uninstall still defaults to preserving data. Obsolete
confirmation parameters are rejected. Candidate 22 was rebuilt with this
contract; the full regression suite, focused cleanup/UI checks and both
installer builds passed. Candidate 21's fresh Sandbox results remain the recorded
installed/portable qualification; a fresh Candidate 22 guest run was not repeated
for this confirmation-only change.

Resolve every blocker, record the tested operating system and package hashes, and prepare release notes with the download choices and known limitations. Then obtain approval to tag and publish `v1.0.0`. Building an installer is not authorization to publish it.

The repository's Sponsors link is [Mark-A-Richardson on GitHub Sponsors](https://github.com/sponsors/Mark-A-Richardson). Release notes may include a short optional-support link; donations do not unlock features.

## Pre-launch follow-ups

- Review the [latest updater qualification](updater-qualification-20261030.md), including its retained Windows consent crash/retry and coverage limits. Live public-asset delivery needs approved published assets; no temporary release was uploaded to manufacture coverage. Rebuild final packages and qualify their exact bytes before publication.
- Add contribution and bug-reporting guidance once the maintainer has chosen a strategy shared across their projects. The current roadmap already describes useful reproduction details, but is not a contribution policy.
