# Build, test, and publish

This page covers the source-build and validation commands used by Pulse Reader contributors.

## Requirements

- Windows x64. Windows 11 is recommended; Windows 10 build 19041+ is accepted by the installer but has not been release-tested.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An installed Windows speech voice for fallback and Windows smoke tests
- An internet connection for the first Kokoro model download, unless the model is already installed

## Build and run

From the repository root:

```powershell
dotnet build PulseReader.slnx --configuration Release
dotnet run --project src/PulseReader/PulseReader.csproj --configuration Release
```

The built executable is:

```text
src\PulseReader\bin\Release\net10.0-windows\PulseReader.exe
```

Pulse Reader permits one running instance per Windows session. A second launch displays a message and exits.

## Selection capture

Pulse Reader first asks a standard Windows editor control to copy its selection directly. Custom controls fall back to a normal `Ctrl+C` operation. The capture service snapshots regular clipboard formats before copying and tracks the Windows clipboard sequence number throughout the transaction. It refuses to clear a snapshot that has already gone stale and does not restore over clipboard content copied by the user or another application while capture is in progress. A partial snapshot remains usable, but Pulse Reader reports that application-specific formats may not have survived. The keyboard fallback runs only when the direct copy leaves the clipboard unchanged.

If those copy paths return no text, or the clipboard is busy without being superseded, Pulse Reader tries the selected control's Windows UI Automation `TextPattern`. Automation calls run away from the tray UI thread and have a 500 ms caller timeout. Only one provider read may remain active, so a hung third-party provider cannot accumulate background work. The original source window must remain active before and after the read. A clipboard sequence superseded by the user or another window disables this fallback for that capture.

The keyboard-copy fallback releases sampled modifiers without pressing them again afterward. It supplies a paired left-Ctrl press/release for a clean `Ctrl+C`, also guarding Alt/Windows release gestures. This avoids restoring a stale key-down after the user has physically released the hotkey. If the user keeps holding a capture modifier, they may need to release and press it again for a subsequent shortcut. A partially accepted input batch triggers bounded key-up-only cleanup for unmatched injected presses, never a retry of the copy command. Cleanup cannot guarantee success if Windows blocks further input. Run `--copy-shortcut-checks` for simulated modifier-release timing and partial-injection coverage; these tests do not send desktop keystrokes.

## Window monitoring

Run `--manual-monitoring-checks` for manual-selection hotkey routing, queue cancellation before and during preparation, late completion rejection, monitoring pause isolation, and the control island's temporary position handoff. Manual queue items carry their source placement and a cancellation generation separately from the monitored view generation; changing a monitored thread does not cancel a manual selection from another window.

Discord suppresses submitted-message echoes using the observed message composer, including fast focused-editor samples. It matches the composer's complete word sequence to a new message body after the composer clears, so custom server nicknames do not affect matching. Matching is single-use, expires after five seconds, and resets on conversation changes or an unavailable/ambiguous composer. Only salted fingerprints are retained. Display-name equality alone never suppresses a post. This remains a heuristic: a draft sent entirely between samples may be missed, while manually clearing a draft followed immediately by another person's identical post can be indistinguishable from sending it.

For Discord authors, only the username control supplies spoken text. Consecutive posts inherit that clean name from their message group. A bounded, channel-scoped lookup retains verified names when headers are virtualized; the combined author-and-badge accessibility label is only a lookup key, never speech. An unresolved author is withheld rather than guessed. Badge wording is not removed from genuine message bodies.

Discord uses a separate, discrete-message path. The adapter accepts one visible message list, identifies the channel from its message IDs, and reads each post as display name followed by body text. Empty conversations use the document's Discord channel route until message IDs become available. Exact message-body IDs exclude quoted reply previews, timestamps, role badges, reactions, and surrounding interface text. Missing or ambiguous conversations and visible dialogs suspend capture.

Discord posts settle for 700 ms and are tracked by message ID rather than text similarity. Edits and redraws do not replay posts; distinct identical posts remain distinct. After echo suppression, bodies use the same reading-unit analyzer as selected text, with the author prefixed only to the first unit. Manual selections can interrupt at a unit boundary and then return to the remaining post. The baseline uses Discord's documented snowflake timestamp to reject older history loaded by scrolling. Channel changes discard pending old-context speech and silently baseline the destination. Attachment-only posts are silent. User listening tests passed for channels, threads, DMs, and voice-channel text panels, including own-message suppression. Empty-channel identity and first arrival have regression coverage and the document route was verified against a populated channel; live empty-channel arrival and other languages still need validation. Run `--discord-checks` for deterministic tests or `--discord-live-check <HWND>` for a 25-second read-only arrival check; these checks do not send Discord messages or save chat text.

Generic document fallback rejects editable or hidden documents and editable ancestors. Whole-document ranges containing editable descendants are withheld because their text cannot be safely separated. The native `--document-fallback-checks` fixture verifies that drafts and subsequent typing stay silent while read-only document text remains available.

Discord attribution stays separate from the message body through the speech queue and backend controller. Deterministic formatting runs once on the whole body before it is split; requests retain prepared unit kinds, speech text, and pauses, with locally rebased source ranges and navigation indexes. Silent structural units, such as table separator rows, are not queued. Kokoro adds the author to the first retained unit without reparsing the body. This preserves table and quotation context and prevents names from breaking leading JSON, code fences, or list markers. Windows speech and fallback retain the original author-and-body text of each retained unit. The `--kokoro-playback-lifecycle` checks also cover navigation on both sides of final-segment completion: the audio player must accept a new generation before the session changes state, and completed audio output is released before idle is reported.

A held hotkey attaches a background UI Automation session to the original top-level window. The monitor subscribes to structure, name, text, live-region, and asynchronous-content events and also takes a bounded watchdog snapshot every two seconds. The initial snapshot is a baseline. Later snapshots are compared by runtime ID and text so provider redraws do not replay unchanged content. Descendants are intentionally scoped by the source UI Automation tree rather than process ID because Chromium and Electron can expose hosted text from a different process.

Attaching to a recognised Codex or LM Studio response, or baselining one after a view change, activates response-wide duplicate protection immediately. It does not wait for the next turn. Codex's standalone accessibility descriptions are excluded from speech so changing progress metadata cannot make genuine paragraphs look volatile. Descriptions inside response paragraphs and in other applications remain eligible. Recognised user bubbles still establish turn boundaries when Codex omits the hidden user heading. A visible working timer inside the scoped transcript and an active composer identify a live assistant section when the user bubble is missing or belongs to an earlier reply. Only text after that timer is eligible. If the next continuation starts in the same frame as the current final answer, the final reaches the speech queue before the new response opens. A streaming aggregate that temporarily shrinks to a prefix retains its last captured text, so restoring its nodes does not replay a sentence tail. Run `--codex-latch-checks` for attachment and redraw regressions and `--monitoring-document-checks` for cached transcript coverage.

Codex conversation identity prefers the visible `_transcriptContent_` container. Some layouts omit the `thread-scroll-container` ancestor; relying on that wrapper alone incorrectly enables generic capture and reads history when switching chats. Older layouts retain the scroll-container fallback. Multiple visible transcripts or a modal suspend capture rather than selecting an arbitrary conversation. The sidebar, composer, and surrounding layout wrappers do not establish a transcript identity.

After identifying the visible conversation, Codex capture reads mounted text throughout that container, including paragraphs below the viewport. It does not scroll the application or expand capture into hidden conversations. Text that Codex removes from its accessibility tree still cannot be recovered until it is exposed again. The explicit live check `--codex-capture-check <window-handle> "<synthetic sentence>"` verifies that the latest fixture sentence reaches the real snapshot reader; use an offscreen test sentence to exercise viewport-independent capture. It does not print captured conversation text.

LM Studio has a separate adapter scoped to its `chat-terminal-messages` raw-view container. A cached UI Automation snapshot avoids per-property cross-process scans, and the surrounding `chat-terminal-scroll-container` supplies the thread identity even before the first message exists. The adapter uses message headers and action controls to separate user messages, thinking, answer paragraphs, and generation statistics. Only the latest turn is eligible; offscreen paragraphs exposed by that container retain their turn identity. It shares the conversation settlement and streamed-answer handoff rules with Codex, without applying Codex CSS selectors to LM Studio. Missing or ambiguous thread containers and visible dialogs suspend capture rather than falling back to reading the whole interface. This is based on the observed English-language LM Studio layout; other layouts still need validation.

LM Studio thinking is eligible only when its expansion control explicitly reports `Expanded`. The collapsed streaming preview remains silent. A bounded thinking buffer remembers started reading units across collapse/reopen; collapse finishes the current unit and makes unread thoughts ineligible until reopened. Preparation that has not reached playback is canceled. The first answer content retires thinking for that turn, and opening historical thoughts does not restart them. Table headers and cells are captured in row order with sentence separators. Append-only fragments from recognized conversations bypass generic short-status settlement so continuous token streams do not wait indefinitely for silence.

LM Studio's message-local generated-response counter supplies a persistent generation revision. This distinguishes a regenerated answer even when it finishes between captures; it does not depend on observing the transient `Stop generating` control. Browsing an older alternative suspends capture, and returning to the latest version establishes a silent baseline. Live checks on LM Studio 0.4.25 covered empty-chat capture, first replies, settings return, ordinary and near-instant regeneration, historical-version browsing, expanded-thinking collapse/reopen, stopping generation, and changing chats during playback. The diagnostic trace confirmed that the old speech queue stopped after a chat change and the next reply was read without relatching.

Text nodes inside editable accessibility controls are excluded, including browser and Electron editors that expose the typed text as nested static-text nodes. A bounded, per-session fingerprint cache also suppresses recently submitted editor text if the provider recreates it as a static message. Stable status elements that replace themselves repeatedly are treated as volatile, while sibling text fragments from the same accessibility container are joined before speech and their later consolidated redraw is not repeated. Generic accessibility roles and semantic class hints exclude timestamps, outgoing-message containers, hidden labels, progress effects, and auxiliary tool activity without matching application names.

Appended output is accumulated until a complete sentence is available; an incomplete generic fragment is released after 700 ms without further changes. Codex working updates wait three seconds per completed sentence before publication. Later tokens do not restart an unchanged sentence's timer; a rewrite replaces a pending version rather than speaking both. Final answers and other applications retain their existing timing. Together with the playback startup buffer, the initial Codex thought typically waits about four seconds. Run `--thought-settlement-checks` for deterministic settlement and session-integration coverage. Monitoring speech uses a unit queue so pause can buffer arrivals and a manually selected passage from another window can take priority after the current unit. A 100,000-character unread-queue limit pauses playback and warns the user. Closing the source, using the monitoring bar's stop control, or choosing **Stop monitoring** from the tray disposes the watcher without blocking the tray UI thread.

For a bounded accessibility-structure investigation, use a Debug build and launch it with `--monitor-diagnostics <path>`. This developer-only mode is compiled out of Release builds. It records control types, roles, bounds, change classifications, and salted fingerprints to a JSON Lines file. It never writes observed text or automation identifiers. The random salt is held only for that process, allowing identical or split text to be correlated within one diagnostic run without creating reusable text hashes. Signal counts are aggregated into the next snapshot, and unchanged accessibility nodes are omitted, to keep high-frequency providers from producing oversized logs.

Diagnostic runs also write `<path>.performance.jsonl` every five seconds. Samples
include capture/processing/logging stage timings, event counts, snapshot sizes,
active workers and their current stage duration, speech queue state, and numeric
CPU/memory/handle/thread metrics for Pulse Reader and the source process tree.
The process sample includes the source and matching GUI-executable descendants,
capped at twelve source processes plus Pulse Reader. Terminal/tool subprocesses
are excluded. Process
sampling uses no extra accessibility reads and does not collect titles, command
lines, paths or conversation text. Sampling continues for the last source after
unlatching so recovery is visible; a worker remains counted until provider
cleanup actually returns. CPU values are cumulative milliseconds, not percentages.
`diagnostic-snapshot` timing separates structural-log overhead from capture work.

The structural log retains three files of at most 16 MiB each, and performance
logging retains three files of at most 4 MiB each. The current file has the
requested name; `.1` is the preceding segment and `.2` is the oldest. An oversized
individual record is replaced with an explicit omission marker. These limits
bound disk usage, not the cost of collecting or hashing a snapshot. I/O failure
disables that writer without stopping monitoring. Run
`--monitoring-performance-checks` in Debug for rotation, privacy, worker lifecycle
and post-detach sampling checks. These diagnostics measure behaviour; they do
not change capture frequency, settlement or pronunciation.

For a low-overhead capture investigation, launch Debug with
`--monitor-performance-only <path>` instead. This retains numeric metrics and
salted text/publication fingerprints but does not request detailed structural
observations. Capture stage labels distinguish application detection, visible
and conversation enumeration, element/ancestor inspection and response activity.

Monitoring startup has an eight-second deadline. A timeout or caller
cancellation invalidates startup and is checked between capture phases, nodes
and ancestors, including when a provider call finally returns. No further
baseline/subscription work should follow a timed-out read. A process-wide capture
gate remains held until that worker and provider cleanup finish; a retry waits
briefly, then reports that the previous capture is still finishing. An in-flight
synchronous provider call cannot be forcibly interrupted by token cancellation.
`--monitoring-performance-checks` covers late startup rejection, caller
cancellation and retry admission using a blocked synthetic provider without
querying another application.

Codex document wrappers are confirmed by a visible transcript landmark within
that document, using the same class-token validation as conversation capture.
For these wrappers, monitoring still checks explicit Edit/password controls and
writable ValuePattern support, but skips the document-wide TextPattern read-only
attribute. The native
[attribute API](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextrange-getattributevalue)
evaluates the entire range, including hidden text. In a recorded failing thread,
this query alone took 16.6 seconds; repeated scans also prevented useful speech
capture. Nested editors and generic documents retain their own editability checks.
Recognition is not inferred from the executable name or title and is cached only
for the current snapshot's element inspection. Run `--monitoring-document-checks`
for the policy and synthetic writable/read-only document regressions. A successful
test does not establish that every future Codex layout exposes the same landmarks.

Codex capture first discovers visible transcript and dialog landmarks using only
cached structural metadata. It then reads text only beneath the unique selected
transcript and stops ancestor inspection at that container. The former
window-wide visible-text discovery pass is skipped, including for embedded
browser panels and sidebars. A recognised Codex view with no unambiguous
transcript remains suspended instead of falling back to generic window text.
The composer activity query selects only the known composer buttons; Codex user
bubbles supply submitted-message boundaries without a window-wide editor probe.
Run `--monitoring-document-checks` for transcript, hidden/ambiguous view, modal,
popover and generic-fallback routing coverage.

The selected transcript is fetched with a raw-view subtree cache in
`AutomationElementMode_None`. Parsing reads only cached properties and child
arrays; managed ancestor paths supply the existing context filters without
live parent walks, per-ancestor cache requests or TextPattern range queries.
Each snapshot gets a new tree, bounded to 20,000 parsed nodes, 128 levels and
2,000,000 text characters. Explicit editors, passwords, writable/unknown value
metadata and embedded documents are excluded; generic applications retain their
existing editability checks. Mounted transcript content below the viewport stays
eligible. The bulk provider request itself remains synchronous and cannot be
interrupted mid-call. `--monitoring-document-checks` covers local cached frames;
`--codex-scope-check <window-handle>` is an explicit live check that also rejects
any live parent, ancestor or editability-pattern stage in the Codex path.

Capture runs on a separate MTA worker, but UI Automation still calls into the
target application's provider. Narrow requests reduce that application's work;
moving the client to another process cannot guarantee an unresponsive provider
will leave its own UI responsive. The startup deadline and capture gate contain
Pulse Reader's retries, not the duration of a native call already in progress.

Ancestor classification fetches type, class, role, automation ID, password,
visibility and ARIA properties in one element-scoped cache request, then applies
the existing filters to those local values. The cache is discarded each snapshot;
text ranges and the composer are not cached across captures. This follows the
[UI Automation bulk-fetching model](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-cachingforclients)
and removes repeated remote property reads without increasing the startup deadline.

The same request caches ValuePattern/TextPattern availability and ValuePattern's
read-only flag. Unsupported patterns are not requested live. A supported cached
writable value still excludes that control; available text patterns retain the
range-level check where required. Unknown or unsupported cache values fall back
to the existing live check rather than being assumed read-only. No input values
or document text are added to the metadata cache. These facts are refreshed with
each snapshot, including after editor read-only state changes.

## Regression checks

Completed-response reconciliation also handles a final opening streamed after
earlier working updates in the same response, including when the completed answer
inserts a new introduction ahead of it. Alignment normally requires at least two
exact trailing sentence units matching a consecutive run in the completed answer. New
introductory units precede any matching units that are still unread. Queue positions
then preserve spoken or unread matching units while removing superseded working
updates; an unstarted earlier thought is cancelled. When only one final sentence
streamed, it can anchor the handoff only if it has at least six words, ends in
sentence punctuation, occurs once in each body, and matches both the trailing
draft unit and the first final unit in the same known response. Short phrases,
ambiguous occurrences, and single matches elsewhere remain ineligible. Run
`--completed-revision-checks` for the spoken, paused and preparing handoff cases.

Run the deterministic modifier-input, hotkey, configuration, rendering, text-preparation, and speech fallback checks with:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release
```

Run the live UI Automation monitoring smoke test with:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --window-monitoring-smoke
```

For an interactive review window with selectable opening text, manually appended sentences, and a token-by-token stream, run:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --window-monitoring-manual-provider
```

## Windows speech smoke test

Run the opt-in installed-voice, WAV synthesis, pause, resume, and stop check with:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --windows-smoke
```

This check briefly plays a Windows speech test phrase.

## Kokoro smoke tests

Run the opt-in model, synthesis, bounded playback, pause, resume, and stop check with:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --kokoro-smoke
```

This command downloads the model if needed and plays a few short test sentences.

Run the warm-cancellation latency and memory-reporting check with:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --kokoro-cancellation-smoke
```

The cancellation check keeps one Kokoro model instance loaded, uses silent playback, cancels a long selection during later-segment inference, verifies that speech can restart promptly, and reports process memory before and after cancellation.

Pulse Reader follows KokoroSharp's original segmentation policy, up to 200 tokens for the first segment and 510 thereafter. Listening tests found it more fluid than the experimental 100-token policy.

## Text-preparation listening corpus

Generate the local `af_heart` original-versus-prepared corpus with:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --text-preparation-ab artifacts\text-preparation-ab
```

The generated `index.html` contains side-by-side text and audio for the accepted preparation policy and its preserve-original controls. Git ignores these artifacts.

Normal Kokoro playback applies the accepted text-preparation policy before synthesis. This includes explicit ellipsis pauses and conservative shortening of recognised opaque identifiers. Windows speech and automatic Windows fallback receive the original selected text. See the [pronunciation review notes](pronunciation-notes.md) for accepted dictionary entries and listening outcomes.

Calendar preparation recognises valid `yyyy-MM-dd` dates and day/month names with an optional year. It adds an ordinal day but no leading article, preserving any existing "the" in the sentence. Three-part versions such as `1.21.7` use explicit point separators; ordinary decimals and IP addresses are left to the existing policy. Drive-letter colons are spoken for folders and roots as well as recognised files. Long digit runs embedded in technical tokens are spelled out to avoid a Misaki number-conversion exception that can otherwise trigger Windows fallback. This safeguard also applies to tables and JSON; standalone quantities keep their existing treatment. Source text and navigation ranges are unchanged.

Run `--spoken-text-checks` with the input-sequence test executable for the focused formatting and segmentation suite. Run `--pronunciation-edge-audio artifacts/pronunciation-text-fixes/listening` to generate the date, version and folder A/B pairs plus the previously failing paragraph. That paragraph has only a corrected Kokoro preview: its previous Kokoro wording throws, so no Windows recording is presented as an A sample. Listening approval is separate from automated checks.

Paired feet/inches notation such as `12' 6"`, `12'6"`, `12'6`, and `12' 6` expands to “12 feet 6 inches.” Smart quotes and prime marks also work. The inches component must be from 0 to less than 12; decimal inches and singular foot/inch forms are supported. Lone quotation marks and degree-marked coordinates are not treated as height measurements. Generate the short listening round with `--feet-inches-ab <output-directory>`.

## Local cleanup model evaluation

The broad local-model rewriting harness remains isolated from normal playback. It sends one prepared reading unit at a time to a CPU-only llama.cpp server, applies conservative content guards, measures load, warm response, memory, cancellation, and shutdown behaviour, then generates original, deterministic, and model-assisted `af_heart` audio.

Provision the pinned portable runtime and default 0.6B model with:

```powershell
tools\setup-local-text-cleanup.ps1
```

Generate the review corpus with:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --local-text-cleanup-evaluation artifacts\local-text-cleanup\review
```

The setup script also accepts `qwen3-1.7b`, `qwen3-4b-instruct-2507`, `qwen3.5-4b`, `qwen3.5-9b`, `qwen3.8-2b-distill`, `qwen2.5-1.5b`, `qwen2.5-3b`, and `smollm2-1.7b` for controlled comparisons. Pass the downloaded filename with `--local-text-cleanup-model` when running the harness. Qwen2.5 3B is evaluation-only and uses the Qwen Research licence; it is not a distribution candidate. The upstream [Qwen3 4B Instruct 2507 model](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507) uses Apache-2.0, and the setup script pins the [LM Studio community Q4_K_M conversion](https://huggingface.co/lmstudio-community/Qwen3-4B-Instruct-2507-GGUF) by repository revision and SHA-256.

Run the narrower context-classification evaluation with:

```powershell
tools\setup-local-text-cleanup.ps1 -Model qwen3-4b-instruct-2507
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --local-targeted-model-evaluation artifacts\local-text-cleanup\focused\qwen3-4b-balanced-final --local-text-cleanup-model Qwen3-4B-Instruct-2507-Q4_K_M.gguf
```

The focused harness asks the model to choose a closed sense label for a marked ambiguous word. Every target is balanced between its two labels. The original four targets have four cases per label; `live` has five per label because listening exposed four mixed-pronunciation sentences as well as the standalone phrase `go live`. The prompt contains no worked examples from that corpus, and label definitions do not repeat scored sentences. The report records the accepted semantic label and separately checks the exact-label output protocol. Production uses the same per-occurrence contract, and deterministic code owns every pronunciation substitution.

Treat the classification score as a result for this small diagnostic corpus, not as general accuracy. A close score difference between models is not enough to rank them. The transformation probes also have a narrower meaning than their output may suggest. They check whether a strict matcher can find a short list of protected terms. Passing that check does not establish that the model preserved every fact, relationship, or ordering detail.

Each run writes the generation time in UTC, model SHA-256, llama.cpp version, inference thread count, evaluator revision, and a SHA-256 fingerprint of the evaluator, prompt, and protected-term matcher sources. The Markdown report HTML-escapes model output. Memory is reported as the maximum value sampled at specific checkpoints, not as process peak memory. Cancellation data covers client cancellation completion, a later health check, and a follow-up request. It does not measure the exact time when llama.cpp releases its inference slot.

The six-model comparison below was run on 29 August 2026 with the 32-case v2 corpus, llama.cpp build 10662, eight CPU threads, and evaluator source fingerprint `c681909e084c19032f9b84d0d2b5833bd0f6f61dd0072f7c43012d7ff6de35af`. Classification is the number of correct closed-label choices. Exact is the number returned without additional text. Transform is the number of caller-directed outputs in which the matcher found every listed protected term; it is not a full-fidelity score. The later `live` supplement changes the corpus and evaluator fingerprint, so its result is recorded separately rather than compared directly with these rows.

| Model | Reasoning | Classification | Exact | Transform | Warm median / P95 | Memory after load / maximum sampled |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Qwen3 4B Instruct 2507 | 0 | 27/32 | 32/32 | 1/3 | 383 / 614 ms | 2,191 / 2,406 MB |
| Qwen3.5 4B | 0 | 27/32 | 32/32 | 2/3 | 990 / 1,320 ms | 2,016 / 2,905 MB |
| Qwen3.5 9B | 0 | 27/32 | 32/32 | 3/3 | 1,937 / 2,365 ms | 3,319 / 4,209 MB |
| Qwen3.8 2B Distill | 0 | 18/32 | 32/32 | 1/3 | 462 / 548 ms | 1,209 / 1,553 MB |
| Qwen3.8 2B Distill | 64 tokens | 26/32 | 28/32 | 2/3 | 2,268 / 2,443 ms | 1,208 / 1,719 MB |
| Qwen3.8 2B Distill | 128 tokens | 28/32 | 30/32 | 2/3 | 3,787 / 4,155 ms | 1,206 / 1,799 MB |

The v3 supplement added ten balanced `live` cases drawn from listening failures and controls. Qwen3 4B classified all 10 `live` cases correctly and returned the exact label for all 42 corpus cases. Its complete v3 result was 36/42 classifications, 42/42 exact responses, and 1/3 transformation probes, with a 476 ms warm median and 797 ms P95. The run used evaluator source fingerprint `cff79e72e3079ad74fe5522a05c8da7463522ec58ed7e2cdf3df2c91f5dbf42f`. Mixed-pronunciation sentences required the harness to mask other identical spellings while classifying the requested occurrence; without that isolation, the other `live` occurrence biased four of the five reside-or-remain-alive decisions. Production retains that masking rule.

All six retained runs canceled the client request within 6 ms, passed the later health check, and released both server processes after shutdown. The follow-up request after cancellation still took 2.2-9.8 seconds, so client cancellation must not be described as immediate inference-slot release.

For the optional closed-label classifier, Qwen3 4B Instruct 2507 remains the practical baseline: it followed the exact-output contract in every case, matched both larger models on this corpus, and had substantially lower warm latency. Qwen3.8 2B at 128 reasoning tokens scored one extra case but was roughly ten times slower at the warm median and produced two non-exact responses. The 9B result does not justify its added latency and memory for classification. Its 3/3 protected-term result is useful evaluation evidence, but it does not establish safe general rewriting and is not a recommendation to add model-directed rewriting to Pulse Reader.

The evaluation commands keep runtime archives, model files, generated WAVs, logs, and reports under the ignored `artifacts/` directory. An explicitly supplied output path is honored, so keep custom output paths inside ignored storage when the generated files should remain untracked.

Production contextual pronunciation uses only Qwen3 4B Instruct 2507. Install its pinned runtime and model with `tools\setup-contextual-pronunciation.ps1`. Run the real installation, classification, control, and shutdown path with:

```powershell
dotnet run --project tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release -- --contextual-pronunciation-smoke
```

Normal regression tests use an in-process fake classifier. They cover the approved target scope, selections above eight occurrences, per-occurrence replacement, target-local context windows, reading-unit progress, disabled mode, exact-label rejection, timeout and cancellation cleanup, healthy-memory retention, pressure-driven release, and restart without loading the 2.5 GB model. The real-model smoke path checks the full accepted `live` corpus occurrence by occurrence, including mixed-sense sentences. The Kokoro smoke path verifies that unaffected opening audio starts immediately, each resolved contextual unit is appended while a later unit remains pending, and the same playback session preserves navigation and cancellation throughout.

## README screenshots

Regenerate the README images from the current playback and Settings renderers with:

```powershell
dotnet build tests/PulseReader.InputSequenceTests/PulseReader.InputSequenceTests.csproj --configuration Release --artifacts-path artifacts\readme-screenshots-build -m:1
& artifacts\readme-screenshots-build\bin\PulseReader.InputSequenceTests\release\PulseReader.InputSequenceTests.exe --readme-screenshots artifacts\readme-screenshots
```

The command writes individual renders for the playback controls, notification area menu, and Settings window, then composes them into the wide interface overview used by the README. The build and images stay under ignored `artifacts/` storage so regeneration does not overwrite a running source build or the reviewed documentation images.

The Settings image uses `AppConfiguration.Default` and `HotkeyDefinition.Default`, with the optional classifier shown as installed but disabled. The tray preview shows the packaged application's idle update action. These render-only fixtures do not launch Pulse Reader or read saved settings. Inspect the generated images before copying the four PNG files into `docs\assets`.

## Publish for Windows x64

Create a self-contained build that does not require a separately installed .NET runtime:

```powershell
dotnet publish src/PulseReader/PulseReader.csproj -p:PublishProfile=win-x64 --output artifacts/PulseReader-win-x64
```

Distribute the complete `artifacts/PulseReader-win-x64` folder. Copying only `PulseReader.exe` will not work. The folder contains the Kokoro v1.0 voice files, while the model remains under `%LOCALAPPDATA%` and is not published with the application.

The publish profile leaves single-file bundling, trimming, and ReadyToRun disabled. A self-contained build carries its own .NET runtime, so rebuild it with an up-to-date .NET 10 SDK to pick up runtime security and servicing updates.
