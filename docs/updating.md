# Update Pulse Reader

This guide describes the updater implemented for Pulse Reader 1.0.0. Candidate 29 passed
the full test suite, all four built-package catalog checks and disposable Windows
checks of the self-contained copied helper, per-user installer handoff/failure
relaunch and retained portable recovery. Candidate 31 also passed isolated
portable and per-user updater UI flows through the production application
context, with controlled HTTP responses and real helper replacement/relaunch.
The [30 October follow-up](updater-qualification-20261030.md) passed live
framework-dependent portable/per-user updates, shipping startup, an actual
Cancel update click and self-contained all-users UAC cancellation/approval with
a different administrator. One approval attempt failed in Windows' consent
process before a successful retry. Published asset delivery and broader
platform/DPI/physical power-loss coverage remain limited. No public release has
been approved.
See [release preparation](releasing.md) for the
evidence and remaining gates.

## Check for a newer version

Packaged copies check GitHub for a newer stable release while Pulse Reader is
running. `Check for updates automatically` is enabled by default in Settings.
Turn it off to stop background checks; manual checking remains available.
Checks are due 24 hours after the last attempt, with the schedule saved across
restarts. This is an application timer, not a Windows scheduled task.

Background checks stay quiet. A newer version adds an update mark to the tray
icon and changes its menu action to `Update to <version>`. Background checks do
not download the application package or install it. Failed checks do not open
an error dialog. A GitHub rate-limit response defers checks until the recorded
retry time, including manual checks.

Right-click the tray icon and choose `Check for updates` to check immediately.
The tooltip reports `Up to date` or `No published release yet` when appropriate.
Source builds, unpackaged copies, and installed copies moved out of their
recorded location show `Updates unavailable` when their release identity cannot
be verified.

## Apply an offered update

1. Choose `Update to <version>` in the tray menu. Pulse Reader checks again. If
   the offered version has changed, choose the new Update action before any
   download starts.
2. Review and accept the distribution terms if their text differs from your
   saved acceptance. Declining or closing the agreement leaves the application
   running without downloading the update.
3. Wait for the download and verification. The tooltip shows progress, and the
   menu action becomes `Cancel download`. Choose it to cancel before application
   replacement starts. A failed or cancelled download leaves Pulse Reader
   running and the update available for retry.
4. Once the helper accepts the verified package, Pulse Reader closes. The helper
   waits for it to exit, applies the update, checks the resulting installation
   and relaunches Pulse Reader as the original Windows user. The menu disables
   cancellation during `Applying update`; it cannot safely stop an installer
   after installation has started.

Run Pulse Reader as your ordinary user when updating. An all-users installation
requests administrator approval through UAC for the installer. A per-user
installation does not request elevation. Declining UAC prevents installer launch;
the helper attempts to relaunch the coherent existing application. UAC
cancellation and approval using a different administrator passed in a disposable
UAC-enabled guest on 30 October, including relaunch and receipt ownership for
the original ordinary user. See the [qualification record](updater-qualification-20261030.md)
for the Windows consent crash/retry and exact tested package combinations.

Installed updates use Inno Setup with `/NORESTART` and `/RESTARTEXITCODE=3010`.
These prevent an installer-triggered Windows reboot and report a required reboot
as exit code 3010. See [Inno Setup's command-line reference](https://jrsoftware.org/ishelp/topic_setupcmdline.htm).
The helper treats 3010 and every other nonzero exit as failure, not a completed
update. It only permits a new build to relaunch after installer exit 0 and
successful installation checks.

There is no atomic installer rollback. Following a nonzero exit, the helper may
reopen the old build only if its complete owned payload, scope marker,
uninstaller files, original terms and application file/directory layout outside
user-data paths match the captured
pre-installation state. It withholds relaunch if that check fails. Retain
`install.log` and repair with the same official package flavour and scope after
a partial installation. Close other copies using the same application folder before
updating; the helper refuses to replace that folder while another copy runs.

## Keep the same package and data

The updater selects the exact existing package choice:

| Current copy | Selected update |
| --- | --- |
| Standard installer | `win-x64` installer, same user or machine scope |
| Framework-dependent installer | `win-x64-framework-dependent` installer, same scope |
| Standard portable folder | `win-x64` portable ZIP |
| Framework-dependent portable folder | `win-x64-framework-dependent` portable ZIP |

An update keeps saved configuration, downloaded Kokoro and optional models,
optional runtime data, and per-user startup choices. It does not switch package
flavour or turn a portable copy into an installed copy. Framework-dependent
copies still require the .NET 10 Windows Desktop Runtime for x64.

For portable copies, the helper extracts the verified ZIP beside the application
folder and replaces only files listed in the package's `payload-files.txt`.
It rejects unsafe archive paths, user-data entries, and collisions with unowned
files. It journals the replacement and keeps a backup until commit. A failure
before commit triggers rollback; changed or missing recovery files cause it to
retain the journal and backup rather than overwrite the conflicting files.

## Inspect a failed or interrupted update

Each attempted download has an operation directory under:

```text
%LOCALAPPDATA%\PulseReader\Updates\<operation-id>
```

After helper startup, `update-helper.log` records its progress and failures.
Installed updates also write `install.log`. Keep these logs when reporting a
failure. If the helper cannot confirm a coherent installation, it withholds
relaunch and records the need to repair using the official package.

Version 1.0.0 retains operation folders, including the package and helper copy.
After a confirmed successful update and after all helpers have exited, you can
remove that completed operation's folder to reclaim disk space. Keep failed or
interrupted operations and their recovery files until the problem is resolved.

Portable recovery also retains `recovery.json`, the copied `helper` folder, and
an exact recovery command in `update-helper.log`. A power loss or terminated
helper requires manual recovery. There is no automatic recovery at reboot.

1. Close any remaining Pulse Reader process using that portable folder.
2. Keep the operation directory and the `.PulseReader-update-*.json`,
   `.PulseReader-stage-*` and `.PulseReader-backup-*` paths beside the application.
3. From an ordinary-user Command Prompt, run the exact command recorded in that
   operation's `update-helper.log`. It invokes the retained helper with
   `--recover-update` and the operation's recovery capability. Do not substitute
   a helper or capability from another operation.
4. Inspect the appended log. Recovery relaunches a confirmed coherent application.
   If it reports a conflict, retain the files for repair and do not delete or
   edit the journal to force recovery.

Candidate 29's disposable Windows check ran the actual retained helper against a
transaction interrupted after DLL backup and restored/relaunched version 1.0.0.
It used a controlled interruption checkpoint, not a physical power cut.

## Release metadata reference

The updater reads the public GitHub release API for
`qdg-main/pulse-reader`, starting with `/releases/latest`. It accepts
only a newer stable `v<major>.<minor>.<patch>` tag, rejects draft and prerelease
metadata, and uses exact repository, tag and asset names. Before downloading the
application package, it rechecks the selected release by numeric release ID.
It rejects a changed offer rather than silently selecting another version.

The API and release assets must be publicly readable without authentication.
A private source repository can be used for development, but its private
releases cannot supply this updater. A private repository or an unpublished
release may return HTTP 404; that response does not prove public update delivery
works. No GitHub token is shipped in the application. See the
[ownership and launch handoff](releasing.md#repository-ownership-and-launch-handoff)
before publishing the first release.

Every selected asset needs a GitHub `sha256:` digest. Package bytes must match
the declared length and SHA-256. The selected package and manifest must also
agree with `SHA256SUMS.txt`; the checksum list itself is checked against its
GitHub digest. These are integrity and release-consistency checks, not publisher
signature verification. A compromised release publisher could replace both
the package and its metadata.

The full `tools/build-release.ps1` build emits `update-manifest.json` with this
schema. `-PublishOnly` and `-SkipInstaller` do not emit an updater manifest.

| Field | Required value or content |
| --- | --- |
| `SchemaVersion` | `1` |
| `Version` | Stable three-part application version, without `v` |
| `TermsSha256` | SHA-256 of the exact UTF-8 `TermsText` |
| `TermsText` | Complete distribution terms text |
| `Packages` | Exactly two package entries, one for each supported flavour |
| `Packages[].Variant` | `win-x64` or `win-x64-framework-dependent` |
| `Packages[].Installer` | Object containing `Name`, positive byte `Length`, and `Sha256` |
| `Packages[].Portable` | Object containing `Name`, positive byte `Length`, and `Sha256` |

Names follow `PulseReader-<version>-<variant>-Setup.exe` and
`PulseReader-<version>-<variant>-Portable.zip`. Both package objects are required
for both variants; an installer entry cannot be `null`. The parser rejects
missing, duplicate or unexpected fields in these objects.

The full build's `SHA256SUMS.txt` includes the four application packages,
`PulseReader-<version>-DependencySources.zip`, and `update-manifest.json`.
Publish those assets and the checksum list together only after release approval.
Do not upload local payload folders or build diagnostics as updater assets.
