# Pulse Reader 1.0.0 release notes

Draft for release review. Version 1.0.0 has not been published. The planned
GitHub Releases preview will be unsigned. Final antivirus assessment, clean
Windows qualification, published update delivery and publication approval remain open.
See [release preparation](releasing.md) before uploading these notes or packages.

Pulse Reader is a Windows tray application that reads selected text aloud with
local Kokoro speech. Select text, press `Ctrl + Shift + Space`, and press the
same shortcut again to stop. Floating controls provide pause and resume; Kokoro
also supports reading-unit navigation and restart without switching back to Settings.

## Choose a download

| File | Use this when |
| --- | --- |
| `PulseReader-1.0.0-win-x64-Setup.exe` | Recommended. Install Pulse Reader with its .NET runtime included. |
| `PulseReader-1.0.0-win-x64-Portable.zip` | Extract and run without using an installer. Includes the .NET runtime. |
| `PulseReader-1.0.0-win-x64-framework-dependent-Setup.exe` | Use the smaller installer if the .NET 10 Windows Desktop Runtime for x64 is already installed. |
| `PulseReader-1.0.0-win-x64-framework-dependent-Portable.zip` | Use the smaller portable folder with that same runtime installed separately. |

The smaller packages need the Windows Desktop Runtime, not just the .NET runtime
or ASP.NET Core runtime. An SDK is not required to use any release package.
Extract the entire portable ZIP before running `PulseReader.exe`.

Windows 11 x64 is the tested platform. The installer also accepts Windows 10
build 19041 and newer, but Windows 10 has not been release-tested. Use an
operating-system version supported by .NET 10.

The installer defaults to your Windows account. An all-users installation
requires administrator approval and shares model downloads across accounts;
each account keeps its own settings and startup preference.

`SHA256SUMS.txt` accompanies the downloads. Compare the downloaded file's SHA-256
with its entry before running it. The dependency-source ZIP is provided for
source and licence review; it is not needed to run Pulse Reader.

Unsigned preview: Windows may show an unknown-publisher or SmartScreen reputation
warning. Verify the matching SHA-256 checksum before running.

## First launch

Setup offers to download the roughly 320 MB Kokoro model, with progress,
cancellation and retry. You can choose an installed Windows voice instead.
Kokoro falls back to Windows speech if model loading or synthesis fails.

The optional `Use local model for ambiguous words` setting is off by default.
Enabling it downloads roughly 2.5 GB of model and runtime data. It helps choose
approved pronunciations for `axes`, `bass`, `lead`, `wind` and `live`; it does
not rewrite your text. Turning it off keeps the downloaded files.

Uninstall keeps models and settings unless you choose to remove them. Full
all-users cleanup warns that it removes other users' Pulse Reader files.
Logged-out accounts retain their small Windows startup and terms-acceptance
records. See [installation and cleanup](configuration.md#installation-scope-and-cleanup).

## Read selections or follow new text

Hold `Ctrl + Shift + Space` briefly to monitor the active window. A short sound
confirms attachment. Selected text is read first; otherwise, existing content
becomes the baseline and only new content is spoken. Hold the shortcut again
on the same window to detach.

Monitoring includes application-specific handling for Codex, LM Studio and
Discord. In LM Studio, expand the thinking panel to include thinking, or leave
it collapsed to hear only the answer. Discord reads the display name and message
body without timestamps or reactions.

You can read a selection in another window without losing the monitored window.
The control bar moves to that temporary reading and returns afterward.

## Local speech and network activity

Speech and optional pronunciation classification run on the CPU, locally.
After model setup, speech works offline. Setup downloads and stable-release
checks require internet access; they do not upload selected or monitored text.

Packaged copies check quietly for updates once a day while running. Disable
`Check for updates automatically` in Settings to opt out. Downloading and applying
an application update requires choosing the tray-menu Update action. Updates
retain the existing package flavour, installation scope, models and settings.

## Known limitations

- Capture and monitoring depend on the source application's accessibility
  support. Elevated applications, inaccessible controls and changing layouts can
  prevent capture or cause missed updates.
- Pronunciation corrections are conservative, and the optional classifier can
  still choose the wrong meaning. Windows speech receives the original text.
- Pulse Reader preserves ordinary clipboard formats but does not manage Windows
  Clipboard History. Some delayed or application-specific formats may not survive
  temporary capture.
- Interrupted portable updates require manual recovery using the retained helper
  and logs. Installer updates do not provide atomic rollback. See
  [updating and recovery](updating.md).

Pulse Reader's source is [MIT licensed](../LICENSE). Bundled dependencies and
downloaded models retain their own licences and notices. Optional donations through
[GitHub Sponsors](https://github.com/sponsors/Mark-A-Richardson) support development;
they do not unlock features.

For setup or playback problems, see [troubleshooting](troubleshooting.md).
