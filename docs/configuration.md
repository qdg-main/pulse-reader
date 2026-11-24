# Configuration and model setup

Most users should change Pulse Reader through Settings. This page documents the saved configuration, Kokoro model location, and startup behaviour for troubleshooting or advanced setup.

## Settings

Double-click the tray icon or choose `Settings` from its menu. Settings can change:

- Kokoro or Windows speech
- Kokoro language, voice, optional second voice, blend weight, speed, and volume
- Optional local contextual pronunciation for approved ambiguous words
- Installed Windows voice, rate, and volume
- The global hotkey
- Floating playback controls
- Start-with-Windows registration
- Automatic update checks

Speech changes apply to the next utterance. Voice choices play a short preview. Hotkey changes use an `Apply` button so Pulse Reader can confirm that Windows accepted the shortcut before replacing the current one.

## Configuration file

Pulse Reader creates its configuration file on first launch:

```text
%LOCALAPPDATA%\PulseReader\config.json
```

The default configuration is:

```json
{
  "speechBackend": "kokoro",
  "kokoroVoice": "af_heart",
  "kokoroVoiceBlendEnabled": false,
  "kokoroSecondaryVoice": "af_sky",
  "kokoroPrimaryVoiceWeight": 50,
  "speechSpeed": 1,
  "speechVolume": 100,
  "contextualPronunciationEnabled": false,
  "checkForUpdatesAutomatically": true,
  "windowsVoice": null,
  "windowsSpeechRate": 0,
  "windowsSpeechVolume": 100,
  "showPlaybackControls": true,
  "playbackControlsPinned": false,
  "playbackControlsPinnedX": 0.5,
  "playbackControlsPinnedY": 0.9,
  "playbackControlsPinAnchor": "monitor",
  "playbackControlsWindowPinnedX": 0.5,
  "playbackControlsWindowPinnedY": 0.9,
  "hotkey": {
    "modifiers": [
      "Ctrl",
      "Shift"
    ],
    "key": "Space"
  }
}
```

Manual edits take effect at the next launch. If the file is missing or invalid, Pulse Reader recreates it with the default Kokoro settings and `Ctrl + Shift + Space` hotkey.

## Update checks

`checkForUpdatesAutomatically` defaults to `true`. Settings exposes it as
`Check for updates automatically`. Packaged copies check for newer stable GitHub
releases while running, no more often than 24 hours after the saved last attempt.
Turning it off stops the timer and cancels an active background check. Manual
`Check for updates` and `Update to <version>` actions remain available in the
tray menu. A background check does not install an update or download its package.

The saved check schedule and cached available version are under
`%LOCALAPPDATA%\PulseReader\Updates\update-check.json`. Update operation logs
are in separate operation folders below the same `Updates` directory.
See [updating and recovery](updating.md) for cancellation, package preservation,
administrator approval and interrupted portable updates.

## Troubleshooting files

Pulse Reader stores settings and diagnostic files for the current Windows user,
including in all-users installations:

| File | Location |
| --- | --- |
| Saved settings | `%LOCALAPPDATA%\PulseReader\config.json` |
| Update check state | `%LOCALAPPDATA%\PulseReader\Updates\update-check.json` |
| Update helper log | `%LOCALAPPDATA%\PulseReader\Updates\<operation-id>\update-helper.log` |
| Installer update log, when an installer runs | `%LOCALAPPDATA%\PulseReader\Updates\<operation-id>\install.log` |
| Optional classifier process output | `%LOCALAPPDATA%\PulseReader\ContextualPronunciation\logs\llama-server.stdout.log` and `llama-server.stderr.log` |

The classifier writes these process-output snapshots after successful startup
or a startup failure.
Release builds do not create window-monitoring traces. A Debug build can record
one when explicitly launched with `--monitor-diagnostics`; see
[monitoring diagnostics](development.md#window-monitoring).

## Installation scope and cleanup

The installer defaults to `Only for me`. That mode needs no administrator
permission and keeps the application and downloaded assets in your account.
`All users` installs the application once for the computer and shares its model
files. Installing or changing shared dependencies needs administrator approval;
ordinary speech and monitoring do not.

The installer offers to launch Pulse Reader in the account that started setup,
including after an all-users installation. If setup was started already elevated
or that normal-user context cannot be verified, launch it from the Start menu
instead. The installer will not start the reader with administrator privileges.

| Item | Only for me | All users |
| --- | --- | --- |
| Application | `%LOCALAPPDATA%\Programs\PulseReader` | `%ProgramFiles%\PulseReader` |
| Kokoro model | `%LOCALAPPDATA%\PulseReader\Models\kokoro.onnx` | `%ProgramData%\PulseReader\Models\kokoro.onnx` |
| Optional classifier model and notices | `%LOCALAPPDATA%\PulseReader\ContextualPronunciation` | `%ProgramData%\PulseReader\ContextualPronunciation` |
| Optional native runtime | Inside the per-user classifier installation | `%ProgramFiles%\PulseReader\OptionalRuntime` |
| Configuration and logs | `%LOCALAPPDATA%\PulseReader` | Same per-user location |

Configuration is created when each user first launches the application, not when
an administrator installs it. Start-with-Windows also remains a per-user choice.
Portable and source builds use per-user storage. The installer records its scope;
moving an installed folder does not turn it into a different installation mode.

Uninstall offers three choices:

1. Remove the application only. Keep downloaded dependencies and saved data.
2. Also remove downloaded dependencies. Keep configuration and logs.
3. Remove all Pulse Reader files, including configuration, logs and temporary
   downloads. Also remove terms-acceptance records from available user registries.

The first choice is the default. An all-users dependency cleanup removes shared
assets used by every account. The full all-users cleanup option warns that it
deletes other users' Pulse Reader settings. Selecting that option and continuing
is sufficient; there is no additional confirmation page or typed phrase. Per-user
cleanup does not remove another account's data or shared machine assets. Cleanup
reports locked, inaccessible or unsafe paths rather than claiming they were
removed.

Empty application and model-storage folders are removed when possible. Folders
containing retained models, settings, logs or other files stay in place.

An all-users full cleanup deletes Pulse Reader files from every registered
normal user profile, including logged-out accounts. Windows startup entries and
terms-acceptance records are cleaned only from already-loaded user registries.
Logged-out users retain those small records. The uninstaller does not load or
modify offline user registry files; that limit also applies to the other cleanup
choices. Unrelated registry values remain untouched.

## Speech fields

`speechBackend` accepts `kokoro` or `windows`.

`kokoroVoice` and `kokoroSecondaryVoice` accept any bundled Kokoro v1.0 voice ID. `kokoroVoiceBlendEnabled` enables the second voice. `kokoroPrimaryVoiceWeight` accepts `0` through `100`; the second voice receives the remaining percentage. Blending voices from different languages is allowed, although strong cross-language blends can introduce speech artifacts.

`speechSpeed` accepts `0.75` through `1.25`, and `speechVolume` accepts `0` through `100`. These settings apply to Kokoro.

`contextualPronunciationEnabled` enables the optional local context classifier for Kokoro. It is `false` by default and has no effect on Windows speech.

`windowsVoice` accepts the name of an enabled local Windows speech voice or `null` for the Windows default. `windowsSpeechRate` accepts `-10` through `10`, and `windowsSpeechVolume` accepts `0` through `100`. Pulse Reader uses the Windows default if a saved voice is no longer installed.

Kokoro and Windows settings are saved independently. Using Kokoro still leaves Windows speech available as an automatic fallback.

## Optional contextual pronunciation

Pulse Reader can use a local Qwen3 4B classifier for five approved ambiguous words: `axes`, `bass`, `lead`, `wind`, and `live`. The model selects one closed meaning label for each occurrence. Pulse Reader then applies a fixed, listening-approved Kokoro phoneme. The model cannot rewrite the selected text.

Enable `Use local model for ambiguous words` in Settings to download and verify the pinned CPU runtime and model. Setup shows progress and lets you cancel or retry. The setting is saved only after setup succeeds; no application restart is needed. The download happens only when you enable this option.

If the saved setting is enabled but its installation is missing or structurally
invalid at startup, Pulse Reader saves the setting as off and explains how to
download it again through Settings. It does not start an optional download on
launch. Temporary access errors, inference timeouts and memory limits do not
reset the setting. Turning the feature off leaves its downloaded files in place.

For source-build or manual setup, the existing script remains available:

```powershell
tools\setup-contextual-pronunciation.ps1
```

The source/manual script verifies the pinned downloads and installs them for
the current user under:

```text
%LOCALAPPDATA%\PulseReader\ContextualPronunciation
```

For an all-users installation, use the application setup dialog instead. It
installs the shared model and protected native runtime with administrator
approval. The per-user script does not populate that shared installation.

The model file is about 2.5 GB. It uses CPU and system memory rather than GPU memory. Pulse Reader starts it only when an approved word appears and Windows has enough headroom for the measured model footprint. Once loaded, the process remains warm while memory is healthy. Pulse Reader releases it when system memory load reaches 90%, available physical memory falls below the greater of 3 GB or 10% of installed RAM, the setting is disabled, or the application exits. If there is not enough memory to start it safely, Pulse Reader keeps the existing deterministic pronunciation.

Pulse Reader processes every approved occurrence in the selection. Playback begins with any unaffected opening units while contextual classification continues ahead of it. Target-bearing units are resolved in reading order and released to Kokoro individually, together with any unaffected units before the next target, so later classifications continue while earlier audio is already playing. Kokoro waits only if playback catches the next unresolved boundary. The first classification after the local model starts has a ten-second allowance; warm classifications retain a two-second limit. If the model is missing, either timeout expires, cancellation occurs, or the response is not exactly one allowed label, Pulse Reader keeps the existing deterministic text. Kokoro remains available and the optional model cannot trigger Windows speech fallback.

## Playback controls and position

`showPlaybackControls` controls whether the floating bar appears for selected-text playback and window monitoring. Disabling it does not change the global hotkey or tray-menu stop command.

`playbackControlsPinned` records whether the bar has a saved position. `playbackControlsPinAnchor` is `window` when that position is inside a detected source window, or `monitor` when it is outside one. The `playbackControlsPinnedX` and `playbackControlsPinnedY` values store the monitor-relative position from `0` through `1`; they also provide the fallback if a window-relative pin is used when no source window is available. `playbackControlsWindowPinnedX` and `playbackControlsWindowPinnedY` store the equivalent source-window-relative position. Pulse Reader manages these fields when the pin is used; manual editing is normally unnecessary.

An unpinned bar anchors near the bottom center of the source window when Windows exposes usable window bounds. Otherwise, it uses the bottom center of the active monitor. Dragging changes the position for the current playback session. Pinning inside the source window keeps the same relative window position on later reads; pinning outside it keeps the same relative monitor position. A visible bar does not follow a window that moves or resizes. If the window changes, Pulse Reader only recalculates the saved relationship after the bar is deliberately dragged.

## Window monitoring

Temporary selected-text playback uses the same visible control bar at the selection's source window. Only one bar is shown: the monitored window's bar returns afterward, retaining its session drag position. Pin preferences remain shared. Pause and resume on the manual bar affect that reading, not the monitoring pause preference. Its close button stops the manual reading without detaching the monitored window; the monitoring bar's close button still detaches.

While manual text is queued, preparing, paused, or playing, a normal hotkey press cancels that manual interaction before attempting another copy, regardless of what is currently selected. A subsequent press captures the new selection. Canceling a pending copy allows clipboard restoration to finish before another capture starts. Monitored updates remain queued and resume after the temporary reading unless monitoring was already paused or its context is unavailable.

If monitoring was paused partway through a sentence, manual reading temporarily parks that sentence. Resuming monitoring afterward restarts it from the beginning; manual playback does not unpause the feed.

Window monitoring is activated per session rather than stored as a persistent setting. Hold the configured hotkey for about 650 ms to attach to the active window. The control bar appears at the threshold without waiting for release, then shows the listening state once capture is ready. A short rising three-pulse sound confirms successful attachment instead of a Windows notification. Holding the hotkey again on the same window detaches with the falling version; the close button and tray-menu stop action remain silent. If text was selected, copying that selection waits until the keys are released. Hold the hotkey on another window to transfer the session. A normal hotkey press retains the existing selected-text behaviour. If attachment or the capture worker fails, a Windows notification still reports the problem; hold the hotkey again to reattach.

In Codex, changing conversation views stops the old monitored speech and treats the new view's existing text as a fresh baseline. Scrolling through an existing final answer does not read it automatically; select that text and use the normal hotkey to read it. Leaving the conversation view suspends monitoring without detaching; returning resumes after the view settles. Hiding the sidebar or opening a non-modal thread-status popover does not interrupt monitoring; the popover's contents are not read. Full modal dialogs still suspend monitoring. An uncertain view rebuild is also baselined conservatively, so updates arriving during that transition may not be read. Manual selections and an explicit playback pause remain under your control.

In LM Studio, leave the thinking panel collapsed to hear only the answer. Expand it during generation to also hear thinking. Collapsing finishes the current sentence, and reopening resumes unread thinking without replaying completed sentences. Once the answer starts arriving, remaining thinking is skipped; expanding an older completed response does not automatically read its thoughts.

In T3 Code, attach with one chat visible and stay near the bottom of its timeline to hear new assistant updates and replies. Your messages, tool activity, and changed-file summaries stay silent. Changing chats or returning from settings starts with a fresh baseline, so existing messages aren't replayed. Monitoring pauses when more than one chat pane is visible.

In Discord, Pulse Reader reads each new message's display name and body, excluding timestamps, badges, reactions, and reply previews. Changing channels establishes a fresh baseline without reading the existing history. Channels, threads, DMs, and voice-channel text panels have passed listening tests. Own-message suppression uses recently observed composer text, so a message typed and sent between captures can still be read back.

Monitoring depends on the text the application exposes through accessibility. A layout change or content that is not exposed can cause missed updates. These app-specific checks do not guarantee capture in every application or version.

## Hotkey fields

Valid modifier names are `Ctrl`, `Alt`, `Shift`, and `Win`. The `key` value uses a Windows key name such as `R` or `F8`.

If another application already owns the configured hotkey, Pulse Reader keeps running and reports that it could not register the shortcut.

## Kokoro model

Pulse Reader uses the CPU-only `KokoroSharp.CPU` runtime. The NuGet dependency supplies all 54 standard Kokoro v1.0 voice files in the application output.

On first launch without a model, Pulse Reader offers Kokoro setup with progress, cancellation and retry. Choosing Windows voice skips that download. Selecting Kokoro later opens setup again. For per-user, portable and source installations, the full-precision model is saved at:

```text
%LOCALAPPDATA%\PulseReader\Models\kokoro.onnx
```

All-users installations use `%ProgramData%\PulseReader\Models\kokoro.onnx`.
Once shared setup completes, other accounts use that model without downloading
another copy. Shared setup uses a separate administrator-approved helper;
speech playback and monitoring stay in the normal user session.

The model is about 320 MB. Future requests use the local file and do not need an internet connection. If the download, model load, voice load, or synthesis fails, Pulse Reader uses Windows speech for that selection and displays a fallback notification.

To install the model before running a per-user or source copy of Pulse Reader, download [`kokoro.onnx` from the KokoroSharp model release](https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx) and save it at the per-user path above. For example:

```powershell
$modelDirectory = Join-Path $env:LOCALAPPDATA 'PulseReader\Models'
New-Item -ItemType Directory -Path $modelDirectory -Force | Out-Null
Invoke-WebRequest `
    -Uri 'https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx' `
    -OutFile (Join-Path $modelDirectory 'kokoro.onnx')
```

## Start with Windows

`Start Pulse Reader when I sign in` registers the current executable for the current Windows user. It does not require administrator access and is stored in Windows rather than `config.json`.

If the application folder moves, open Settings and enable the option again to update the registered path. Windows can still disable Pulse Reader through its Startup apps controls.
