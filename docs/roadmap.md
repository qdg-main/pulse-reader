# Project roadmap

The current feature set is sufficient for the first release. The focus is everyday testing, repeatable bug fixes, and preparing a distributable build. This checklist is not a release schedule.

## Completed foundation

- [x] Add Windows voice selection, speed, volume, and preview
- [x] Audit cold-start and warm speech latency
- [x] Add start-with-Windows support
- [x] Add floating playback controls
- [x] Add automatic placement, dragging, and window- or monitor-relative pinning
- [x] Add deterministic reading-unit analysis and an evaluation corpus
- [x] Connect reading-unit navigation and restart controls to playback
- [x] Apply conservative spoken-text preparation after original-versus-prepared listening tests
- [x] Benchmark and integrate an optional local context classifier for approved pronunciations
- [x] Add UI Automation text capture fallback
- [x] Add optional window-monitoring mode

## Testing and release readiness

The release-readiness branch was merged and pushed to `main` at `f9cd005`,
targeting version 1.0.0. First-run setup, optional-model downloads, both
installer/portable package variants, and the reviewed notices/runtime-consent
route are implemented. The updater is implemented for version 1.0.0;
Candidate 29 passed the full suite, all four built-asset
catalog checks and actual self-contained helper/per-user installer and retained
portable recovery checks. Candidate 31's controlled app-context UI flows also
passed, and the maintainer approved their screenshot walkthrough on 30 October
2026. Update-specific cancellation wording is fixed in source. The
[30 October follow-up](updater-qualification-20261030.md) passed live
framework-dependent routes, shipping startup, pointer cancellation and
self-contained all-users UAC with a different administrator, after a retained
Windows consent crash/retry. Published asset delivery and broader
platform/power-loss coverage remain limited. Remaining package acceptance and
publication approval are still required. See [release preparation](releasing.md)
for the current gates and [updating](updating.md) for the implemented behaviour.

- [ ] Exercise the Release build in everyday use, including selection capture, playback navigation, window monitoring, and switching applications or conversations
- [ ] Resolve repeatable issues found during testing, with regression tests and listening checks where needed
- [ ] Test the published Windows x64 build in a clean user profile, including first-run model download, interrupted-download recovery, and later offline use
- [ ] Verify saved settings, hotkey registration, and enabling or disabling start-with-Windows from the intended release location
- [x] Prepare all four Candidate 30 packages and a [1.0.0 release-note draft](release-notes-1.0.0.md) with download choices and known limitations; publication remains gated
- [x] Implement package-flavour-preserving stable release checks and a tray-menu Update action
- [x] Qualify Candidate 29's self-contained copied helper, per-user installer success/failure relaunch and interrupted portable recovery in disposable Windows guests
- [x] Qualify Candidate 31's controlled updater UI flows and obtain maintainer approval of the screenshot walkthrough
- [x] Qualify framework-dependent portable/per-user updates, shipping startup, actual pointer cancellation and self-contained all-users UAC with a different administrator; retain failed-attempt evidence
- [ ] Qualify live public-asset delivery once approved assets exist, and review or accept remaining platform/DPI/power-loss limits
- [ ] Add contribution and bug-reporting guidance after the shared project strategy is chosen

Use the [build, test, and publish guide](development.md) for commands. These release checks remain open even where development-build tests have passed.

When reporting an issue, keep the smallest text sample that reproduces it and note the application, speech backend, voice, speed, contextual-pronunciation setting, and build tested. Describe what was expected and what was heard. Remove private content before sharing samples.

## Outside the current plan

- OCR capture for non-selectable text
- Additional opt-in processing for cases the local pipeline cannot handle

Neither is a prerequisite for release. The existing local context classifier remains supported.

## Deliberately deferred

- GPU speech acceleration
- More complete Windows Clipboard History preservation and cleanup
- Advanced pitch, emotion, and pronunciation controls
