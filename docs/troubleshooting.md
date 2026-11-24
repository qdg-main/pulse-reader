# Troubleshoot Pulse Reader

Start with the smallest text sample that reproduces the problem. Note your
Pulse Reader version, Windows version, source application, speech backend,
voice, speed and whether local ambiguous-word correction is enabled. Remove
private content before sharing a sample or log.

## The application seems not to open

Pulse Reader lives in the Windows notification area. Check its hidden-icons
menu, then double-click the icon or choose `Settings` from its right-click menu.
Only one instance can run in a Windows session.

Extract the complete portable ZIP, rather than launching from inside it or
copying only `PulseReader.exe`. For a framework-dependent package, install the
.NET 10 Windows Desktop Runtime for x64. The ordinary .NET runtime alone does
not supply Windows Forms. The standard installer and ZIP include their runtime.

## The shortcut does not read a selection

Try a short, copyable sentence in a normal Windows text editor first. Confirm
the configured shortcut in Settings; the default is `Ctrl + Shift + Space`.
Pulse Reader reports a shortcut-registration failure if another application
already owns it.

Some applications do not expose a copyable or accessible selection. Pulse Reader
has a bounded UI Automation fallback, but no OCR. Reading from an application
running as administrator may also fail. Do not elevate Pulse Reader just to
work around an unsupported application.

## Windows speech plays instead of Kokoro

The fallback notification means Kokoro could not download, load or synthesize
the selection. Try a simple sentence and confirm Kokoro is selected in Settings.
If setup failed, use the setup dialog's retry option after checking connectivity
and available disk space. Windows voice remains available without a model download.

The Kokoro model normally lives at
`%LOCALAPPDATA%\PulseReader\Models\kokoro.onnx`. An all-users installation uses
`%ProgramData%\PulseReader\Models\kokoro.onnx`. Use the application setup flow to
repair a shared installation; do not copy executable runtime files into that
shared model directory or change its permissions.

## Optional pronunciation correction is unavailable

Enable `Use local model for ambiguous words` in Settings to install or repair
its pinned model and CPU runtime. The option is saved only after setup succeeds.
If a saved enabled installation is missing at startup, Pulse Reader switches
the setting off and explains how to install it again. Turning it off does not
delete the download.

The classifier needs enough available system memory. A timeout or low-memory
condition leaves the deterministic pronunciation in place; it does not force
Windows speech fallback. Its per-user process logs are under
`%LOCALAPPDATA%\PulseReader\ContextualPronunciation\logs`.

## Monitoring misses or repeats text

Keep the intended conversation or channel visible and at its newest content.
Virtualized or offscreen messages may not be exposed through accessibility.
Opening an existing conversation establishes a baseline; old messages are not
automatically read. In LM Studio, expand the thinking panel only if you want
thinking read aloud.

If an application update changes its accessible layout, keep a small repeatable
example and the source application version. Release builds do not expose the
developer monitoring-capture log. Do not post screenshots or logs containing
private conversation text.

## An update fails

Pulse Reader retains update details under
`%LOCALAPPDATA%\PulseReader\Updates\<operation-id>`. Keep `update-helper.log` and,
for installed updates, `install.log`. A download failure leaves the application
running; you can retry from the tray menu.

After replacement has begun, do not remove the operation folder or the portable
journal, staging or backup files. Follow [update recovery](updating.md#inspect-a-failed-or-interrupted-update).
An installer failure may require repair with the same official package flavour
and installation scope. Do not edit a recovery journal to force a launch.

## Antivirus or Windows blocks a package

Do not disable protection or create an exclusion as a troubleshooting shortcut.
Check that the package came from the project's official release and compare its
SHA-256 with `SHA256SUMS.txt`. Keep the detection name, affected file, file hash
and security-product version for investigation. Matching a published checksum
establishes file identity, not an antivirus-clean result.
