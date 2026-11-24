# Updater qualification on 30 October 2026

This record separates completed updater checks from remaining published-asset
and platform-coverage limits. It supplements the approved
14 October screenshot walkthrough. It does not approve publication.

## Test inputs

Fresh Candidate 32 payloads were built from the current worktree with SDK
10.0.403. Both payload verifiers passed, including required notices, native
files and 54 voices. These are dirty-worktree candidates, not a clean tagged
release. Output is under `artifacts/release/PulseReader-1.0.0-candidate32`.
The updated host SDK performed first-use setup and reported creating an ASP.NET
development certificate. No certificate-trust command was run. The normal
Pulse Reader installation, settings and models were not changed.

The framework-dependent guests use Microsoft's Desktop Runtime 10.0.14 x64
installer. Its SHA-512 matches the
[official release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json),
and its Authenticode signature is valid and identifies Microsoft Corporation.
The downloaded installer is 60,035,296 bytes. It runs only inside disposable
guests, not on the host.

Each update guest disables networking and clipboard redirection, maps fixture
inputs read-only and exports reports through one writable mapping. Test
application processes run as the disposable standard account
`PulseUpdateFixture`. Synthetic 1.0.1 packages and controlled release responses
exercise the real validation, copied helper and replacement code.

## Results

| Check | Result | Evidence or limit |
| --- | --- | --- |
| Framework-dependent portable update | Passed | 1.0.0 PID 6300 to 1.0.1 PID 2452, real helper, preserved settings/sentinel and relaunch exit code 0 |
| Framework-dependent per-user installer update | Passed | 1.0.0 PID 3692 to 1.0.1 PID 6672, real Inno/helper path and relaunch exit code 0 |
| Genuine shipping executable after update | Passed on fresh rerun | `artifacts/updater-gap-tests-20261030/shipping-relaunch-r2`; real 1.0.1 PID 3516 |
| Actual pointer click on Cancel update | Passed | Live desktop click followed by zero package requests, unchanged old payload/data, retry, replacement and relaunch |
| UAC enabled across an in-guest restart | Passed | Boot time changed, marker survived, `EnableLUA=1`, secure desktop remained enabled and interactive token was non-elevated |
| All-users UAC cancellation | Passed | Old 1.0.0 reopened as the ordinary user, with no installer launch or payload/data/receipt changes |
| All-users approval using a different administrator | Passed after retry | Inno ran under `PulseUpdateAdmin`; 1.0.1 relaunched as `PulseUpdateFixture`, not elevated |
| Post-approval data and scope validation | Passed | All 422 owned files match; machine scope/flavour, exact seeded config bytes, personal/shared sentinels and original-user receipt verified |
| Cross-user launcher success/failure reporting | Passed | Same-user worker captured native exit 0 for verification and 1 for an intentional failed assertion; diagnostic stack retained |
| Terms, Settings and tray UI checks | Passed | Fresh isolated test build, zero compiler warnings or errors |
| Release catalog and rate-limit checks | Passed | Covers package flavour, digest/manifest agreement, cancellation and bounded HTTP behaviour |
| Authenticated handoff and persisted daily schedule | Passed as normal user | Restricted-shell runs failed on pipe/file access; normal-user reruns passed without changing permissions or assertions |
| Full regression suite | Passed as normal user | `artifacts/updater-gap-tests-20261030/regression-suite.log`; bundled voices were copied into that isolated runner |
| Preparation refusal checks | Passed | Source overlap, incompatible modes and reused-flavour mismatch were rejected before output creation |
| Live production GitHub check | No public latest release | Default production HTTP client returned `NoRelease: true`; no package was downloaded or installed |

The live check is an explicit opt-in test command,
`PulseReader.InputSequenceTests.exe --update-live-network-check`. It is not
part of the offline regression suite and never applies an update. Its result is
retained in `artifacts/updater-gap-tests-20261030/live-network-check.json`.

Independent SHA-256 comparisons matched all 153 owned files in each resulting
framework-dependent payload to that route's prepared new package. The installer,
shipping-relaunch and actual-click guests also reported zero Pulse Reader
Application Error/.NET Runtime crash events. No crash-event claim is made for
the first portable guest, which was discarded before a successful diagnostic
export.

The original shipping-startup assertions passed, but its wrapper failed while
exporting a temporary JSON file that had just been atomically renamed. The
exporter now excludes only its known `.tmp-<32 hex digits>` publications. A
deterministic stale-snapshot probe reproduced the old failure and passed through
the actual corrected exporter. The fresh shipping rerun passed both the app
assertions and the wrapper. Original failure evidence remains under
`artifacts/updater-gap-tests-20261030/shipping-relaunch`.

The actual Cancel update click is recorded in
`artifacts/updater-gap-tests-20261030/physical-cancel/Reports/before-physical-cancel.png`.
The guest had to be activated before its screen could be captured correctly;
no input was sent to another host application.

## What the new scenarios verify

The framework-dependent scenarios exercise the same Settings/menu/terms flow
as the approved walkthrough, but require the guest's separately installed
Desktop Runtime. They must preserve the framework-dependent flavour through
download selection, helper startup, replacement and relaunch. A self-contained
test runner is not substituted into those application payloads.

The shipping-relaunch scenario starts with the controlled UI parent, then keeps
the genuine production apphost in the new package. The real helper relaunches
that executable through `Program.Main`, without an injected application context.
It checks that the new app remains running, preserves settings and the model
sentinel, rejects duplicate sign-in startup quietly, rejects unaccepted sign-in
startup without prompting or accepting terms, and starts with accepted terms.
Exact guest-owned processes are terminated after these assertions for test
cleanup. That cleanup is not evidence of graceful tray-menu Exit.

The pointer scenario pauses the automatic test at the real terms dialog. An
actual click must dismiss Cancel update before the existing no-download,
unchanged-file and retry assertions can continue. Other scenario steps still
use production control callbacks. This is not a varied-DPI or full taskbar
placement matrix.

## Elevation outcome and remaining limits

### Elevation checks completed with a retained failed attempt

The initial framework-dependent guests had `EnableLUA=0`; their installer
results were not counted as UAC coverage. A separate guest then enabled UAC and
restarted successfully. Microsoft's
[Sandbox documentation](https://learn.microsoft.com/windows/security/threat-protection/windows-sandbox/windows-sandbox-overview)
describes restart persistence. The probe verified it on this build, rather than
assuming that a separate VM was necessary. Host UAC and secure-desktop policy
were not changed. The temporary guest accounts and the UAC-enabled guest were
discarded after evidence export.

The maintainer cancelled the first real prompt. The production helper reopened
the byte-identical 1.0.0 machine installation as the original ordinary user.
The first approval attempt failed before installer launch with Win32 error
`0xc000027b`. The guest Application log identifies `consent.exe` crashing in
`Windows.UI.Xaml.dll` with that exception code. Pulse Reader reopened the
verified unchanged old build. This was a Windows consent-process crash, not a
normal test timeout or a Pulse Reader crash. Its underlying Windows cause was
not investigated or claimed fixed.

When the maintainer was ready, an approval-only retry succeeded. The installer
log records administrative installation and its protected temporary directory
under `C:\Users\PulseUpdateAdmin`. Relaunch recorded the original
`PulseUpdateFixture` SID and a non-elevated token. A separate verifier ran as that
ordinary user and checked the complete new payload, machine identity, exact
seeded configuration, both personal and shared model sentinels and the new
HKCU terms receipt. The final diagnostic export records zero Pulse Reader
Application Error/.NET Runtime crash events, including after the intentional
harness-failure probe. It retains the separate Windows consent crash.

The original `machine-result.json` remains a failure record. The successful retry
is recorded separately in `machine-post-retry-validation.json`,
`machine-current-helper.log`, `machine-current-install.log` and
`machine-worker-verification.json`, under
`artifacts/updater-gap-tests-20261030/uac-restart/Reports`. No failing attempt was
rewritten into a pass.

Windows PowerShell exposed a null cross-user `Process.ExitCode` in the outer
launcher. The corrected launcher runs a small worker in the ordinary user's
context and records the actual native test exit code there. Both a passing
verification and intentional failing probe were exercised. Failure still exits
nonzero and retains the specific assertion and stack. Future two-stage machine
controllers also wait for `machine-approval-ready.marker` in the guest Reports
directory before opening the approval prompt. This does not change Windows'
prompt timeout or claim to repair its consent component.

### End-to-end published update delivery

The anonymous production GitHub endpoint returned no public latest release.
The live client handled this correctly, but no public package/manifest delivery
was exercised. This needs approved release assets before it can be qualified.
No release or temporary test release was published to manufacture a pass.

### Other retained limits

Physical power loss, Windows 10, other machines and varied DPI remain outside
this run. Earlier retained-journal recovery evidence is still the relevant
interruption result. No model loading, speech-quality or complete user-data
preservation claim follows from preserving a small guest sentinel.
The framework-dependent all-users combination was not run directly: the live
framework-dependent routes were portable and per-user, while the live all-users
UAC route used the self-contained flavour.

## Pre-merge review and Candidate 33

The aggregate updater/release-polish review found a machine-fixture verification
gap: an empty installed ownership list could skip all file comparisons. The
verifier now requires the complete expected and installed path sets to agree
before comparing every owned file's bytes. Both the normal approval controller
and the approval-only verifier use this check. The focused regression failed
against the previous comparison and passed after the fix; it also rejects
missing and corrupt owned files. It runs in the full regression suite without
creating a Sandbox, requesting elevation or changing application settings.

The follow-up review found no further blocking issue. The isolated Release
solution build passed with zero warnings/errors. The full regression suite
passed after the fix, as did the release-tool runtime, injected installer ACL,
uninstall policy and actual junction/overlap path checks. Junction creation
failed in the restricted shell; the unchanged test passed as the normal user.
The refreshed all-dependency NuGet advisory restore completed without warnings.
Updated Markdown links, PowerShell parsing and whitespace checks passed.

Fresh local packages are retained at
`artifacts/release/PulseReader-1.0.0-candidate33-final-audit`. Both payload
verifiers, all six release checksums and the production `UpdateClient` catalog
checks for all four installer/portable flavours passed. They were built with
SDK 10.0.403 from the reviewed worktree. They remain unsigned, with
`PublicReleaseReady: false`; they are not a clean tagged release. No fresh guest
installation was repeated for these exact assets. The earlier runtime results
above remain the scoped Sandbox evidence, and final packages must be rebuilt
and checked after the upcoming Codex monitoring fixes before publication.

The retained logs are `finalization-package-build.log`,
`finalization-regression-suite-after-fix.log` and `finalization-nuget-audit.log`
under `artifacts/updater-gap-tests-20261030`. This review does not resolve the
documented public-delivery, antivirus/signing or platform-coverage limits.

## Repeating the isolated checks

Prepare a fresh output directory with `tools/test-update-in-sandbox.ps1`.
Select `-Variant win-x64-framework-dependent`, supply
`-DesktopRuntimeInstaller`, and add `-InteractiveQualification`. Add
`-InstallerQualification` for the per-user installer route. Reused builds must
have the same package flavour.

`-ShippingRelaunch` keeps the real production apphost in the new package.
`-PhysicalCancelProbe` pauses at Cancel update for a real input action. Both
require `-InteractiveQualification`. Review the generated WSB and guest script
before launching. Only stop guests created for this test, after exporting their
reports. Runtime installation logs can take several minutes to finish in a
network-disabled guest.

`-MachineElevationPayload` with `-InstallerQualification` prepares dedicated
self-contained machine-scope payloads without launching a guest. Use
`tools/test-uac-sandbox-restart.ps1` only in its guarded disposable guest, then
stage `tools/test-update-machine-in-sandbox.ps1` and the machine inputs there.
The initial setup runs as guest SYSTEM; the updater itself runs as the ordinary
test user. A person handles the UAC decisions. Do not store temporary account
passwords in source, evidence files or persistent configuration. The launcher
supports `-Run -VerifyOnly` and `-Run -FailureProbe` for validating results and
failure propagation without another installation or UAC prompt.

The qualification itself did not commit, push, merge, publish a release or
restart the normal Pulse Reader installation. Repository finalisation is a
separate maintainer-authorised step; publication remains unapproved.
