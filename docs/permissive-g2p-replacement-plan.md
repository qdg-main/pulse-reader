# Permissive G2P replacement investigation

This report supports the Pulse Reader 1.0.0 dependency decision. Initial research
12 October 2026; implementation updated 13 October 2026. It covers replacements for the copied number conversion and
punctuation code in MisakiSharp 2.1.1 while preserving existing speech.
Experiments ran separately under `artifacts/permissive-replacement-research`.
The source build now uses the modified local frontend instead of the stock
runtime DLL. Voices, models, data bytes and the running Release remain unchanged.
No maintainer has been contacted. Public release remains gated on acceptance.

The maintainer completed the ten paired listening examples on 13 October 2026
and reported that all sounded the same. This closes that listening acceptance
step, not the remaining installer/clean-profile or publisher release gates.

## Implementation result, 13 October 2026

The pinned source project is `src/MisakiSharp.PulseReader`. It removes the
reviewed number class, punctuation restoration/normalization bodies and generated
regex helpers. New MIT-compatible implementations preserve the existing public
interface and all twenty resource bytes. A direct excluded-assets package
reference plus project reference prevents the stock transitive DLL from supplying
the runtime frontend. Package checks inspect actual assembly metadata/resources.

- Number fixture: 50,342 exact output/exception comparisons, with no exclusions
  or mismatches. This includes the previous 32,204 baseline, 7,523 floating-point
  cases, 615 huge integer cases and 10,000 negative years. The magnitude ceiling
  remains below `10^306` and observed binary-fraction rounding is preserved.
- Punctuation fixture: 48,876 exact output/provider-boundary comparisons,
  including randomized Unicode, malformed UTF-16, annotation and empty-output
  cases. No mismatches or unexpected exceptions occurred.
- Assembled compatibility: 193 distinct source texts, raw and app-prepared,
  produce 772 matching US/GB phoneme/token comparisons. Fourteen multilingual
  comparisons also match. All twenty resources match by SHA-256, and removed
  helpers are absent from the replacement assembly.
- Full InputSequenceTests regression suite passes in the normal desktop context.
  The restricted context could not register a test hotkey; that failed run
  retained its assertion and nonzero exit. It was not treated as a pass.
- Ten audio pairs use identical input, voice/model/runtime dependencies and
  phonemes/tokens. Waveforms are not byte-identical. A repeat using the unchanged
  baseline also differs. Paired durations differ by at most nine samples at
  24 kHz; this does not replace listening acceptance.

The listening page and raw reports are local artifacts under
`artifacts/permissive-replacement-implementation/listening/output`. Source-level
data classification now supports retaining the generated resources with scoped
notices; it supersedes the earlier suggestion that hypothetical dictionary
content concerns automatically require a new permission request. See the
[updated data review](licensing-pronunciation-data-review.md).

The following sections retain the initial research and proposed qualification
matrix. Their earlier exclusions describe the research adapters, not the
implemented C# converter. The running Release has not been replaced.

## Recommended approach

Use a small, pinned replacement library with the existing public interface,
lexicons, IPA mappings, tokenizer/tagger and voice/model configuration.
Change only the reviewed code paths initially. Preserve all currently exposed
languages. An English-only fork or disabled fallback would remove capability
and does not meet this task's goal.

For English numbers, port the MIT
[number-to-words 1.2.5 source](https://github.com/marlun78/number-to-words/tree/02b3cc45829fbe21d8166fcf1df0d5b33e616b7e)
by Martin Eneqvist, with recorded British-wording, year and decimal adapters.
Retain its complete [MIT notice](https://github.com/marlun78/number-to-words/blob/02b3cc45829fbe21d8166fcf1df0d5b33e616b7e/LICENSE)
and document the C# port's changes. Node is used only for this investigation;
the proposed product would not install or invoke Node.

For punctuation, use newly authored source-offset spans and a forward
assembler built with .NET's existing text APIs. The research prototype retains
literal slices and sends only speech slices to the provider. It does not
translate the original mutable-list restoration algorithm or its position
tags. Reimplement raw-provider normalization from its observed contract and
source the bounded IPA/dialect mappings directly from the Apache misaki wrapper.
The Apache wrapper itself delegates punctuation to phonemizer, so importing it
wholesale would not provide the missing permissive implementation.

Tests compare observable output; they are not a formal clean-room process or a
legal determination of independence. The investigation has seen the original
lineage review. Retain source boundaries and review actual replacement code
before assigning release-ready status.

## What the experiments establish

The baseline calls the actual MisakiSharp DLL through reflection, pinned to
SHA-256 `F90EF27EE2D001B15B31270003D2310297847D2B44F7DC2665150F014F9EFE0D`.
It contains no copied converter body. It observes 32,204 numeric method calls
and tests synthetic provider behaviour separately from actual replay samples.

| Research comparison | Exact matches | Limits |
| --- | ---: | --- |
| MIT converter with British cardinal wording | 10,531 / 10,531 | Inputs within JavaScript's safe-integer range |
| MIT converter with British ordinal wording | 10,531 / 10,531 | Same range; no excluded input counted as a pass |
| Observed year policy | 10,000 / 10,000 | Values 0 through 9999, covering parsed four-digit inputs |
| Observed decimal policy | 22 / 22 | Supplied .NET round-trip text; not all floating-point values |
| New punctuation planner/normalizer | 8,200 / 8,200 | Exact seam output and provider-call boundaries, not full public G2P output |
| Existing replay providers with new punctuation seam | 42 / 42 | Three samples per en-us/en-gb/es/fr-fr/it/pt-br/hi provider, both flag modes |

Integer inputs include every value 0-10,000, seeded larger quantities, negatives
and scale boundaries. The MIT candidate's British adapter preserves `and`,
commas and hyphens exactly in the compared range. Its core remains the unchanged
pinned MIT source; the adapter processes that source's output groups rather than
porting num2words.

There are 195 integer inputs per cardinal/ordinal method outside the candidate's
safe range, plus 726 year inputs outside the four-digit scope. Those are
unqualified, not successes. The existing library accepts BigInteger values and
has many larger scale names; a C# port must preserve that contract or obtain an
explicit decision about a changed range. Strict parsing must also replace the
JavaScript API's partially permissive string parsing.

Four float cases are explicitly excluded: `5E-324`, `1E+19`, `NaN`, and
`Infinity`. The first and last two throw in the old library; the research does
not count matching an unsupported case as parity. The decimal adapter consumes
the baseline's textual representation. A C# implementation still needs tests
for obtaining that representation from doubles, exponent expansion, rounding,
negative subunit values and scale limits. The old `-0.5` loses its minus sign;
the experiment preserves that observation rather than silently fixing it.

Punctuation fixtures cover leading/internal/trailing marks, repeated marks,
empty/whitespace-only input, empty provider output at different positions,
ties, line endings, multiple spaces and language annotations. Independent
review found an initially missed annotation edge, `() (en)`, where the old
normalizer consumes more than a narrow language-code parser would. Added
fixtures now match it. The runner fails nonzero on output/boundary mismatches
or unexpected original-provider exceptions; all 8,200 comparisons completed
without either. Actual replay samples reuse unchanged data/providers and do
not qualify replacement data.

No audio synthesis or listening test was performed. Exact intermediate matches
are promising for preserving speech, but a full replacement DLL must also match
public G2P outputs, final Kokoro tokens, segmentation/navigation boundaries and
synthesis. These measurements do not prove all unseen text sounds unchanged.

## Candidates not selected

Humanizer 2.14.1 has useful cardinal behaviour, but its
[first English converter commit](https://github.com/Humanizr/Humanizer/commit/ad618195dab5c6ec8f37d968e554441a2e8bb96b)
cites and incorporates an older Stack Overflow implementation. The
[Stack Overflow contribution policy](https://stackoverflow.com/help/licensing)
assigns share-alike terms to that contribution. No separate permissive grant
was established. Its MIT repository label alone does not resolve this origin,
and its ordinal wording differs from the baseline. Hold it out of this route.

The Apache `humanwhocodes/number-to-words` candidate lacks ordinal, negative,
year and decimal APIs and matches less of the observed wording. It provides no
advantage over the tested MIT candidate for this task.

No ready-made permissive punctuation replacement was qualified. Adding a
parser package would still require the compatibility policy and would introduce
another dependency. The small span-based implementation is the preferred path.

## Initial pronunciation-data question (superseded 13 October)

This section records the initial uncertainty, not a current permission gate.
The subsequent source-level check found no concrete conflicting output term or
identified protected eSpeak source copying in the shipped tables. The current
route retains their exact bytes and scoped notices; see the updated data review
for the evidence and residual uncertainty. No upstream inquiry is required by
the findings recorded here.

Retaining existing tables is the strongest technical option for preserving
speech across all languages. Scoped CC-BY-SA data obligations can remain
separate from permissive application code. They need not all become MIT.
However, those notices do not grant rights to unrelated eSpeak dictionary
material.

The public eSpeak documentation describes explicit word-pronunciation entries
as well as rules. No inspected public statement settles the bulk-table
redistribution question. Its
[phoneme-output discussion](https://github.com/orgs/espeak-ng/discussions/2252)
was unanswered during this review. Running GPL software does not automatically
make its output GPL, and absence of an exception does not establish that all
output is protected either. This investigation does not decide that legal issue.

Replacing English fallback with CMUdict's BSD-style data or an Apache G2P
candidate is possible, but changes dialect/coverage/stress and does not supply
an equivalent multilingual replacement. Disabling fallback is also a speech
regression. Do not substitute these merely to claim the licensing task is done.

The initial research suggested a possible narrow inquiry describing
eSpeak-NG 1.52.0 phoneme-table extraction,
dictionary-backed entries, languages, transformations and the fact that no
eSpeak binary or compiled dictionary ships. Ask for the applicable public
data/output terms, not general permission to use GPL software. The research
cache contains a draft; none was submitted. A generic permissive declaration
from a downstream maintainer cannot waive upstream rights.

## Initial implementation and acceptance plan

Steps 1–5 are implemented and qualified by the checks above. Final listening
acceptance and package/clean-profile validation remain; step 6's data question
is superseded by the source-level review, while Microsoft distribution terms
remain a separate release gate.

1. Build an isolated C# library prototype using the pinned MIT number source
   and new punctuation code. Complete numeric range/float cases before calling
   it a compatible replacement. Record source pins, retained notices and changes.
2. Remove the old number class and all reviewed punctuation/normalization code
   from compilation, including generated regex helpers. Assess native/process
   provider wrappers separately. Calling new methods while retaining the copied
   code in the shipped assembly is not removal.
3. Keep the existing public interfaces and test US/GB dialect/flap behaviour,
   null/empty results, IPA mappings and every affected language. Japanese and
   Chinese must also remain covered by integration checks even though the
   changed replay seam does not serve their normal frontends.
4. Compare public phonemes, final token IDs and segmentation on existing
   pronunciation, formatting, long-text and navigation corpora. Synthesize
   focused A/B samples only where outputs differ or integration requires it.
5. Explicitly prevent KokoroSharp's transitive stock MisakiSharp package from
   returning to the final payload. Update reviewed source/DLL pins and notices
   only after the replacement passes qualification. Inspect the shipped
   assembly, not just the source or selected call path.
6. Resolve the remaining data determination and Microsoft distribution terms
   before clearing public release. This investigation does not change those gates.

Raw evidence, exact candidate downloads, source hashes and probes are under
`artifacts/permissive-replacement-research/{baseline,numbers,punctuation,data}`.
Those are local ignored research artifacts, not published release assets.

To reproduce the baseline and punctuation/replay comparison, run from the
`artifacts/permissive-replacement-research/baseline` directory:

```powershell
dotnet run --project Baseline.csproj --configuration Release -- 'P:\Code\Pulse-Reader\src\PulseReader\bin\Release\net10.0-windows'
```

From the repository root, run the number adapters:

```powershell
node artifacts/permissive-replacement-research/numbers/prototype/british-adapter.cjs artifacts/permissive-replacement-research/baseline/numbers-baseline.json
node artifacts/permissive-replacement-research/numbers/prototype/year-decimal-adapter.cjs artifacts/permissive-replacement-research/baseline/numbers-baseline.json
```

Mismatch checks return nonzero and retain reports; unsupported cases stay
listed separately. These commands use the existing SDK/Node and pinned local
sources, without installing packages or starting Pulse Reader.
