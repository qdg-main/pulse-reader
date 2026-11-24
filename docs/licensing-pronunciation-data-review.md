# Pronunciation data redistribution review

Reviewed 2026-10-13 for the release owner. This report supports the decision to
redistribute the data embedded in MisakiSharp 2.1.1, pinned to
`4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06`. It records publisher declarations and
the inspected transformation paths. It does not decide the separate LGPL/GPL
code-port review or certify every contributor's rights chain.

The wordfreq notice gate can be closed through explicit attribution and a
scoped CC-BY-SA data notice. The exact generator package version remains
unrecorded, but that fact alone does not prevent this route. Chinese data also
needs the CC-CEDICT and Unicode notices in addition to the MIT wrapper notices.
Zdic publishes a CC0 declaration, so its presence does not require contacting
the maintainer merely to obtain a public redistribution grant.

This report proposes the data notice scope and preservation requirements. The
release owner must integrate them into the distributed notices and source
inventory before marking that work complete.

The completed source-level check supports retaining the exact pronunciation
resources while replacing the separately identified GPL/LGPL code paths.
MisakiSharp publishes the project and its generated tables under Apache-2.0,
subject to the underlying data notices identified here. The check found no
concrete conflicting declaration for the generated eSpeak tables and did not
identify protected eSpeak source expression copied into them. Dictionary-backed
output remains a documented rights uncertainty, not an additional maintainer
permission gate. This is an evidence-based release classification, not a
certification of every contributor's rights.

## What the pinned package embeds

The pinned [project file](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/MisakiSharp.csproj)
lists twenty embedded resources. The following table covers the pronunciation
and English tagging paths reviewed here. Counts come from decompressing the
pinned resources, with lexicon, bigram and LTS sections counted separately.
They are output counts, not the number of wordfreq input entries.

| Resources | Inspected output | Established input path |
| --- | --- | --- |
| `enus_espeak.tsv.gz`, `engb_espeak.tsv.gz` | 662,200 lexicon rows each; 135,766 and 128,355 LTS rows respectively | English wordfreq vocabulary, added symbols/numbers/case variants and eSpeak-NG pronunciation probes |
| `es_espeak.tsv.gz` | 518,891 lexicon rows; 344 bigrams; 24,760 LTS rows | wordfreq, optional Wikipedia vocabulary harvest, Wikipedia bigrams and eSpeak-NG probes |
| `fr_espeak.tsv.gz` | 509,728 lexicon rows; 5,929 bigrams; 91,717 LTS rows | Same pipeline for French |
| `it_espeak.tsv.gz` | 502,724 lexicon rows; 577 bigrams; 62,163 LTS rows | Same pipeline for Italian |
| `pt_espeak.tsv.gz` | 455,689 lexicon rows; 35,458 bigrams; 46,041 LTS rows | Same pipeline for Portuguese |
| `hi_espeak.tsv.gz` | 228,824 lexicon rows; 7,388 bigrams; 68,065 LTS rows | Same pipeline for Hindi |
| `hi_espeak_lexicon.tsv.gz` | 160,632 ordinary lexicon rows, plus 421 number-table rows | Hindi and English wordfreq vocabulary, Hindi Wikipedia titles/articles, fixture tokens and eSpeak-NG probes |
| `en_lexicon_us.tsv.gz`, `en_lexicon_gb.tsv.gz` | 365,368 and 392,464 entries respectively, in GOLD/SILVER sections | Misaki 0.9.4 English JSON dictionaries, expanded through `Lexicon.grow_dictionary` |
| `en_tagger.bin.gz` | `KTAG1` binary with 49 POS classes | Averaged perceptron trained on spaCy `en_core_web_sm` labels, then pruned and quantized |
| `en_tokenizer.tsv.gz` | Converted tokenizer rules and exceptions | spaCy English tokenizer data |
| `zh_g2p.tsv.gz`, `zh_g2p_v2.tsv.gz` | Converted pinyin and jieba dictionary data | pypinyin, pypinyin-dict `large_pinyin`, Misaki overrides and jieba |

Resource headers name Misaki 0.9.4 or eSpeak-NG 1.52.0. They do not record a
wordfreq version. The tagger header records its binary format, not its spaCy
model version or training environment.

## What the wordfreq extraction does

[`generate_espeak_en.py`](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/generators/generate_espeak_en.py)
requests `top_n_list('en', 500000, wordlist='large')`, excludes whitespace
entries, reserves 5,000 held-out words using a fixed random seed, adds case
variants, numbers and symbols, and asks eSpeak-NG for pronunciations. Its emit
stage combines available dump variants, removes redundant uppercase rows and
sorts the retained keys before embedding the lexicon and LTS tables. The input
frequency values are not reproduced as frequency columns. The selected word
keys remain in the distributed table.

[`generate_espeak_native.py`](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/generators/generate_espeak_native.py)
requests up to `10 ** 9` wordfreq entries for each of Spanish, French, Italian,
Portuguese and Hindi. It adds numbers and case variants and can append a cached
Wikipedia vocabulary. It probes pronunciations in different contexts and
stores the word keys and pronunciation changes. This is a large extraction,
not a use of a few individual lookup results.

[`generate_espeak_lts.py`](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/generators/generate_espeak_lts.py)
builds grapheme, stress, onset and coda tables from those intermediate dumps.
The English generator also supports synthetic rule-probe dumps. Its final
emit stage selects an available LTS artifact without recording which training
inputs produced it. Treating the whole affected resource as covered by the
data notice avoids an unsupported claim that every LTS row has one particular
source.

[`generate_hi_native.py`](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/generators/generate_hi_native.py)
adds all available Hindi wordfreq entries and 15,000 English entries to its
vocabulary. It also uses specified Hindi Wikipedia articles, the namespace-0
title dump and fixture tokens. The generator converts them to pronunciation
lookups and number tables.

## Why the exact wordfreq version remains unknown

The pinned tree contains no Python requirements file or lockfile. All four
commits reachable from the pinned MisakiSharp revision were inspected through
the public commit API, including their patches. The pronunciation generators
and resources enter in the 2.0.0 commit
`d16193d1df45cbbf6e7a8918756a092eaad9fe0f`; their imports do not pin wordfreq.
The later 2.1.0 and 2.1.1 commits do not add generation-environment records.
[The inspected history](https://github.com/Lyrcaxis/MisakiSharp/commits/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/)
therefore supplies no exact installed version.

The release-era upstream Misaki source at
`e820629b96334db28227df37f280e4836d46fadb` declares version 0.9.4. Its
[`uv.lock`](https://github.com/hexgrad/misaki/blob/e820629b96334db28227df37f280e4836d46fadb/uv.lock)
contains no wordfreq package. It pins other dependencies, including pypinyin
0.53.0 and pypinyin-dict 0.9.0, but does not prove what the MisakiSharp author
installed in the separate generator environment.

There is useful evidence that package-version ambiguity need not imply
different data. Public Git tree metadata shows identical data blob hashes at
wordfreq `v2.5.1`, `v3.0.2` and the retained attribution snapshot
`912caf64b657478d1dff1138efdc078947d54bb1`:

| Data file under `wordfreq/data/` | Git blob SHA shared by the three inspected revisions |
| --- | --- |
| `large_en.msgpack.gz` | `c754e8ecf6ae76ad2f7c9a47c69be26a26fa8c29` |
| `large_es.msgpack.gz` | `0ccf75b840797b05a1d9420728aac1d16857cdd6` |
| `large_fr.msgpack.gz` | `f5a0a7e72c78c1cbfab3007d2f9fa3dd5d147bf1` |
| `large_it.msgpack.gz` | `3a321efef0a7299a50da365c6a3f9916cf395f94` |
| `large_pt.msgpack.gz` | `d439c4580711f87698c9ba83edd102f13756d8c1` |
| `small_hi.msgpack.gz` | `357d54106bcb2fba3f0554f7d86f8e52edf9ae1b` |

The older [v2.5.1 README](https://github.com/rspeer/wordfreq/blob/v2.5.1/README.md)
and [v3.0.2 README](https://github.com/rspeer/wordfreq/blob/v3.0.2/README.md)
both declare CC-BY-SA-4.0 for data. They declare MIT for code, while the retained
newer snapshot declares Apache-2.0 for code. No wordfreq code is compiled into
MisakiSharp by these generators. The code-license change should not be
mistaken for a data-license change.

This proves equality of the inspected upstream blobs. It does not prove a
byte-level match between those input datasets and the author's environment,
and it does not identify the exact installed package. No dataset or model was
downloaded to attempt that match. Record the unknown version without inventing
a version based on the generator date or the attribution snapshot.

## A scoped data redistribution route

Wordfreq's retained [NOTICE](https://github.com/rspeer/wordfreq/blob/912caf64b657478d1dff1138efdc078947d54bb1/NOTICE.md)
declares its data freely redistributable under CC-BY-SA-4.0. Preserve the Robyn
Speer attribution, supplied copyright notice, data terms and underlying-source
credits, including the SUBTLEX authors and the statement that SUBTLEX is freely
available data. The README contains the full bibliographic credits referenced
by the notice, so retain those as well.

Under [CC-BY-SA-4.0 sections 3 and 4](https://creativecommons.org/licenses/by-sa/4.0/legalcode.en),
sharing covered material requires attribution, the supplied notices, a license
reference and a description of modifications. ShareAlike applies when covered
adapted material is shared. Section 4 separately addresses substantial database
extraction and reuse. Whether particular word lists, pronunciations or learned
tables attract copyright or database rights depends on the protected material
and applicable law. The practical conservative route is to meet the data
conditions for the listed resources instead of depending on an unproven
exemption for every row.

The data notice should say that the eight eSpeak pronunciation resources listed
above contain wordfreq-derived vocabulary, identify the upstream source and
the unrecorded generator version, and describe selection, case expansion,
pronunciation probing, filtering, sorting, compression and learned-table
generation. Apply CC-BY-SA-4.0 to the covered adapted data and any applicable
adapter rights. Do not describe the original wordfreq data as relicensed under
Apache or MIT. Do not claim this grants rights belonging to an unrelated
third-party source.

The scope should identify data resources even though they sit inside
`MisakiSharp.dll`. Retain the ability to extract and reuse the data and exclude
it from any application terms that would prohibit those licensed activities.
CC-BY-SA does not itself demand publication of application source code,
generator source, or original training corpora. Supplying the exact generated
data files is a useful way to make their scope and reuse clear, not a claim of
a CC corresponding-source requirement.

If the separate code review adopts a GPLv3 distribution route, Creative Commons
lists [GPLv3 as a one-way compatible adapter license for BY-SA 4.0](https://creativecommons.org/compatible-licenses/).
That option has its own conditions. It does not make the original data
GPL-only or remove attribution, and compatibility should not be assumed for
every older CC license.

## Other shipped data paths

### Wikipedia vocabulary and bigrams

The pinned
[`generate_espeak_corpus.py`](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/generators/generate_espeak_corpus.py)
extracts vocabulary from prefixes of five language Wikipedia dumps.
[`generate_espeak_bigrams.py`](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/generators/generate_espeak_bigrams.py)
extracts frequent pairs from those dumps and stores pronunciation exceptions.
The five final multilingual resources contain nonempty `##BIGRAMS` sections,
so this input path is present in the shipped files. It is separate from
Wikipedia's contribution to wordfreq.

Preserve an explicit Wikipedia contributor credit, the language editions and
dump links used by the scripts, their transformations, and the CC-BY-SA terms.
For the Hindi article input, the exact article titles remain in the generator
and can be retained with links to their history pages. The dump revision and
per-pair article provenance were not recorded. Individual tokens or short
pairs may be unprotected facts; that does not prove that every substantial
extraction has no database or other protected interest. Wikimedia's
[reuse terms](https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use#7._Licensing_of_Content)
describe contributor attribution and modification notices. Do not claim the
tables reproduce full articles or require publishing unrelated application
code.

### English lexicons, tokenizer and tagger

[`generate_en_dict.py`](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/generators/generate_en_dict.py)
reads Misaki 0.9.4's US/GB gold and silver JSON files, expands them through
`Lexicon.grow_dictionary`, preserves their sections and emits TSV. The
release-era [Misaki project declaration](https://github.com/hexgrad/misaki/blob/e820629b96334db28227df37f280e4836d46fadb/pyproject.toml)
and root LICENSE declare Apache-2.0; the inspected tree has no separate
lexicon license. Its public English dictionary history records dictionary
updates and corrections without an additional source-license declaration.
Preserve Misaki's Apache text, author attribution and the conversion notice.
No reviewed source establishes a separate English lexicon restriction that
would justify a new release gate. Exact original JSON input hashes remain
unrecorded in the converted resources.

[`generate_en_tagger.py`](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/generators/generate_en_tagger.py)
constructs a corpus from specified Gutenberg books, Kokoro demo text and
synthetic sentences. It asks `en_core_web_sm` for token labels, learns its own
perceptron, builds a frequent-word tag dictionary, then prunes and quantizes
the result. It does not export spaCy model tensors or full corpus sentences.
The reviewed [spaCy model metadata](https://github.com/explosion/spacy-models/blob/master/meta/en_core_web_sm-3.8.0.json)
declares MIT, but the generator does not record which model version was used.
Keep the spaCy/Explosion notice and this model distinction. The unrecorded
version limits reproducibility; it does not alone prove a missing data grant.
This review does not expand into the provenance of all spaCy training data or
claim that any book's full text ships inside the tagger.

### Chinese pinyin data

[`generate_zh_dict_v2.py`](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/generators/generate_zh_dict_v2.py)
calls `pypinyin_dict.phrase_pinyin_data.large_pinyin.load()` before collecting
pypinyin's phrase and character entries. The
[phrase-pinyin-data README at `cee0ed6e`](https://github.com/mozillazg/phrase-pinyin-data/blob/cee0ed6e6e4898580cafd2bd5e3723e20b214aa0/README.md)
states that `large_pinyin.txt` combines Zdic dictionary files, CC-CEDICT,
`pinyin.txt`, `di.txt` and manual corrections. The wrapper's MIT license is not
an explanation of every underlying source's terms.

[CC-CEDICT's publisher page](https://cc-cedict.org/wiki/)
declares CC-BY-SA-4.0 and credits its contributors and editors. Retain the
CC-CEDICT attribution, conversion description and applicable CC text for the
Chinese derived data. If an older CC-BY-SA-3.0 declaration is retained from an
upstream source snapshot, retain that text too and distinguish it from the
current publisher declaration. The exact CC-CEDICT input revision and its
embedded declaration are not recoverable from the inspected generated file.

[Zdic's terms](https://www.zdic.net/terms/) section 4 expressly declares CC0 1.0
for the site's words, characters and other stated content. Preserve the terms
snapshot and the CC0 declaration as source evidence. CC0 does not require an
attribution condition; a Zdic credit still records origin. The site's terms
also name reference dictionaries. The public dedication is a publisher grant,
not independent proof of every reference work's rights chain.

The [pinyin-data README](https://github.com/mozillazg/pinyin-data)
identifies Unihan fields among the character data sources. Unicode's
[data license](https://www.unicode.org/license.txt) requires its copyright and
permission notice in redistributed files or associated documentation. Preserve
that notice separately from the pypinyin MIT notice. A current attribution
snapshot is not evidence that the generator used the latest Unicode dataset.

### eSpeak-NG output

MisakiSharp's pinned [LICENSE](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/LICENSE),
[README](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/README.md)
and project license expression declare Apache-2.0. The project includes the
generated data under that declaration. Its NOTICE specifically identifies
GPL-derived `EspeakG2P`/`EspeakFallback` code behavior, but supplies no separate
GPL declaration for the generated replay resources. This publisher grant is
actual redistribution evidence for the publisher's rights in the tables. It
cannot override a conflicting third-party right, which must be assessed from
the retained material and upstream terms.

The inspected eSpeak-NG 1.52.0 [README](https://github.com/espeak-ng/espeak-ng/blob/1.52.0/README.md)
declares GPL version 3 or later. The checked
[`en_rules`](https://github.com/espeak-ng/espeak-ng/blob/1.52.0/dictsource/en_rules)
file also carries a GPL header. Its rule directives, matching conditions and
comments are eSpeak source material. The
[`dictionary.md`](https://github.com/espeak-ng/espeak-ng/blob/1.52.0/docs/dictionary.md)
documentation distinguishes rule processing from explicit `*_list` and
`*_extra` pronunciation lookups, including lookups after stripping affixes.
The API output can therefore reflect dictionary entries; conversion to IPA
alone does not establish that it lacks protected expression.

The pinned generators call `espeak_TextToPhonemes`, then store pronunciations
and measured changes under independently selected word keys. They do not read
or export eSpeak dictionary source files, compiled dictionary binaries or rule
definitions. `generate_espeak_lts.py` aligns returned phonemes with letters,
selects frequent emissions and stress patterns, and prunes redundant contexts
to produce its own learned tables. The English synthetic-probe path likewise
queries outputs; the final file does not identify which available LTS input
was selected. Do not describe the learned sections as verified independent of
every dictionary-backed probe.

All eight eSpeak-derived resources were decompressed for the follow-up check.
Their headers and sections match generated word-pronunciation entries,
context deltas, bigram exceptions, learned LTS tables and Hindi number slots.
The scan found no retained source comments, `.group`/`.replace`/`.Lnn` rule
directives or `$alt`/`$textmode`/`$phonememode` dictionary syntax. It identified
no separate GPL declaration in those resource headers. This corroborates the
generator path; it is not a proof that every possible form of protected
expression is absent. The English and standalone Hindi row recounts include
the ordinary `#` symbol key, which the earlier count incorrectly treated as a
comment.

The exact upstream [GPL section 2](https://github.com/espeak-ng/espeak-ng/blob/1.52.0/COPYING)
and GNU [output guidance](https://www.gnu.org/licenses/gpl-faq.html#WhatCaseIsOutputGPL)
tie output coverage to protected content in the output. They do not impose GPL
on every result of running the program. No inspected term specifically
declares these generated tables GPL, and the source check did not establish a
copy of protected eSpeak dictionary expression that conflicts with the
publisher's Apache grant. Ordinary pronunciation mappings can convey facts;
that observation alone does not resolve rights in every selection, variant or
learned table.

For this release, retain the exact data and the scoped input notices described
above. Do not require a new upstream permission solely because eSpeak produced
the pronunciations or because a complete rights-chain guarantee is unavailable.
Record the dictionary-content uncertainty without declaring all rows
unrestricted or all assets MIT. The separately identified copied GPL/LGPL code
still requires its own replacement or compliant distribution decision.

## Files and notices to preserve

For the release data package and notices, retain:

- The exact generated resources named in the data notice, with their hashes
  and embedded resource names. Keep the original resource files available as
  data, alongside their DLL representation if the chosen package provides it.
- MisakiSharp LICENSE and NOTICE; the original Misaki Apache license and
  author attribution; complete spaCy/Explosion MIT notices; the relevant
  pypinyin, pypinyin-dict, phrase-pinyin-data and pinyin-data MIT notices.
- `wordfreq-LICENSE.txt`, `wordfreq-NOTICE.md`, `CC-BY-SA-4.0.txt`, the wordfreq
  README's underlying-source bibliographic credits, and an affected-resource
  notice that records the transformations and unconfirmed generator version.
- Wikipedia contributor, language-edition, dump and Hindi article-source
  credits; applicable CC text; and the transformation description.
- CC-CEDICT creator/contributor credits and applicable CC texts; Unicode's
  complete data permission notice; the Zdic CC0 declaration and source URL.

For the audit record and any code/source delivery required by the separate
GPL/LGPL review, preserve the pinned source archive, all generators, the
project's embedded-resource declarations and the original generated files.
The upstream input JSON files, generation caches and exact Python environment
were not supplied as provenance records. Do not invent them or claim that the
retained generator scripts provide a fully reproducible generation build.
CC-BY-SA alone does not require procuring those absent files.

The research cache is under `artifacts/license-source-audit/data/`. It contains
the pinned generator scripts, source trees, all four MisakiSharp commit
details, release-era Misaki declarations and lockfile, wordfreq historical
README/license texts and tree hashes, and the primary CC, Wikimedia, Zdic,
Unicode and GNU guidance retrieved for this review. Those caches support this
report; they are not a claim that their current snapshots were the original
generation inputs.

The 2026-10-13 follow-up retains six exact eSpeak-NG 1.52.0 primary-source files
and its classification record under
`artifacts/permissive-replacement-implementation/data/`. That record documents
the resource hashes, inspected output shapes and the limits of the source
syntax scan.

## Closure decision and remaining questions

The wordfreq package version, Wikipedia dump date, spaCy model version and
pypinyin input revisions remain factually unresolved. They should stay visible
in the inventory as provenance limitations. The reviewed publisher grants and
the conservative scoped data route do not require treating each missing
version as a release blocker or contacting a maintainer before adding the
notices.

Close the wordfreq/data-notice gate once the named resources, required credits,
modification statements and CC terms are present in the actual release
payload. Close the missing Chinese data-notice work once CC-CEDICT and Unicode
are included alongside the MIT wrappers and Zdic evidence. The release owner
must validate the final payload rather than relying on this report's presence.

The data source check is complete for the retained-resource route. It found no
concrete data-license conflict that requires blocking the permissive code
replacement or obtaining new maintainer permission. This conclusion supersedes
the earlier research suggestion that table-specific clarification must precede
release merely because dictionary-backed output might involve protected
material. Missing generation records and the unproven extent of any dictionary
rights remain visible limitations. A blanket claim that all tables lack
protected expression would exceed this review.

Keep the separate code derivation and replacement/rebuild validation open until
that work is complete. Data-notice integration and validation of the actual
release payload also remain delivery work. No external contact was made in
this audit.
